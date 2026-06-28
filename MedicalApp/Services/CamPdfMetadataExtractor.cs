using System.Text.RegularExpressions;

namespace MedicalApp.Services
{
    /// <summary>
    /// Result of scanning a clinic PDF for the minimum metadata required by
    /// the CAM batch processor: patient NAME and patient EMAIL, plus a
    /// sanity check that the file actually looks like a medical lab report.
    /// </summary>
    public class CamPdfMetadata
    {
        public string? PatientName { get; set; }
        public string? PatientEmail { get; set; }

        /// <summary>True when name+email are present AND the file looks like a medical-lab PDF.</summary>
        public bool IsValid { get; set; }

        /// <summary>Human-readable reason populated when <see cref="IsValid"/> is false.</summary>
        public string? Reason { get; set; }

        /// <summary>
        /// True when the explicit [MedicalApp] block was found and parsed —
        /// the gold path. False means the data came from heuristic fallbacks
        /// (label scan / near-email / capitalized line).
        /// </summary>
        public bool MatchedExplicitBlock { get; set; }

        /// <summary>True when keyword heuristics say this PDF is a medical lab report.</summary>
        public bool IsMedicalLabReport { get; set; }

        /// <summary>
        /// Translation key (in Loc.cs) corresponding to <see cref="Reason"/>.
        /// Lets callers display a localized message in the live progress log
        /// while keeping <see cref="Reason"/> itself in English for stable DB
        /// storage and traceability across languages.
        /// </summary>
        public string? ReasonKey { get; set; }
    }

