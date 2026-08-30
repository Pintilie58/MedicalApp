using MedicalApp.Models;
using System.Text;
using System.Text.Json;

namespace MedicalApp.Services
{
    /// <summary>
    /// SPLIT PIPELINE — the same interpretation, produced by three calls whose
    /// natures are different, instead of one monolithic call that pays for
    /// "thinking" while transcribing a table.
    ///
    ///   Stage A — EXTRACTION   : the analyte table, no prose, thinking=low.
    ///   Stage B — EXPLANATIONS : per-analyte texts, in PARALLEL batches, thinking=minimal.
    ///   Stage C — NARRATIVE    : summary / correlations / recommendations on a
    ///                            strong model with REAL thinking — its input is
    ///                            only the extracted table, so it costs very little.
    ///
    /// B and C run concurrently, so the wall clock is A + max(B, C) instead of
    /// the sum of everything. Enabled with Gemini:PipelineMode = "split";
    /// any failure falls back to the monolithic call (see CallGeminiAsync).
    /// </summary>
    public partial class GeminiMedicalInterpretationService
    {
        /// <summary>
        /// Per-stage milliseconds / tokens of the last split run, merged by the
        /// controller into the request's StageTimer so the Admin performance
        /// panel can show where the seconds and the cents went.
        /// </summary>
        public Dictionary<string, long> LastStageTimings { get; } = new();

        /// <summary>
        /// Human-readable list of the models that actually produced the last
        /// interpretation. Empty on the monolithic path (the caller already knows
        /// the model there). Persisted so the Admin panel stops showing the
        /// configured primary model for a run made by three other models.
        /// </summary>
        public string LastModelsUsed { get; private set; } = "";

        /// <summary>
        /// "split: 2.5-flash|2.5-flash|2.5-pro" — the three stage models without
        /// the "gemini-" noise, capped at the 40 characters of the DB column.
        /// </summary>
        private string ShortModels()
        {
            static string S(string m) => m.Replace("gemini-", "", StringComparison.OrdinalIgnoreCase);
            var label = $"split: {S(_settings.ExtractorModel)}|{S(_settings.ExplainModel)}|{S(_settings.NarrativeModel)}";
            return label.Length <= 40 ? label : label[..40];
        }

        private sealed record GeminiRaw(
            string Text, string FinishReason, int InputTokens, int OutputTokens, int ThoughtTokens);

