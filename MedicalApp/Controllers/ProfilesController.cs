using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace MedicalApp.Controllers
{
    /// <summary>
    /// Manages the current user's health profiles (Eu, Mama, Tata, etc.).
    /// All actions require an authenticated user (session "UserEmail" set).
    /// </summary>
    public class ProfilesController : Controller
    {
        private readonly AppDbContext _db;
        private readonly PdfReportGenerator _pdfGenerator;
        private readonly EvolutionPdfGenerator _evolutionPdf;
        private readonly ArchiveAccessService _archiveAccess;
        private readonly IEmailService _emailService;
        private readonly ILogger<ProfilesController> _logger;

        public ProfilesController(
            AppDbContext db,
            PdfReportGenerator pdfGenerator,
            EvolutionPdfGenerator evolutionPdf,
            ArchiveAccessService archiveAccess,
            IEmailService emailService,
            ILogger<ProfilesController> logger)
        {
            _db = db;
            _pdfGenerator = pdfGenerator;
            _evolutionPdf = evolutionPdf;
            _archiveAccess = archiveAccess;
            _emailService = emailService;
            _logger = logger;
        }

        private string? CurrentEmail => HttpContext.Session.GetString("UserEmail");

        // ====================================================================
        // LIST
        // ====================================================================
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            var profiles = await _db.Profiles
                .AsNoTracking()
                .Where(p => p.UserEmail == CurrentEmail)
                .OrderByDescending(p => p.IsDefault)
                .ThenBy(p => p.Name)
                .ToListAsync();

            // Interpretation counts per profile (successful ones only).
            var profileIds = profiles.Select(p => p.Id).ToList();
            var counts = await _db.InterpretationHistories
                .AsNoTracking()
                .Where(h => h.ProfileId.HasValue
                            && profileIds.Contains(h.ProfileId.Value)
                            && h.Status == "success")
                .GroupBy(h => h.ProfileId!.Value)
                .Select(g => new { ProfileId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.ProfileId, x => x.Count);

            var vm = new ProfilesIndexViewModel
            {
                Profiles = profiles.Select(p => new ProfilesIndexViewModel.ProfileRow
                {
                    Id = p.Id,
                    Name = p.Name,
                    Relationship = p.Relationship,
                    Gender = p.Gender,
                    BirthYear = p.BirthYear,
                    Notes = p.Notes,
                    IsDefault = p.IsDefault,
                    CreatedAt = p.CreatedAt,
                    InterpretationsCount = counts.TryGetValue(p.Id, out var c) ? c : 0
                }).ToList()
            };

            return View(vm);
        }

        // ====================================================================
        // HISTORY (archive) - list interpretations for a specific profile
        // ====================================================================
        [HttpGet]
        public async Task<IActionResult> History(int id)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            var profile = await _db.Profiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == id && p.UserEmail == CurrentEmail);
            if (profile == null)
            {
                TempData["ErrorMessage"] = "Profilul nu a fost găsit.";
                return RedirectToAction(nameof(Index));
            }

            var rows = await _db.InterpretationHistories
                .AsNoTracking()
                .Where(h => h.UserEmail == CurrentEmail
                            && h.ProfileId == profile.Id
                            && h.Status == "success")
                .OrderByDescending(h => h.CreatedAt)
                .Select(h => new
                {
                    h.Id,
                    h.CreatedAt,
                    h.OriginalFileName,
                    h.Language,
                    h.RawJsonResult
                })
                .ToListAsync();

            var items = new List<ProfileHistoryViewModel.HistoryRow>(rows.Count);
            foreach (var r in rows)
            {
                var row = new ProfileHistoryViewModel.HistoryRow
                {
                    Id = r.Id,
                    CreatedAt = r.CreatedAt,
                    OriginalFileName = r.OriginalFileName,
                    Language = r.Language,
                    HasRawJson = !string.IsNullOrWhiteSpace(r.RawJsonResult)
                };

                // Lightweight parse only to show counts in the table - never block the page if parsing fails.
                if (row.HasRawJson)
                {
                    try
                    {
                        var parsed = JsonSerializer.Deserialize<InterpretationResult>(r.RawJsonResult!,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        row.KeyResultsCount = parsed?.KeyResults?.Count;
                        row.AbnormalFindingsCount = parsed?.AbnormalFindings?.Count;
                        row.PatientName = parsed?.PatientInfo?.Name;
                        row.DateTaken = parsed?.PatientInfo?.DateTaken;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not parse stored RawJsonResult for history id={Id}", r.Id);
                    }
                }

                items.Add(row);
            }

            // Sort by patient's sampling date (newest sampling first), with a tolerant
            // parser - falls back to CreatedAt when DateTaken is missing or unparsable.
            items = items
                .OrderByDescending(r => ParseSamplingDate(r.DateTaken) ?? r.CreatedAt)
                .ThenByDescending(r => r.CreatedAt)
                .ToList();

            var vm = new ProfileHistoryViewModel
            {
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                Relationship = profile.Relationship,
                Items = items
            };

            // Fetch the user to know their free-period state (for the UI hint only;
            // nothing is charged on this page).
            var user = await _db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Email == CurrentEmail);
            if (user != null)
            {
                vm.IsInFreePeriod = ArchiveAccessService.IsInFreePeriod(user);
                vm.FreeUntil = user.FreeArchiveUntil ?? user.DataC.Add(ArchiveAccessService.FreePeriod);
                vm.FreeUsesLeftInBundle = ArchiveAccessService.FreeUsesLeftInBundle(user);
            }

            return View(vm);
        }

        // ====================================================================
        // DOWNLOAD REPORT - regenerate PDF from stored JSON on the fly
        // ====================================================================
        [HttpGet]
        public async Task<IActionResult> DownloadReport(int id)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            var history = await _db.InterpretationHistories
                .AsNoTracking()
                .FirstOrDefaultAsync(h => h.Id == id
                                          && h.UserEmail == CurrentEmail
                                          && h.Status == "success");
            if (history == null || string.IsNullOrWhiteSpace(history.RawJsonResult))
            {
                TempData["ErrorMessage"] = "Raportul nu a fost găsit sau nu mai are date salvate.";
                return RedirectToAction(nameof(Index));
            }

            InterpretationResult? result;
            try
            {
                result = JsonSerializer.Deserialize<InterpretationResult>(history.RawJsonResult,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        AllowTrailingCommas = true,
                        ReadCommentHandling = JsonCommentHandling.Skip
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deserialize RawJsonResult for history id={Id}", id);
                TempData["ErrorMessage"] = "Raportul nu a putut fi reconstruit din datele stocate.";
                return RedirectToAction(nameof(History), new { id = history.ProfileId ?? 0 });
            }

            if (result == null)
            {
                TempData["ErrorMessage"] = "Raportul nu a putut fi reconstruit din datele stocate.";
                return RedirectToAction(nameof(History), new { id = history.ProfileId ?? 0 });
            }

            byte[] pdfBytes;
            try
            {
                pdfBytes = _pdfGenerator.Generate(result, LocalizedLabels.ForCurrentUi());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PDF regeneration failed for history id={Id}", id);
                TempData["ErrorMessage"] = "Eroare la generarea PDF-ului.";
                return RedirectToAction(nameof(History), new { id = history.ProfileId ?? 0 });
            }

            var fileName = $"MedicalApp_{history.CreatedAt:yyyyMMdd_HHmmss}_report.pdf";
            return File(pdfBytes, "application/pdf", fileName);
        }

        // ====================================================================
        // DELETE one interpretation from the archive (with explicit user confirmation
        // submitted from the History page).
        // ====================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteHistory(int id, int profileId)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            var history = await _db.InterpretationHistories
                .FirstOrDefaultAsync(h => h.Id == id && h.UserEmail == CurrentEmail);
            if (history == null)
            {
                TempData["ErrorMessage"] = "Interpretarea nu a fost găsită.";
                return RedirectToAction(nameof(History), new { id = profileId });
            }

            _db.InterpretationHistories.Remove(history);
            await _db.SaveChangesAsync();

            _logger.LogInformation(
                "User {Email} deleted interpretation history id={Id} (profile={Pid}, file={File}).",
                CurrentEmail, id, history.ProfileId, history.OriginalFileName);

            TempData["SuccessMessage"] = "Interpretarea a fost ștearsă din arhivă.";
            return RedirectToAction(nameof(History), new { id = profileId });
        }

        // ====================================================================
        // COMPARE 2 to 4 interpretations side-by-side (P1.5.5, premium feature).
        // Columns are ordered oldest → newest by patient's sampling date
        // (PatientInfo.DateTaken in the stored JSON, with a tolerant parser),
        // falling back to CreatedAt when the date cannot be parsed.
        // ====================================================================
        [HttpGet]
        public async Task<IActionResult> Compare(int profileId, int[]? ids)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            // Sanitize: distinct, non-zero ids, max 4.
            var distinctIds = (ids ?? Array.Empty<int>())
                .Where(i => i > 0)
                .Distinct()
                .Take(CompareInterpretationsViewModel.MaxSelections)
                .ToArray();

            if (distinctIds.Length < CompareInterpretationsViewModel.MinSelections)
            {
                TempData["ErrorMessage"] =
                    $"Selectează între {CompareInterpretationsViewModel.MinSelections} și " +
                    $"{CompareInterpretationsViewModel.MaxSelections} interpretări pentru comparație.";
                return RedirectToAction(nameof(History), new { id = profileId });
            }

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == CurrentEmail);
            if (user == null)
            {
                HttpContext.Session.Clear();
                return RedirectToAction("Index", "Home");
            }

            var profile = await _db.Profiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == profileId && p.UserEmail == CurrentEmail);
            if (profile == null)
            {
                TempData["ErrorMessage"] = "Profilul nu a fost găsit.";
                return RedirectToAction(nameof(Index));
            }

            var items = await _db.InterpretationHistories
                .AsNoTracking()
                .Where(h => distinctIds.Contains(h.Id)
                            && h.UserEmail == CurrentEmail
                            && h.ProfileId == profile.Id
                            && h.Status == "success"
                            && h.RawJsonResult != null)
                .ToListAsync();

            if (items.Count != distinctIds.Length)
            {
                TempData["ErrorMessage"] = "Una sau mai multe interpretări selectate nu au fost găsite.";
                return RedirectToAction(nameof(History), new { id = profileId });
            }

            // Archive premium billing: 1 use regardless of how many columns are compared.
            var check = _archiveAccess.TryConsume(user, "compare");
            if (!check.Allowed)
            {
                TempData["ErrorMessage"] =
                    "Ai rămas fără credite pentru comparație. Cumpără credite pentru a continua.";
                return RedirectToAction("Buy", "Credits");
            }
            await _db.SaveChangesAsync();

            // Deserialize each JSON; drop any that fail to parse.
            var parsed = new List<(InterpretationHistory h, InterpretationResult r)>();
            foreach (var h in items)
            {
                var r = DeserializeSafe(h.RawJsonResult);
                if (r != null) parsed.Add((h, r));
            }
            if (parsed.Count < CompareInterpretationsViewModel.MinSelections)
            {
                TempData["ErrorMessage"] = "Comparația nu a putut fi generată din datele stocate.";
                return RedirectToAction(nameof(History), new { id = profileId });
            }

            // Sort oldest → newest by patient's SAMPLING date (PatientInfo.DateTaken).
            // Fallback to CreatedAt when DateTaken is missing or unparsable.
            parsed = parsed
                .Select(t => (t.h, t.r,
                              eff: ParseSamplingDate(t.r.PatientInfo?.DateTaken) ?? t.h.CreatedAt))
                .OrderBy(t => t.eff)
                .Select(t => (t.h, t.r))
                .ToList();

            var vm = BuildComparison(profile, parsed);
            vm.CreditConsumed = check.CreditConsumed;
            return View(vm);
        }

        private static InterpretationResult? DeserializeSafe(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                return JsonSerializer.Deserialize<InterpretationResult>(raw,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        AllowTrailingCommas = true,
                        ReadCommentHandling = JsonCommentHandling.Skip
                    });
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Tolerantly parses the various date formats labs print on PDFs. Examples we want
        /// to handle: "27/01/2014", "27.01.2014", "27-01-2014", "2014-01-27", "01/27/2014",
        /// "27/01/2014 14:30", "27 Jan 2014" etc. Returns null when no parse succeeds.
        /// </summary>
        private static DateTime? ParseSamplingDate(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var s = raw.Trim();

            string[] formats =
            {
                "yyyy-MM-dd",
                "yyyy/MM/dd",
                "dd/MM/yyyy", "dd-MM-yyyy", "dd.MM.yyyy",
                "d/M/yyyy",  "d-M-yyyy",  "d.M.yyyy",
                "MM/dd/yyyy",
                "dd/MM/yyyy HH:mm", "dd-MM-yyyy HH:mm", "dd.MM.yyyy HH:mm",
                "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss",
                "dd MMM yyyy", "dd MMMM yyyy",
                "MMM dd, yyyy", "MMMM dd, yyyy"
            };

            // Try several culture-specific parses (locales used by the lab PDFs we see).
            string[] cultures = { "en-US", "ro-RO", "fr-FR", "es-ES", "de-DE" };
            foreach (var cult in cultures)
            {
                var ci = System.Globalization.CultureInfo.GetCultureInfo(cult);
                if (DateTime.TryParseExact(s, formats, ci,
                        System.Globalization.DateTimeStyles.AssumeLocal, out var d))
                    return d;
            }
            // Last-ditch generic parse.
            return DateTime.TryParse(s,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out var any) ? any : (DateTime?)null;
        }

        public static CompareInterpretationsViewModel BuildComparison(
            Profile profile,
            List<(InterpretationHistory h, InterpretationResult r)> sortedOldestFirst)
        {
            // ----------------------------------------------------------------
            // Pas 4: grouping by LOINC code (with parameter-name fallback)
            // ----------------------------------------------------------------
            // Historically the Compare view grouped rows by lowercase parameter
            // name. That broke whenever two lab reports for the same analyte
            // used different wording — "VSH" vs "ESR", "Glicemie" vs "Glucose",
            // "Procent protrombina" vs "Quick %" — even though they are the
            // same test (and now we have an authoritative LOINC code proving
            // they are the same test).
            //
            // New algorithm:
            //   * If a KeyResult has a non-empty LoincCode (post-validator),
            //     the row key is "loinc:<code>". All cells with the same
            //     code line up on ONE row, even if their parameter labels
            //     disagree.
            //   * If a KeyResult has no LoincCode (legacy rows pre-Pas 2, or
            //     parameters with no LOINC counterpart like custom indices),
            //     it falls back to the OLD behaviour: row key is
            //     "name:<normalized parameter>".
            //   * Cross-grouping is allowed: a parameter that was coded in
            //     one report and not coded in another will appear on TWO
            //     separate rows. That is intentional and HONEST — we don't
            //     pretend the link is solid. The user will see this and can
            //     re-interpret the older report to get LOINC coverage.
            // ----------------------------------------------------------------

            static string NameKey(string param) =>
                "name:" + (param ?? string.Empty).Trim().ToLowerInvariant();

            static string KeyFor(KeyResult kr) =>
                !string.IsNullOrWhiteSpace(kr.LoincCode)
                    ? "loinc:" + kr.LoincCode.Trim()
                    : NameKey(kr.Parameter);

            int n = sortedOldestFirst.Count;

            // Build per-column key→KeyResult dictionaries.
            var keyMaps = sortedOldestFirst
                .Select(t => (t.r.KeyResults ?? new())
                    .Where(k => !string.IsNullOrWhiteSpace(k.Parameter))
                    .GroupBy(KeyFor)
                    .ToDictionary(g => g.Key, g => g.First()))
                .ToList();

            // ----------------------------------------------------------------
            // Pas 5: ordering by LOINC CLASS (medical specialty)
            // ----------------------------------------------------------------
            // Now that every matched KeyResult carries an authoritative
            // LoincClass (HEM, CHEM, SERO, ENDO, COAG, UA, ...) we can group
            // the Compare table by medical specialty exactly like a real
            // lab report PDF does: Hematology first, then Coagulation,
            // Biochimie serică, Endocrinologie, Serologie, Urinalysis, etc.
            //
            // For each row key we pick the LATEST non-null LoincClass we
            // see across all columns (the newer interpretation is the most
            // likely to have been processed with the CLASS-aware seeder).
            // Rows without any class fall into the "Alte analize" bucket
            // and appear at the very end so they remain visible.
            // ----------------------------------------------------------------
            string? PickClassFor(string rowKey)
            {
                for (int i = n - 1; i >= 0; i--)
                {
                    if (keyMaps[i].TryGetValue(rowKey, out var kr) &&
                        !string.IsNullOrWhiteSpace(kr.LoincClass))
                    {
                        return kr.LoincClass;
                    }
                }
                return null;
            }

            // Build a one-shot "what class does each row belong to" map so
            // we sort by it without re-computing the value four times.
            var classByKey = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var k in keyMaps.SelectMany(m => m.Keys).Distinct())
                classByKey[k] = PickClassFor(k);

            // Union of all row keys, sorted by:
            //   1. LOINC CLASS priority (Hematology -> Coagulation -> Chemistry -> ...)
            //   2. parameter display name, case-insensitive
            // We DELIBERATELY no longer split "loinc:" vs "name:" prefixes
            // first — class-based grouping is a more meaningful organization
            // for the user. Rows without a class go last (priority 999).
            var allKeys = keyMaps
                .SelectMany(m => m.Keys)
                .Distinct()
                .OrderBy(k => Services.LoincClassDisplay.GetPriority(classByKey[k]))
                .ThenBy(k =>
                {
                    // representative parameter name for alphabetic sub-ordering
                    for (int i = n - 1; i >= 0; i--)
                        if (keyMaps[i].TryGetValue(k, out var kr))
                            return kr.Parameter ?? k;
                    return k;
                }, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // ----------------------------------------------------------------
            // LOINC drift detection (option b — conservative)
            // ----------------------------------------------------------------
            // Build a map: normalized parameter name -> set of distinct
            // LOINC codes assigned to that name across ALL columns.
            // When the SAME name received >=2 different LOINC codes, every
            // row that carries one of those codes gets HasLoincDrift=true
            // and a tooltip listing the other codes seen under the same
            // wording. This warns the user about Gemini's text-extraction
            // variability without false-alarming on every minor difference.
            // ----------------------------------------------------------------
            var codesByNormName = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var (_, r) in sortedOldestFirst)
            {
                foreach (var kr in r.KeyResults ?? new())
                {
                    if (string.IsNullOrWhiteSpace(kr.Parameter) ||
                        string.IsNullOrWhiteSpace(kr.LoincCode)) continue;
                    var nname = kr.Parameter.Trim().ToLowerInvariant();
                    if (!codesByNormName.TryGetValue(nname, out var set))
                    {
                        set = new HashSet<string>(StringComparer.Ordinal);
                        codesByNormName[nname] = set;
                    }
                    set.Add(kr.LoincCode.Trim());
                }
            }

            int risen = 0, fallen = 0, unchanged = 0, partial = 0;

            var rows = new List<CompareInterpretationsViewModel.ComparisonRow>(allKeys.Count);
            string? previousClassLabel = null;
            foreach (var k in allKeys)
            {
                // Find a representative parameter object for the row's metadata
                // (latest column wins, falls back through earlier columns).
                KeyResult? meta = null;
                for (int i = n - 1; i >= 0 && meta == null; i--)
                    keyMaps[i].TryGetValue(k, out meta);

                var rowClass = classByKey[k];
                var classLabel = Services.LoincClassDisplay.GetLabel(rowClass);

                var row = new CompareInterpretationsViewModel.ComparisonRow
                {
                    Parameter = meta?.Parameter ?? k,
                    Unit = meta?.Unit,
                    ReferenceRange = meta?.ReferenceRange,
                    // Surface the LOINC identity on LOINC-grouped rows so the
                    // view can show a tooltip / badge. Null on name-fallback rows.
                    LoincCode = k.StartsWith("loinc:") ? meta?.LoincCode : null,
                    LoincLongName = k.StartsWith("loinc:") ? meta?.LoincLongName : null,
                    LoincSource = k.StartsWith("loinc:") ? meta?.LoincSource : null,
                    LoincScore = k.StartsWith("loinc:") ? meta?.LoincScore : null,
                    LoincClass = rowClass,
                    ClassDisplayLabel = classLabel,
                    // First row in each class group triggers a section header
                    // in the view. We compare against the previous row's label
                    // (not class code) so "HEM" and "HEM/BC" merge cleanly into
                    // a single "Hematologie" header.
                    IsFirstInClass = !string.Equals(classLabel, previousClassLabel, StringComparison.Ordinal),
                };
                previousClassLabel = classLabel;

                // Apply LOINC-drift warning when this row's parameter name
                // (case-insensitive) was mapped to MORE than one LOINC code
                // across the compared interpretations. The other codes go in
                // DriftLoincCodes for the tooltip.
                if (!string.IsNullOrWhiteSpace(row.LoincCode) && meta != null &&
                    !string.IsNullOrWhiteSpace(meta.Parameter))
                {
                    var nname = meta.Parameter.Trim().ToLowerInvariant();
                    if (codesByNormName.TryGetValue(nname, out var allCodes) && allCodes.Count > 1)
                    {
                        row.HasLoincDrift = true;
                        row.DriftLoincCodes = allCodes
                            .Where(c => !string.Equals(c, row.LoincCode, StringComparison.Ordinal))
                            .OrderBy(c => c, StringComparer.Ordinal)
                            .ToList();
                    }
                }

                // First numeric value index (used as the baseline for "risen/fallen").
                int? baseIdx = null;
                double baseValue = 0;
                int presentCount = 0;
                int numericCount = 0;

                for (int i = 0; i < n; i++)
                {
                    var cell = new CompareInterpretationsViewModel.Cell();
                    if (keyMaps[i].TryGetValue(k, out var kr))
                    {
                        presentCount++;
                        cell.Value = kr.Value;
                        cell.Status = kr.Status;
                        cell.CellDirection = "unchanged"; // refined below
                        var (v, ok) = ParseNumeric(kr.Value);
                        if (ok)
                        {
                            numericCount++;
                            if (baseIdx == null)
                            {
                                baseIdx = i;
                                baseValue = v;
                                cell.CellDirection = "first";
                            }
                            else
                            {
                                if (Math.Abs(v - baseValue) < 1e-9) cell.CellDirection = "unchanged";
                                else if (v > baseValue) cell.CellDirection = "risen";
                                else cell.CellDirection = "fallen";
                            }
                        }
                        else
                        {
                            cell.CellDirection = baseIdx == null ? "first" : "unchanged";
                        }
                    }
                    else
                    {
                        cell.CellDirection = "absent";
                    }
                    row.Cells.Add(cell);
                }

                // Aggregate row-level direction.
                if (presentCount < n)
                {
                    row.Direction = "partial";
                    partial++;
                }
                else if (numericCount == n && baseIdx != null)
                {
                    // Compare LAST numeric vs the baseline (first numeric).
                    var lastNumeric = row.Cells
                        .Select((c, idx) => (c, idx))
                        .Where(t => t.c.CellDirection != "absent" && ParseNumeric(t.c.Value).ok)
                        .Select(t => ParseNumeric(t.c.Value).value)
                        .Last();
                    if (Math.Abs(lastNumeric - baseValue) < 1e-9) { row.Direction = "unchanged"; unchanged++; }
                    else if (lastNumeric > baseValue) { row.Direction = "risen"; risen++; }
                    else { row.Direction = "fallen"; fallen++; }
                }
                else
                {
                    // All cells present but at least one non-numeric: compare strings.
                    var first = row.Cells[0].Value?.Trim();
                    bool allEqual = row.Cells.All(c =>
                        string.Equals(c.Value?.Trim(), first, StringComparison.OrdinalIgnoreCase));
                    if (allEqual) { row.Direction = "unchanged"; unchanged++; }
                    else { row.Direction = "unparsable"; }
                }

                rows.Add(row);
            }

            var columns = sortedOldestFirst.Select(t =>
            {
                var eff = ParseSamplingDate(t.r.PatientInfo?.DateTaken) ?? t.h.CreatedAt;
                return new CompareInterpretationsViewModel.Column
                {
                    HistoryId = t.h.Id,
                    CreatedAt = t.h.CreatedAt,
                    OriginalFileName = t.h.OriginalFileName,
                    DateTaken = t.r.PatientInfo?.DateTaken,
                    EffectiveDate = eff,
                    KeyResultsCount = t.r.KeyResults?.Count ?? 0,
                    AbnormalFindingsCount = t.r.AbnormalFindings?.Count ?? 0
                };
            }).ToList();

            return new CompareInterpretationsViewModel
            {
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                Columns = columns,
                Rows = rows,
                RisenCount = risen,
                FallenCount = fallen,
                UnchangedCount = unchanged,
                PartialCount = partial
            };
        }

        /// <summary>
        /// Tries to extract a numeric value from labels like "4.6", "4,6", "12.3 x10^9/L",
        /// "&lt;0.5", "&gt;200". Returns (0, false) when no parse is possible.
        /// </summary>
        private static (double value, bool ok) ParseNumeric(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return (0, false);
            var s = raw.Trim().TrimStart('<', '>', '=', '~', '≤', '≥', ' ').Replace(',', '.');
            // Take the first contiguous number-ish token.
            var buf = new System.Text.StringBuilder();
            bool seenDigit = false;
            foreach (var c in s)
            {
                if (char.IsDigit(c) || c == '.' || (c == '-' && buf.Length == 0))
                {
                    buf.Append(c);
                    if (char.IsDigit(c)) seenDigit = true;
                }
                else if (seenDigit) break;
            }
            if (buf.Length == 0 || !seenDigit) return (0, false);
            return double.TryParse(buf.ToString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var v)
                ? (v, true)
                : (0, false);
        }

        // ====================================================================
        // EVOLUTION (P1.8): time-series chart for 1..5 LOINC codes across ALL
        // of the profile's interpretations. No credit cost — we only aggregate
        // data that is already stored in InterpretationHistories.RawJsonResult.
        // ====================================================================
        [HttpGet]
        public async Task<IActionResult> Evolution(int profileId, string? codes)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            var profile = await _db.Profiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == profileId && p.UserEmail == CurrentEmail);
            if (profile == null)
            {
                TempData["ErrorMessage"] = "Profilul nu a fost găsit.";
                return RedirectToAction(nameof(Index));
            }

            // Parse and sanitize the user-pasted LOINC codes.
            // Accept comma, semicolon, whitespace and newline as separators so
            // the user can paste a quick list like "718-7, 4548-4 2160-0".
            var codeList = (codes ?? string.Empty)
                .Split(new[] { ',', ';', '\n', '\r', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(EvolutionViewModel.MaxSelections)
                .ToList();

            if (codeList.Count < EvolutionViewModel.MinSelections)
            {
                TempData["ErrorMessage"] =
                    $"Introdu între {EvolutionViewModel.MinSelections} și " +
                    $"{EvolutionViewModel.MaxSelections} coduri LOINC.";
                return RedirectToAction(nameof(History), new { id = profileId });
            }

            var vm = await BuildEvolutionAsync(profile, codeList);
            return View(vm);
        }

        // ====================================================================
        // EVOLUTION EXPORT — generate PDF on the server (embedding the Chart.js
        // PNG produced client-side via canvas.toDataURL) and either DOWNLOAD it
        // or EMAIL it to the logged-in user. No credit cost.
        // ====================================================================
        public class EvolutionExportRequest
        {
            public int ProfileId { get; set; }
            /// <summary>Comma/space-separated LOINC codes (same payload as the view query).</summary>
            public string Codes { get; set; } = string.Empty;
            /// <summary>
            /// PNG data URL produced by the canvas. Example:
            /// <c>"data:image/png;base64,iVBORw0KGgoAAAA..."</c>. May be empty
            /// if the user didn't wait for the chart to render — the PDF then
            /// contains only the tables.
            /// </summary>
            public string? ChartPngDataUrl { get; set; }
            /// <summary>"download" or "email".</summary>
            public string Mode { get; set; } = "download";
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EvolutionExport([FromForm] EvolutionExportRequest req)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return Unauthorized();

            var profile = await _db.Profiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == req.ProfileId && p.UserEmail == CurrentEmail);
            if (profile == null)
                return NotFound("Profil inexistent.");

            var codeList = (req.Codes ?? string.Empty)
                .Split(new[] { ',', ';', '\n', '\r', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(EvolutionViewModel.MaxSelections)
                .ToList();

            if (codeList.Count < EvolutionViewModel.MinSelections)
                return BadRequest("Trebuie cel puțin un cod LOINC.");

            var vm = await BuildEvolutionAsync(profile, codeList);

            // Decode the chart PNG from the data URL (best effort — pass null
            // to the PDF generator if it's missing/malformed).
            byte[]? png = null;
            if (!string.IsNullOrWhiteSpace(req.ChartPngDataUrl))
            {
                var commaIdx = req.ChartPngDataUrl.IndexOf(',');
                if (commaIdx > 0)
                {
                    try
                    {
                        png = Convert.FromBase64String(req.ChartPngDataUrl[(commaIdx + 1)..]);
                    }
                    catch (FormatException ex)
                    {
                        _logger.LogWarning(ex, "EvolutionExport: invalid base64 in ChartPngDataUrl, dropping image.");
                    }
                }
            }

            byte[] pdfBytes;
            try
            {
                pdfBytes = _evolutionPdf.Generate(vm, png);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EvolutionExport: PDF generation failed.");
                return StatusCode(500, "Generarea PDF a eșuat. Vezi log-ul aplicației.");
            }

            var fileName = $"Evolutie_{Sanitize(profile.Name)}_{DateTime.Now:yyyyMMdd_HHmm}.pdf";

            if (string.Equals(req.Mode, "email", StringComparison.OrdinalIgnoreCase))
            {
                var html =
                    $"<p>Bună,</p>" +
                    $"<p>Ai cerut raportul de evoluție în timp pentru profilul " +
                    $"<strong>{System.Net.WebUtility.HtmlEncode(profile.Name)}</strong>. " +
                    $"Îl găsești atașat acestui email ({vm.Series.Count} analize, " +
                    $"{vm.Series.Sum(s => s.Points.Count)} măsurători).</p>" +
                    $"<p>Coduri LOINC incluse: <code>{System.Net.WebUtility.HtmlEncode(string.Join(", ", codeList))}</code></p>" +
                    $"<p>O zi bună!<br/>— MedicalApp</p>";
                try
                {
                    await _emailService.SendEmailWithAttachmentAsync(
                        CurrentEmail,
                        $"Evoluție analize — {profile.Name}",
                        html,
                        pdfBytes,
                        fileName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "EvolutionExport: email send failed to {Email}.", CurrentEmail);
                    TempData["ErrorMessage"] = "Trimiterea emailului a eșuat. Încearcă \u201EDescarcă PDF\u201D în schimb.";
                    return RedirectToAction(nameof(Evolution),
                        new { profileId = req.ProfileId, codes = string.Join(",", codeList) });
                }

                TempData["SuccessMessage"] = $"Raportul a fost trimis pe email la {CurrentEmail}.";
                return RedirectToAction(nameof(Evolution),
                    new { profileId = req.ProfileId, codes = string.Join(",", codeList) });
            }

            // Default: stream the file back as a download.
            return File(pdfBytes, "application/pdf", fileName);
        }

        private static string Sanitize(string s)
        {
            var chars = s.Select(ch => (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_') ? ch : '_').ToArray();
            return new string(chars);
        }

        private async Task<EvolutionViewModel> BuildEvolutionAsync(Profile profile, List<string> codes)
        {
            var vm = new EvolutionViewModel
            {
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                RequestedCodes = codes,
            };

            // Load every successful interpretation for the profile.
            var histories = await _db.InterpretationHistories
                .AsNoTracking()
                .Where(h => h.UserEmail == CurrentEmail
                            && h.ProfileId == profile.Id
                            && h.Status == "success"
                            && h.RawJsonResult != null)
                .OrderBy(h => h.CreatedAt)
                .ToListAsync();

            // Distinct color palette (up to 5 series). Picked for high contrast
            // against white background and against each other.
            var palette = new[] { "#0d6efd", "#dc3545", "#198754", "#fd7e14", "#6f42c1" };

            // Build one series per requested code.
            var codeSet = new HashSet<string>(codes, StringComparer.OrdinalIgnoreCase);
            var seriesByCode = new Dictionary<string, EvolutionViewModel.EvolutionSeries>(StringComparer.OrdinalIgnoreCase);

            foreach (var h in histories)
            {
                var r = DeserializeSafe(h.RawJsonResult);
                if (r?.KeyResults == null) continue;

                var eff = ParseSamplingDate(r.PatientInfo?.DateTaken) ?? h.CreatedAt;

                foreach (var kr in r.KeyResults)
                {
                    if (string.IsNullOrWhiteSpace(kr.LoincCode)) continue;
                    if (!codeSet.Contains(kr.LoincCode.Trim())) continue;

                    var code = kr.LoincCode.Trim();
                    if (!seriesByCode.TryGetValue(code, out var s))
                    {
                        s = new EvolutionViewModel.EvolutionSeries
                        {
                            LoincCode = code,
                            ColorHex = palette[seriesByCode.Count % palette.Length],
                        };
                        seriesByCode[code] = s;
                    }

                    var (val, ok) = ParseNumeric(kr.Value);
                    var point = new EvolutionViewModel.EvolutionPoint
                    {
                        EffectiveDate = eff,
                        DateLabel = eff.ToLocalTime().ToString("yyyy-MM-dd"),
                        Value = kr.Value,
                        NumericValue = ok ? val : (double?)null,
                        Status = kr.Status,
                        PatientName = r.PatientInfo?.Name,
                        Laboratory = r.PatientInfo?.Laboratory,
                        Unit = kr.Unit,
                        ReferenceRange = kr.ReferenceRange,
                    };
                    s.Points.Add(point);

                    // Always refresh the series's "latest seen" metadata from
                    // the newest point so the table shows the freshest unit
                    // and reference range (which can change between labs).
                    s.DisplayParameter = kr.Parameter ?? code;
                    s.LoincLongName = kr.LoincLongName ?? s.LoincLongName;
                    s.LoincSource = kr.LoincSource ?? s.LoincSource;
                    if (kr.LoincScore.HasValue) s.LoincScore = kr.LoincScore;
                    s.ClassDisplayLabel = Services.LoincClassDisplay.GetLabel(kr.LoincClass);
                    s.Unit = kr.Unit ?? s.Unit;
                    s.ReferenceRange = kr.ReferenceRange ?? s.ReferenceRange;
                    var (lo, hi) = ParseReferenceRange(kr.ReferenceRange);
                    if (lo.HasValue) s.RefLow = lo;
                    if (hi.HasValue) s.RefHigh = hi;
                }
            }

            // Order each series' points chronologically.
            foreach (var s in seriesByCode.Values)
                s.Points = s.Points.OrderBy(p => p.EffectiveDate).ToList();

            // Codes the user asked for but never appeared in any interpretation.
            vm.CodesNotFound = codes.Where(c => !seriesByCode.ContainsKey(c)).ToList();

            // Series ordering: in the order the user typed the codes (so the
            // first one keeps its primary color and appears first in the legend).
            vm.Series = codes
                .Where(c => seriesByCode.ContainsKey(c))
                .Select(c => seriesByCode[c])
                .ToList();

            // Reassign palette colors in the user-typed order (palette index
            // can drift if some codes weren't found).
            for (int i = 0; i < vm.Series.Count; i++)
                vm.Series[i].ColorHex = palette[i % palette.Length];

            return vm;
        }

        /// <summary>
        /// Parses a reference-range string like "12 - 18", "&lt;100", "&gt;70",
        /// "0.5 - 1.2 mg/dL" into low/high numeric bounds. Either side may be
        /// null when the range is one-sided or completely unparsable.
        /// </summary>
        private static (double? low, double? high) ParseReferenceRange(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return (null, null);
            var s = raw.Replace(',', '.').Replace('–', '-').Replace('—', '-');

            // One-sided: "<X" / "≤X" -> upper bound; ">X" / "≥X" -> lower bound.
            var trim = s.TrimStart();
            if (trim.StartsWith("<") || trim.StartsWith("≤"))
            {
                var (v, ok) = ParseNumeric(trim.TrimStart('<', '≤'));
                return ok ? ((double?)null, v) : (null, null);
            }
            if (trim.StartsWith(">") || trim.StartsWith("≥"))
            {
                var (v, ok) = ParseNumeric(trim.TrimStart('>', '≥'));
                return ok ? (v, (double?)null) : (null, null);
            }

            // Range "X - Y" — find first '-' between digits.
            // We collect the FIRST two numbers in the string and treat them
            // as [low, high]. That handles "12 - 18", "12-18 mg/dL",
            // "0.5 - 1.2 / mmol/L", etc.
            var nums = System.Text.RegularExpressions.Regex.Matches(s,
                @"-?\d+(?:\.\d+)?");
            if (nums.Count >= 2)
            {
                if (double.TryParse(nums[0].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var lo)
                    && double.TryParse(nums[1].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var hi))
                {
                    return (lo, hi);
                }
            }
            return (null, null);
        }

        // ====================================================================
        // CREATE
        // ====================================================================
        [HttpGet]
        public IActionResult Create()
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            return View("Form", new ProfileFormViewModel());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(ProfileFormViewModel model)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            if (!ModelState.IsValid) return View("Form", model);

            var trimmedName = (model.Name ?? "").Trim();

            // Case-insensitive duplicate check
            var nameExists = await _db.Profiles
                .AnyAsync(p => p.UserEmail == CurrentEmail &&
                               p.Name.ToLower() == trimmedName.ToLower());
            if (nameExists)
            {
                ModelState.AddModelError(nameof(model.Name),
                    "Ai deja un profil cu acest nume. Alege altul.");
                return View("Form", model);
            }

            _db.Profiles.Add(new Profile
            {
                UserEmail = CurrentEmail,
                Name = trimmedName,
                Relationship = string.IsNullOrWhiteSpace(model.Relationship) ? null : model.Relationship.Trim(),
                Gender = string.IsNullOrWhiteSpace(model.Gender) ? null : model.Gender.Trim(),
                BirthYear = model.BirthYear,
                Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim(),
                CardiovascularRisk = NormalizeCvRisk(model.CardiovascularRisk),
                IsDefault = false,
                CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();

            TempData["SuccessMessage"] = $"Profilul \"{trimmedName}\" a fost creat.";
            return RedirectToAction(nameof(Index));
        }

        // ====================================================================
        // EDIT
        // ====================================================================
        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            var profile = await _db.Profiles
                .FirstOrDefaultAsync(p => p.Id == id && p.UserEmail == CurrentEmail);
            if (profile == null) return RedirectToAction(nameof(Index));

            var vm = new ProfileFormViewModel
            {
                Id = profile.Id,
                Name = profile.Name,
                Relationship = profile.Relationship,
                Gender = profile.Gender,
                BirthYear = profile.BirthYear,
                Notes = profile.Notes,
                CardiovascularRisk = profile.CardiovascularRisk,
                IsDefault = profile.IsDefault
            };
            return View("Form", vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(ProfileFormViewModel model)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            if (!ModelState.IsValid) return View("Form", model);

            var profile = await _db.Profiles
                .FirstOrDefaultAsync(p => p.Id == model.Id && p.UserEmail == CurrentEmail);
            if (profile == null) return RedirectToAction(nameof(Index));

            var trimmedName = (model.Name ?? "").Trim();

            var nameExists = await _db.Profiles
                .AnyAsync(p => p.UserEmail == CurrentEmail &&
                               p.Id != profile.Id &&
                               p.Name.ToLower() == trimmedName.ToLower());
            if (nameExists)
            {
                ModelState.AddModelError(nameof(model.Name),
                    "Ai deja un profil cu acest nume. Alege altul.");
                return View("Form", model);
            }

            profile.Name = trimmedName;
            profile.Relationship = string.IsNullOrWhiteSpace(model.Relationship) ? null : model.Relationship.Trim();
            profile.Gender = string.IsNullOrWhiteSpace(model.Gender) ? null : model.Gender.Trim();
            profile.BirthYear = model.BirthYear;
            profile.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
            profile.CardiovascularRisk = NormalizeCvRisk(model.CardiovascularRisk);

            await _db.SaveChangesAsync();
            TempData["SuccessMessage"] = $"Profilul \"{trimmedName}\" a fost actualizat.";
            return RedirectToAction(nameof(Index));
        }

        // ====================================================================
        // DELETE
        // ====================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            var profile = await _db.Profiles
                .FirstOrDefaultAsync(p => p.Id == id && p.UserEmail == CurrentEmail);
            if (profile == null) return RedirectToAction(nameof(Index));

            if (profile.IsDefault)
            {
                TempData["ErrorMessage"] = "Profilul implicit \"Eu\" nu poate fi șters. Doar poate fi redenumit.";
                return RedirectToAction(nameof(Index));
            }

            _db.Profiles.Remove(profile);
            await _db.SaveChangesAsync();

            TempData["SuccessMessage"] = $"Profilul \"{profile.Name}\" a fost șters.";
            return RedirectToAction(nameof(Index));
        }

        /// <summary>
        /// Validates and normalizes the cardiovascular-risk dropdown value.
        /// Accepts only the three known categories; everything else (including the
        /// "unknown" placeholder) is mapped to null so the AI prompt can fall back
        /// to its multi-threshold rule.
        /// </summary>
        private static string? NormalizeCvRisk(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var v = raw.Trim().ToLowerInvariant();
            return v switch
            {
                "low_moderate" => "low_moderate",
                "high"         => "high",
                "very_high"    => "very_high",
                _              => null
            };
        }
    }
}