    /// <summary>
    /// Pulls patient name + email out of a clinic-generated PDF. Strategy
    /// order, from highest to lowest confidence:
    ///
    ///   0. Explicit <c>[MedicalApp]</c> block — RECOMMENDED convention.
    ///      Clinic adds (manually or via template) on the last page:
    ///         [MedicalApp]
    ///         Pacient: Ion Popescu
    ///         Email: ion.popescu@example.com
    ///      When found, we trust it 100% and skip every other strategy.
    ///
    ///   1. Labelled lines ("Nume/Prenume: Ion Popescu", "Patient: ...").
    ///   2. Text immediately above the FIRST patient-block email.
    ///   3. First capitalized 2-4 word line on the page.
    ///
    /// Plus a keyword-based sanity check that the PDF contains medical
    /// terminology — rejects invoices, contracts, etc.
    /// </summary>
    public class CamPdfMetadataExtractor
    {
        // RFC-pragmatic email regex.
        private static readonly Regex EmailRx = new(
            @"\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Strategy 0: explicit [MedicalApp] block. Tolerates accents on label
        // keywords, multiple whitespace, both colon and dash separators.
        // Captures: group 1 = name, group 2 = email.
        private static readonly Regex MedicalAppBlockRx = new(
            @"\[MedicalApp\]\s*\r?\n" +
            @"\s*(?:Pacient|Patient|Nume|Name|Nom|Nombre)\s*[:\-]\s*(.+?)\s*\r?\n" +
            @"\s*(?:Email|E-mail|eMail|Mail|Correo|Courriel)\s*[:\-]\s*([A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Strategy 1 label list. Multi-language; new labels can be appended
        // without code changes. Includes Romanian "Nume/Prenume" forms.
        private static readonly string[] NameLabels =
        {
            "nume si prenume", "nume şi prenume", "nume și prenume",
            "nume/prenume", "prenume/nume", "nume,prenume",
            "pacient", "pacienta", "pacientă",
            "patient", "patient name", "patient's name",
            "patiente", "nom du patient", "nom",
            "nombre del paciente", "nombre",
            "name des patienten",
            "имя", "пациент",
            "patientennaam",
            "nume", "name"
        };

        private static readonly Regex NameLabelRx = new(
            // Matches the label anywhere on the line (not only at line-start)
            // so things like "Data: 10/10  Nume/Prenume: Ion Popescu" still work.
            @"(?im)(?:^|\s)(?:" + string.Join("|", NameLabels.Select(Regex.Escape)) + @")\s*[:\-]\s*(.+?)(?:\r?\n|$)",
            RegexOptions.Compiled);

        // Medical-lab keyword heuristic. We require AT LEAST 2 hits to consider
        // the PDF a lab report. Mix of RO / EN / FR / ES / DE common terms.
        private static readonly string[] MedicalKeywords =
        {
            // Romanian
            "analize", "analiza", "rezultat", "rezultate", "buletin",
            "biochimie", "hematologie", "imunologie", "hemoleucograma", "hemoleucogramă",
            "glicemie", "glucoza", "glucoză", "colesterol", "trigliceride",
            "leucocite", "eritrocite", "trombocite", "hemoglobina", "hemoglobină",
            "creatinina", "creatinină", "uree", "transaminaza", "tiroida",
            "valoare", "valori", "valori de referinta", "valori de referință",
            "interval de referinta", "interval de referință",
            "ser", "sange", "sânge", "urina", "urină",
            "laborator", "specimen", "recoltare",
            // English
            "results", "biochemistry", "hematology", "haematology", "immunology",
            "blood test", "blood count", "specimen", "reference range",
            "glucose", "cholesterol", "creatinine", "haemoglobin", "hemoglobin",
            // French
            "résultats", "biochimie", "hématologie", "analyses",
            // Spanish
            "resultados", "bioquímica", "hematología", "análisis",
            // German
            "ergebnisse", "biochemie", "hämatologie", "blutbild"
        };

        private readonly ILogger<CamPdfMetadataExtractor> _logger;

        public CamPdfMetadataExtractor(ILogger<CamPdfMetadataExtractor> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Scans <paramref name="pdfBytes"/> for a patient name + email pair.
        /// Never throws — always returns a populated <see cref="CamPdfMetadata"/>
        /// with <c>IsValid=false</c> + an explanation when extraction fails.
        ///
        /// Optional <paramref name="clinicDomainBlacklist"/>: domain substrings
        /// (e.g. "clinica-sante.ro") whose emails are SKIPPED when scanning
        /// for the patient address. Used to keep the clinic's own header
        /// email out of the patient pool. Case-insensitive.
        /// </summary>
        public CamPdfMetadata Extract(byte[] pdfBytes, string fileNameForLogs, IEnumerable<string>? clinicDomainBlacklist = null)
        {
            var result = new CamPdfMetadata();
            string text;
            try
            {
                using var ms = new MemoryStream(pdfBytes);
                text = PdfTextExtractor.Extract(ms);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CAM extractor: PdfTextExtractor failed for {File}", fileNameForLogs);
                result.Reason = "PDF unreadable (possibly scanned image — needs digital PDF).";
                result.ReasonKey = "CamProbeUnreadable";
                return result;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                result.Reason = "PDF text layer empty (scanned image?).";
                result.ReasonKey = "CamProbeTextEmpty";
                return result;
            }

            // ---- medical-lab sanity check ----
            result.IsMedicalLabReport = CountMedicalKeywords(text) >= 2;
            if (!result.IsMedicalLabReport)
            {
                result.Reason = "PDF does not look like a medical lab report (no medical terminology found).";
                result.ReasonKey = "CamProbeNotMedical";
                return result;
            }

            // ===== Strategy 0: explicit [MedicalApp] block (gold path) =====
            var block = MedicalAppBlockRx.Match(text);
            if (block.Success)
            {
                var explicitName = CleanCandidate(block.Groups[1].Value);
                var explicitEmail = block.Groups[2].Value.Trim();
                if (IsPlausibleName(explicitName))
                {
                    result.PatientName = explicitName;
                    result.PatientEmail = explicitEmail;
                    result.MatchedExplicitBlock = true;
                    result.IsValid = true;
                    return result;
                }
            }

            // ===== Heuristic fallback path =====
            var blacklist = (clinicDomainBlacklist ?? Enumerable.Empty<string>())
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => d.Trim().ToLowerInvariant())
                .ToList();

            // Email — pick the FIRST one whose domain is NOT in the blacklist.
            Match? patientEmailMatch = null;
            foreach (Match m in EmailRx.Matches(text))
            {
                var email = m.Value.ToLowerInvariant();
                if (blacklist.Any(b => email.Contains(b))) continue;
                patientEmailMatch = m;
                break;
            }
            if (patientEmailMatch == null)
            {
                if (blacklist.Count > 0)
                {
                    result.Reason = "No patient email found (only blacklisted clinic-domain emails were present).";
                    result.ReasonKey = "CamProbeEmailBlacklisted";
                }
                else
                {
                    result.Reason = "Email not found in PDF.";
                    result.ReasonKey = "CamProbeEmailNotFound";
                }
                return result;
            }
            result.PatientEmail = patientEmailMatch.Value.Trim();

            // Name — try 3 strategies, in order of decreasing confidence.
            result.PatientName = FindNameByLabel(text)
                                 ?? FindNameNearEmail(text, patientEmailMatch.Index)
                                 ?? FindNameByCapitalizedLine(text);

            if (string.IsNullOrWhiteSpace(result.PatientName))
            {
                result.Reason = "Patient name not found in PDF. Add a [MedicalApp] block on the last page (see CheckPdfs help).";
                result.ReasonKey = "CamProbeNameNotFound";
                return result;
            }

            result.IsValid = true;
            return result;
        }

        private static int CountMedicalKeywords(string text)
        {
            var lower = text.ToLowerInvariant();
            int hits = 0;
            foreach (var kw in MedicalKeywords)
                if (lower.Contains(kw)) hits++;
            return hits;
        }

        private static string? FindNameByLabel(string text)
        {
            foreach (Match m in NameLabelRx.Matches(text))
            {
                var candidate = CleanCandidate(m.Groups[1].Value);
                if (IsPlausibleName(candidate)) return candidate;
            }
            return null;
        }

        private static string? FindNameNearEmail(string text, int emailIndex)
        {
            int lineStart = text.LastIndexOf('\n', Math.Max(0, emailIndex - 1));
            if (lineStart < 0) lineStart = 0;
            var head = text[..lineStart];

            var lines = head.Split('\n', StringSplitOptions.None)
                .Reverse()
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Take(3)
                .ToList();

            foreach (var l in lines)
            {
                var candidate = CleanCandidate(l);
                if (IsPlausibleName(candidate)) return candidate;
            }
            return null;
        }

        private static string? FindNameByCapitalizedLine(string text)
        {
            foreach (var rawLine in text.Split('\n').Take(30))
            {
                var candidate = CleanCandidate(rawLine);
                if (!IsPlausibleName(candidate)) continue;
                var lower = candidate.ToLowerInvariant();
                if (lower.Contains("clinic") || lower.Contains("laborator") ||
                    lower.Contains("hospital") || lower.Contains("spital"))
                    continue;
                return candidate;
            }
            return null;
        }

        private static string CleanCandidate(string raw)
        {
            var s = raw.Trim();
            // Strip a leading slash + word + colon (e.g. "/Prenume: " when the
            // tokenizer split "Nume/Prenume:" into two pieces). This is the
            // specific Romanian-PDF artifact we saw in the wild.
            s = Regex.Replace(s, @"^/[A-Za-zĂÂÎȘȚăâîșț]+\s*:\s*", "");
            // Strip any other leading "Word: " label that survived the regex.
            s = Regex.Replace(s, @"^[A-Za-zĂÂÎȘȚăâîșț /]+:\s*", "");
            // Drop everything starting from the first digit (names have none).
            int firstDigit = s.IndexOfAny("0123456789".ToCharArray());
            if (firstDigit > 0) s = s[..firstDigit].Trim();
            // Cut at the first label-like separator.
            foreach (var sep in new[] { "  ", "\t", ";", "|" })
            {
                int idx = s.IndexOf(sep, StringComparison.Ordinal);
                if (idx > 0) { s = s[..idx].Trim(); break; }
            }
            return s.TrimEnd('.', ',', ':', '-', '_', '*', '/').Trim();
        }

        private static bool IsPlausibleName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            if (s.Length < 3 || s.Length > 80) return false;
            if (!s.Contains(' ')) return false;
            // Reject anything that still has a slash or colon — those are
            // tokenizer artifacts, not real names.
            if (s.Contains('/') || s.Contains(':')) return false;
            int letters = s.Count(c => char.IsLetter(c));
            if (letters < s.Length * 0.7) return false;
            if (s.Contains('@')) return false;
            return true;
        }
    }
}