        // =====================================================================
        //  Orchestration
        // =====================================================================
        private async Task<(InterpretationResult Result, int InputTokens, int OutputTokens, string RawResponse)>
            RunSplitPipelineAsync(
                string languageCode, string languageName, string fileName, PatientContext? patientContext,
                string? pdfBase64, int pdfBytesLength, string? extractedText, CancellationToken ct)
        {
            LastStageTimings.Clear();
            LastStageTimings["ai_pipeline"] = 1;
            LastModelsUsed = "";

            // ---------- Stage A: extraction ----------
            var swA = System.Diagnostics.Stopwatch.StartNew();
            var (result, inA, outA, rawA) = await CallGeminiAsync(
                languageCode, fileName, patientContext, pdfBase64, pdfBytesLength, extractedText, ct,
                modelOverride: _settings.ExtractorModel,
                userPromptAddendum: StageAContract,
                thinkingLevelOverride: _settings.ExtractorThinkingLevel,
                allowSplitPipeline: false);
            swA.Stop();

            LastStageTimings["ai_a_ms"] = swA.ElapsedMilliseconds;
            LastStageTimings["ai_a_in"] = inA;
            LastStageTimings["ai_a_out"] = outA;

            var analytes = result.KeyResults ?? new List<KeyResult>();
            if (!result.IsMedicalAnalysis || analytes.Count == 0)
            {
                // Rejected document: nothing to explain, nothing to narrate.
                _logger.LogInformation("Split pipeline: stage A rejected the document — stages B and C skipped.");
                LastModelsUsed = ShortModels();
                return (result, inA, outA, rawA);
            }

            _logger.LogInformation(
                "Split pipeline: stage A extracted {Count} analytes in {Ms} ms ({Model}). Starting B+C in parallel.",
                analytes.Count, swA.ElapsedMilliseconds, _settings.ExtractorModel);

            var patientBlock = BuildPatientContextBlock(patientContext);

            // ---------- Stages B, C and A2 concurrently ----------
            var swBC = System.Diagnostics.Stopwatch.StartNew();
            var explainTask = RunExplanationStageAsync(analytes, languageCode, languageName, patientBlock, ct);
            var narrativeTask = RunNarrativeStageAsync(analytes, languageCode, languageName, patientBlock, ct);
            var sweepTask = _settings.EnableCompletenessSweep
                ? RunCompletenessSweepAsync(analytes, languageCode, fileName, patientContext,
                                            pdfBase64, pdfBytesLength, extractedText, ct)
                : Task.FromResult((new List<KeyResult>(), 0, 0));
            await Task.WhenAll(explainTask, narrativeTask, sweepTask);
            swBC.Stop();

            var (explanations, inB, outB, batches) = explainTask.Result;
            var (narrative, inC, outC, thoughtsC) = narrativeTask.Result;
            var (recovered, inS, outS) = sweepTask.Result;

            LastStageTimings["ai_b_in"] = inB;
            LastStageTimings["ai_b_out"] = outB;
            LastStageTimings["ai_b_batches"] = batches;
            LastStageTimings["ai_c_in"] = inC;
            LastStageTimings["ai_c_out"] = outC;
            LastStageTimings["ai_c_think"] = thoughtsC;
            LastStageTimings["ai_bc_ms"] = swBC.ElapsedMilliseconds;
            LastStageTimings["ai_s_in"] = inS;
            LastStageTimings["ai_s_out"] = outS;
            LastStageTimings["ai_s_recovered"] = recovered.Count;

            // ---------- Assembly ----------
            for (int i = 0; i < analytes.Count; i++)
                if (explanations.TryGetValue(i, out var text) && !string.IsNullOrWhiteSpace(text))
                    analytes[i].Explanation = text;

            // Analytes recovered by the sweep join the table and get their own
            // explanations in one extra small call (only when something was found).
            int inR = 0, outR = 0;
            if (recovered.Count > 0)
            {
                int firstNew = analytes.Count;
                analytes.AddRange(recovered);

                var newIndices = Enumerable.Range(firstNew, recovered.Count).ToList();
                var (recoveredExplanations, inRx, outRx) = await ExplainBatchAsync(
                    analytes, newIndices,
                    BuildSystemPrompt().Replace("{LANGUAGE_NAME}", languageName),
                    languageCode, languageName, patientBlock, ct);
                inR = inRx; outR = outRx;

                foreach (var kv in recoveredExplanations)
                    analytes[kv.Key].Explanation = kv.Value;

                if (result.Audit != null)
                    result.Audit.ExpectedCount = Math.Max(result.Audit.ExpectedCount, analytes.Count);

                _logger.LogWarning(
                    "Split pipeline: completeness sweep recovered {Count} analyte(s) missed by stage A: {Names}.",
                    recovered.Count, string.Join(", ", recovered.Select(r => r.Parameter)));
            }

            if (narrative != null)
            {
                result.Summary = narrative.Summary ?? result.Summary;
                result.Correlations = narrative.Correlations ?? result.Correlations;
                result.Recommendations = narrative.Recommendations ?? result.Recommendations;
                result.Disclaimer = narrative.Disclaimer ?? result.Disclaimer;
                if (narrative.AbnormalFindings is { Count: > 0 }) result.AbnormalFindings = narrative.AbnormalFindings;
                if (narrative.RiskFactors is { Count: > 0 }) result.RiskFactors = narrative.RiskFactors;
                if (narrative.DoctorQuestions is { Count: > 0 }) result.DoctorQuestions = narrative.DoctorQuestions;
            }

            int missing = analytes.Count(a => string.IsNullOrWhiteSpace(a.Explanation));
            if (missing > 0)
                _logger.LogWarning("Split pipeline: {Missing}/{Total} analytes ended without an explanation.",
                    missing, analytes.Count);

            var merged = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

            _logger.LogInformation(
                "Split pipeline done: A={A}ms, B+C(+sweep)={BC}ms (B batches={Batches}, recovered={Recovered}), " +
                "tokens in={In} out={Out}.",
                swA.ElapsedMilliseconds, swBC.ElapsedMilliseconds, batches, recovered.Count,
                inA + inB + inC + inS + inR, outA + outB + outC + outS + outR);

            LastModelsUsed = ShortModels();

            return (result,
                    inA + inB + inC + inS + inR,
                    outA + outB + outC + outS + outR,
                    merged);
        }

        // =====================================================================
        //  Stage A2 — completeness sweep (runs in parallel with B and C)
        // =====================================================================
        private async Task<(List<KeyResult> Recovered, int InputTokens, int OutputTokens)>
            RunCompletenessSweepAsync(
                List<KeyResult> extracted, string languageCode, string fileName, PatientContext? patientContext,
                string? pdfBase64, int pdfBytesLength, string? extractedText, CancellationToken ct)
        {
            var languageName = SupportedLanguagesConfig.GetLangName(languageCode);
            var systemPrompt = BuildSystemPrompt().Replace("{LANGUAGE_NAME}", languageName);
            var userPrompt = BuildUserPrompt(languageName, languageCode, fileName, patientContext,
                                             hasInlinePdf: pdfBase64 != null,
                                             extractedText: extractedText)
                             + "\n\n" + BuildSweepContract(extracted);

            try
            {
                var raw = await PostAsync(
                    systemPrompt: systemPrompt,
                    userPrompt: userPrompt,
                    pdfBase64: pdfBase64,
                    modelName: _settings.ExtractorModel,
                    thinkingBudget: 0,
                    thinkingLevel: _settings.ExtractorThinkingLevel,
                    logContext: $"STAGE A2 sweep, {extracted.Count} already extracted",
                    languageCode: languageCode,
                    ct: ct);

                var missing = ParseSweep(raw.Text, extracted);
                return (missing, raw.InputTokens, raw.OutputTokens);
            }
            catch (Exception ex)
            {
                // Purely additive stage: if it fails, we keep stage A's table.
                _logger.LogWarning(ex, "Split pipeline: completeness sweep failed — keeping stage A's table as is.");
                return (new List<KeyResult>(), 0, 0);
            }
        }

        /// <summary>
        /// Keeps only entries that are genuinely new (normalized name unknown) and
        /// that carry a value — a sweep must never duplicate or invent rows.
        /// </summary>
        private List<KeyResult> ParseSweep(string modelText, List<KeyResult> extracted)
        {
            var known = extracted
                .Where(k => !string.IsNullOrWhiteSpace(k.Parameter))
                .Select(k => NormalizeName(k.Parameter))
                .ToHashSet(StringComparer.Ordinal);

            var recovered = new List<KeyResult>();

            var parsed = JsonSerializer.Deserialize<SweepResponse>(
                ExtractJsonObject(modelText),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    AllowTrailingCommas = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                });

            foreach (var kr in parsed?.Missing ?? new List<KeyResult>())
            {
                if (string.IsNullOrWhiteSpace(kr.Parameter) || string.IsNullOrWhiteSpace(kr.Value)) continue;
                if (!known.Add(NormalizeName(kr.Parameter))) continue;
                recovered.Add(kr);

                // Hard cap: a confused model must not be able to duplicate the
                // whole report through this door.
                if (recovered.Count >= 15) break;
            }

            return recovered;
        }

        private static string NormalizeName(string name) =>
            System.Text.RegularExpressions.Regex.Replace(
                name.Trim().ToLowerInvariant(), @"[\s\.\-_/]+", "");

        private sealed class SweepResponse
        {
            [System.Text.Json.Serialization.JsonPropertyName("missing")]
            public List<KeyResult>? Missing { get; set; }
        }

        private static string BuildSweepContract(List<KeyResult> extracted)
        {
            var sb = new StringBuilder();
            sb.Append(@"=========================================================
COMPLETENESS SWEEP — SECOND READING PASS (output-contract override)
=========================================================
A first pass has already extracted the analytes listed below. Your ONLY job now
is to find what that pass MISSED. Read every section, table, page and footer of
the report again, line by line, including the last row of each section.

Output STRICT JSON, no markdown fences, exactly this shape:
{""missing"":[ <same object shape as one entry of ""key_results"", with ""explanation"":"""" > ]}

RULES:
- Include an analyte ONLY if it is measured in the report AND its name is absent
  from the list below. Match on meaning, not spelling: a different wording of an
  analyte already listed is NOT missing.
- NEVER invent an analyte, a value, a unit or a reference range. If nothing was
  missed, return {""missing"":[]} — that is the expected, normal answer.
- Do not include comments, methods, sample types, headers or panel titles: only
  measured parameters with a value.
- Leave ""explanation"" empty; another stage writes it.

ALREADY EXTRACTED (");
            sb.Append(extracted.Count);
            sb.Append(" analytes):\n");
            foreach (var kr in extracted)
                sb.Append("- ").Append(kr.Parameter).Append('\n');
            return sb.ToString();
        }

        // =====================================================================
        //  Stage B — per-analyte explanations, parallel batches
        // =====================================================================
        private async Task<(Dictionary<int, string> Explanations, int InputTokens, int OutputTokens, int Batches)>
            RunExplanationStageAsync(List<KeyResult> analytes, string languageCode, string languageName,
                                     string patientBlock, CancellationToken ct)
        {
            var batchSize = Math.Max(4, _settings.ExplainBatchSize);
            var systemPrompt = BuildSystemPrompt().Replace("{LANGUAGE_NAME}", languageName);

            var batches = new List<List<int>>();
            for (int i = 0; i < analytes.Count; i += batchSize)
                batches.Add(Enumerable.Range(i, Math.Min(batchSize, analytes.Count - i)).ToList());

            var tasks = batches.Select(indices => ExplainBatchAsync(
                analytes, indices, systemPrompt, languageCode, languageName, patientBlock, ct)).ToList();

            var results = await Task.WhenAll(tasks);

            var merged = new Dictionary<int, string>();
            int inTokens = 0, outTokens = 0;
            foreach (var (map, inT, outT) in results)
            {
                inTokens += inT;
                outTokens += outT;
                foreach (var kv in map) merged[kv.Key] = kv.Value;
            }

            return (merged, inTokens, outTokens, batches.Count);
        }

        /// <summary>
        /// One batch of explanations. A failed batch is retried once on the same
        /// model and then once on the narrative model; if it still fails, only
        /// THOSE analytes stay without an explanation — the report is not lost.
        /// </summary>
        private async Task<(Dictionary<int, string> Map, int InputTokens, int OutputTokens)> ExplainBatchAsync(
            List<KeyResult> analytes, List<int> indices, string systemPrompt,
            string languageCode, string languageName, string patientBlock, CancellationToken ct)
        {
            var userPrompt = BuildExplainPrompt(analytes, indices, languageCode, languageName, patientBlock);

            string[] modelChain =
            {
                _settings.ExplainModel,
                _settings.ExplainModel,
                _settings.NarrativeModel
            };

            for (int attempt = 0; attempt < modelChain.Length; attempt++)
            {
                try
                {
                    var raw = await PostAsync(
                        systemPrompt: systemPrompt,
                        userPrompt: userPrompt,
                        pdfBase64: null,
                        modelName: modelChain[attempt],
                        thinkingBudget: 0,
                        thinkingLevel: _settings.ExplainThinkingLevel,
                        logContext: $"STAGE B, {indices.Count} analytes",
                        languageCode: languageCode,
                        ct: ct);

                    return (ParseExplanations(raw.Text, indices), raw.InputTokens, raw.OutputTokens);
                }
                catch (Exception ex) when (attempt < modelChain.Length - 1 && !ct.IsCancellationRequested)
                {
                    _logger.LogWarning(ex,
                        "Split pipeline: explanation batch [{First}..{Last}] failed on {Model}, retrying.",
                        indices.First(), indices.Last(), modelChain[attempt]);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Split pipeline: explanation batch [{First}..{Last}] failed definitively. " +
                        "Those analytes will have no explanation.",
                        indices.First(), indices.Last());
                    return (new Dictionary<int, string>(), 0, 0);
                }
            }

            return (new Dictionary<int, string>(), 0, 0);
        }

        private static Dictionary<int, string> ParseExplanations(string modelText, List<int> indices)
        {
            var map = new Dictionary<int, string>();
            var allowed = indices.ToHashSet();

            using var doc = JsonDocument.Parse(ExtractJsonObject(modelText));
            if (!doc.RootElement.TryGetProperty("explanations", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return map;

            int fallbackPos = 0;
            foreach (var item in arr.EnumerateArray())
            {
                var text = item.TryGetProperty("explanation", out var e) ? e.GetString() : null;
                if (string.IsNullOrWhiteSpace(text)) { fallbackPos++; continue; }

                int idx;
                if (item.TryGetProperty("index", out var iEl) &&
                    (iEl.ValueKind == JsonValueKind.Number ? iEl.TryGetInt32(out idx)
                                                           : int.TryParse(iEl.GetString(), out idx))
                    && allowed.Contains(idx))
                {
                    map[idx] = text!.Trim();
                }
                else if (fallbackPos < indices.Count)
                {
                    // The model dropped or renumbered the index: fall back to the
                    // order we asked for, which it does respect.
                    map[indices[fallbackPos]] = text!.Trim();
                }
                fallbackPos++;
            }

            return map;
        }

        // =====================================================================
        //  Stage C — clinical narrative
        // =====================================================================
        private async Task<(InterpretationResult? Narrative, int InputTokens, int OutputTokens, int Thoughts)>
            RunNarrativeStageAsync(List<KeyResult> analytes, string languageCode, string languageName,
                                   string patientBlock, CancellationToken ct)
        {
            var systemPrompt = BuildSystemPrompt().Replace("{LANGUAGE_NAME}", languageName);
            var userPrompt = BuildNarrativePrompt(analytes, languageCode, languageName, patientBlock);

            string[] modelChain = { _settings.NarrativeModel, _settings.ExtractorModel };

            for (int attempt = 0; attempt < modelChain.Length; attempt++)
            {
                try
                {
                    var raw = await PostAsync(
                        systemPrompt: systemPrompt,
                        userPrompt: userPrompt,
                        pdfBase64: null,
                        modelName: modelChain[attempt],
                        thinkingBudget: _settings.ThinkingBudget,
                        thinkingLevel: _settings.NarrativeThinkingLevel,
                        logContext: $"STAGE C, {analytes.Count} analytes",
                        languageCode: languageCode,
                        ct: ct);

                    var narrative = JsonSerializer.Deserialize<InterpretationResult>(
                        ExtractJsonObject(raw.Text),
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true,
                            AllowTrailingCommas = true,
                            ReadCommentHandling = JsonCommentHandling.Skip
                        });

                    return (narrative, raw.InputTokens, raw.OutputTokens, raw.ThoughtTokens);
                }
                catch (Exception ex) when (attempt < modelChain.Length - 1 && !ct.IsCancellationRequested)
                {
                    _logger.LogWarning(ex,
                        "Split pipeline: narrative stage failed on {Model}, retrying on {Next}.",
                        modelChain[attempt], modelChain[attempt + 1]);
                }
            }

            // Narrative is essential for the report — make the whole split fail so
            // CallGeminiAsync falls back to the monolithic call.
            throw new InvalidOperationException("Split pipeline: narrative stage failed on every model.");
        }

        // =====================================================================
        //  Stage prompts (output-contract overrides on top of the SAME system
        //  prompt, so every extraction / explanation / safety rule stays in force)
        // =====================================================================
        private const string StageAContract = @"=========================================================
STAGE 1 OF 3 — EXTRACTION ONLY (output-contract override)
=========================================================
This call is the EXTRACTION stage of a 3-stage pipeline: another call writes the
per-analyte explanations and a third one writes the clinical narrative. For THIS
call ONLY, override the output contract:
- Emit ""is_medical_analysis"", ""rejection_reason"", ""patient_info"", ""key_results""
  and ""_extraction_audit"".
- Inside every ""key_results"" entry emit all fields EXCEPT the prose one: set
  ""explanation"" to """" (empty string). Do NOT write any explanation text.
- Set ""summary"", ""correlations"", ""recommendations"" and ""disclaimer"" to """",
  and ""abnormal_findings"", ""risk_factors"", ""doctor_questions"" to [].
- EVERY extraction rule from the system instructions stays in force without
  exception: completeness, first/last row of a section, value-vs-reference
  pairing, status (normal/high/low/borderline), parameter_normalized_en,
  panel_header_raw, analyte_line_raw, lab routing codes, and the
  _extraction_audit self-check.
Extraction accuracy is the ONLY thing that matters here. Do not spend effort on
prose — it is another stage's job.";

        private static string BuildExplainPrompt(List<KeyResult> analytes, List<int> indices,
            string languageCode, string languageName, string patientBlock)
        {
            var sb = new StringBuilder();
            sb.Append($"RESPONSE LANGUAGE: {languageName} (code: {languageCode})\n\n");
            sb.Append(@"=========================================================
STAGE 2 OF 3 — PER-ANALYTE EXPLANATIONS ONLY
=========================================================
The lab report has ALREADY been extracted by the previous stage. Below is a
batch of its analytes, exactly as extracted. Your only job is to write the
""explanation"" text for each of them.

- Follow the explanation-depth policy from the system instructions EXACTLY as
  you would inside a full report: brief for a normal value, full educational
  depth (what it measures, what the deviation may mean, what influences it,
  what to do next) for high / low / borderline values.
- Use ONLY the data given below. NEVER invent, re-read, correct or question a
  value, unit, reference range or status — they are final.
- Do NOT diagnose and do NOT mention medications, doses or treatments.
- Write in " + languageName + @".

Output STRICT JSON, no markdown fences, exactly this shape:
{""explanations"":[{""index"":<the index given below>,""explanation"":""...""}]}
Return one entry for EVERY analyte listed, in the same order.
");
            if (!string.IsNullOrWhiteSpace(patientBlock)) sb.Append(patientBlock).Append('\n');

            sb.Append("\n<ANALYTES>\n");
            foreach (var i in indices)
            {
                var a = analytes[i];
                sb.Append($"[{i}] {a.Parameter} | value: {a.Value} | unit: {a.Unit} | " +
                          $"reference: {a.ReferenceRange} | status: {a.Status}");
                if (!string.IsNullOrWhiteSpace(a.PanelHeaderRaw)) sb.Append($" | panel: {a.PanelHeaderRaw}");
                sb.Append('\n');
            }
            sb.Append("</ANALYTES>");
            return sb.ToString();
        }

        private static string BuildNarrativePrompt(List<KeyResult> analytes,
            string languageCode, string languageName, string patientBlock)
        {
            var sb = new StringBuilder();
            sb.Append($"RESPONSE LANGUAGE: {languageName} (code: {languageCode})\n\n");
            sb.Append(@"=========================================================
STAGE 3 OF 3 — CLINICAL NARRATIVE ONLY
=========================================================
The complete lab report has ALREADY been extracted and is given below. Your job
is the REASONING part: read the whole picture across panels and write the
narrative sections. Take the time to reason properly — this is the part of the
report the patient actually reads.

Emit ONLY these fields, STRICT JSON, no markdown fences:
{
  ""summary"": string,
  ""abnormal_findings"": [ { ""parameter"": string, ""explanation"": string, ""severity"": ""mild""|""moderate""|""severe"" } ],
  ""correlations"": string,
  ""recommendations"": string,
  ""disclaimer"": string,
  ""risk_factors"": [string, ...],
  ""doctor_questions"": [string, ...]
}
- Do NOT emit ""key_results"", ""patient_info"" or ""_extraction_audit"".
- All the schema rules from the system instructions apply to these fields:
  the minimum length of ""correlations"" and ""recommendations"", the mandatory
  entry in ""abnormal_findings"" for EVERY high / low / borderline analyte, the
  wording of ""risk_factors"", the doctor questions, and every safety rule
  (no diagnosis, no medication, no dose).
- Use ONLY the analytes below. Never invent a value and never contradict a status.
- Write everything in " + languageName + @".
");
            if (!string.IsNullOrWhiteSpace(patientBlock)) sb.Append(patientBlock).Append('\n');

            sb.Append("\n<ANALYTES>\n");
            foreach (var a in analytes)
            {
                sb.Append($"{a.Parameter} | value: {a.Value} | unit: {a.Unit} | " +
                          $"reference: {a.ReferenceRange} | status: {a.Status}");
                if (!string.IsNullOrWhiteSpace(a.PanelHeaderRaw)) sb.Append($" | panel: {a.PanelHeaderRaw}");
                sb.Append('\n');
            }
            sb.Append("</ANALYTES>");
            return sb.ToString();
        }

        // =====================================================================
        //  Shared low-level call: HTTP + error taxonomy + wrapper parsing.
        //  Used by the monolithic path AND by every split stage, so the retry
        //  semantics the controller relies on (transient / retired model) are
        //  identical everywhere.
        // =====================================================================
        private async Task<GeminiRaw> PostAsync(
            string systemPrompt, string userPrompt, string? pdfBase64, string modelName,
            int thinkingBudget, string? thinkingLevel, string logContext, string languageCode,
            CancellationToken ct)
        {
            var requestBody = BuildRequestBody(systemPrompt, userPrompt, pdfBase64,
                                               modelName, thinkingBudget, thinkingLevel);
            var url = string.Format(EndpointFormat, modelName, _settings.ApiKey,
                                    _settings.BaseUrl.TrimEnd('/'));

            using var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds);
            using var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            _logger.LogInformation("Calling Gemini {Model} for {Language} ({Context}).",
                modelName, languageCode, logContext);

            using var response = await http.PostAsync(url, content, ct);
            var responseString = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Gemini returned {Status}. Body: {Body}",
                    (int)response.StatusCode, Truncate(responseString, 2000));

                // 429 (rate-limit) and 503 (server overload) are TRANSIENT: the
                // controller applies a longer backoff and more attempts for them.
                var statusInt = (int)response.StatusCode;
                if (statusInt == 429 || statusInt == 503)
                {
                    throw new GeminiTransientException(statusInt,
                        $"Gemini API transient error {statusInt}: {Truncate(responseString, 300)}");
                }

                // 404 + "no longer available" / "NOT_FOUND" means Google retired the
                // model id. No retry can fix it — surface a dedicated exception so
                // the caller escalates to the next tier immediately.
                if (statusInt == 404
                    && (responseString.Contains("no longer available", StringComparison.OrdinalIgnoreCase)
                        || responseString.Contains("NOT_FOUND", StringComparison.Ordinal)))
                {
                    _logger.LogError(
                        "Gemini model '{Model}' has been retired by Google. Update appsettings.json " +
                        "(see https://ai.google.dev/gemini-api/docs/models for the current list).",
                        modelName);
                    throw new GeminiModelRetiredException(modelName,
                        $"Gemini model '{modelName}' was retired by Google and is no longer available. " +
                        $"Update the configured model id in appsettings.json.");
                }

                throw new InvalidOperationException(
                    $"Gemini API error {statusInt}: {Truncate(responseString, 500)}");
            }

            string modelText;
            string finishReason = "";
            int inputTokens = 0, outputTokens = 0, thoughts = 0;
            try
            {
                using var doc = JsonDocument.Parse(responseString);
                var root = doc.RootElement;

                if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
                {
                    var promptFeedback = root.TryGetProperty("promptFeedback", out var pf)
                        ? pf.ToString() : "(no feedback)";
                    _logger.LogError("Gemini returned no candidates. promptFeedback: {Feedback}. Body: {Body}",
                        promptFeedback, Truncate(responseString, 1000));
                    throw new InvalidOperationException(
                        "Gemini returned no candidates (possibly blocked by safety filters).");
                }

                var candidate = candidates[0];
                if (candidate.TryGetProperty("finishReason", out var fr))
                    finishReason = fr.GetString() ?? "";

                if (!candidate.TryGetProperty("content", out var contentEl)
                    || !contentEl.TryGetProperty("parts", out var parts)
                    || parts.GetArrayLength() == 0
                    || !parts[0].TryGetProperty("text", out var textEl))
                {
                    _logger.LogError("Gemini candidate has no text part. finishReason={Finish}. Body: {Body}",
                        finishReason, Truncate(responseString, 1500));
                    throw new InvalidOperationException(
                        $"Gemini returned an empty response (finishReason={finishReason}).");
                }

                modelText = textEl.GetString() ?? string.Empty;

                if (root.TryGetProperty("usageMetadata", out var usage))
                {
                    if (usage.TryGetProperty("promptTokenCount", out var pt))
                        inputTokens = pt.GetInt32();
                    if (usage.TryGetProperty("candidatesTokenCount", out var ct2))
                        outputTokens = ct2.GetInt32();
                    if (usage.TryGetProperty("thoughtsTokenCount", out var th))
                        thoughts = th.GetInt32();
                }
                LastThoughtsTokenCount = thoughts;
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not parse Gemini wrapper response. Body: {Body}",
                    Truncate(responseString, 2000));
                throw new InvalidOperationException("Gemini returned an unrecognized response shape.", ex);
            }

            _logger.LogInformation(
                "Gemini response received ({Model}). Tokens in={In} out={Out} thinking={Think}. " +
                "FinishReason={Finish}. TextLen={Len}",
                modelName, inputTokens, outputTokens, thoughts, finishReason, modelText.Length);

            // Truncated output -> the JSON is invalid. Fail fast so the retry
            // (or the monolithic fallback) kicks in with a clear reason.
            if (string.Equals(finishReason, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Gemini hit MaxOutputTokens ({_settings.MaxOutputTokens}). Response was truncated.");
            }

            return new GeminiRaw(modelText, finishReason, inputTokens, outputTokens, thoughts);
        }
    }
}
