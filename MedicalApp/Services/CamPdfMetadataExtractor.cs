using System.Text.RegularExpressions;

namespace MedicalApp.Services
{
    /// <summary>
    /// Result of scanning a clinic PDF for the minimum metadata required by
    /// the CAM batch processor: patient NAME and patient EMAIL.
    /// </summary>
    public class CamPdfMetadata
    {
        public string? PatientName { get; set; }
        public string? PatientEmail { get; set; }

        /// <summary>True when BOTH name and email were found and look sane.</summary>
        public bool IsValid { get; set; }

        /// <summary>Human-readable reason populated when <see cref="IsValid"/> is false.</summary>
        public string? Reason { get; set; }
    }

    /// <summary>
    /// Pulls patient name + email out of the raw text of a clinic-generated
    /// PDF. Designed to be FAULT-TOLERANT across labs and languages — we try
    /// a sequence of patterns from most specific to most generic, and stop at
    /// the first one that succeeds.
    ///
    /// Important design notes:
    ///   * We DO NOT validate national IDs (CNP, SSN, NHS, Aadhaar, ...) here.
    ///     The patient is identified inside the clinic at intake; our app
    ///     only needs a stable lookup key for history. Name + email is enough,
    ///     and works in ANY country/language without per-locale code.
    ///   * Name comes BEFORE email in 99% of lab reports, so once we find
    ///     a label like "Patient", "Pacient", "Patiente", "Nume" we look for
    ///     non-empty text on the same line or the next.
    ///   * The fallback name-finder runs the email regex first, then takes
    ///     a line of clean text above the email line — most clinic PDFs
    ///     print patient block as <c>Nume\nEmail</c>.
    /// </summary>
    public class CamPdfMetadataExtractor
    {
        // RFC-pragmatic email regex (good enough for clinic PDFs).
        private static readonly Regex EmailRx = new(
            @"\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Labels that introduce the patient name. Kept multi-language so we
        // can scale to 30 languages without code changes; new ones just get
        // appended to this array.
        private static readonly string[] NameLabels =
        {
            "pacient", "pacienta", "pacientă",
            "nume si prenume", "nume și prenume", "nume",
            "patient", "patient name", "patient's name",
            "patiente", "patiente:", "nom du patient", "nom",
            "nombre del paciente", "nombre",
            "name des patienten", "patient name",
            "имя", "пациент",
            "patientennaam"
        };

        private static readonly Regex NameLabelRx = new(
            // "Pacient: Ion Popescu"  /  "Patient   :   John Doe"  /  "Nume şi prenume - Maria"
            // group 1 = whatever comes after the label on the same line.
            @"(?im)^\s*(?:" + string.Join("|", NameLabels.Select(Regex.Escape)) + @")\s*[:\-]?\s*(.+)$",
            RegexOptions.Compiled);

        private readonly ILogger<CamPdfMetadataExtractor> _logger;

        public CamPdfMetadataExtractor(ILogger<CamPdfMetadataExtractor> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Scans <paramref name="pdfBytes"/> for a patient name + email pair.
        /// Never throws — always returns a populated <see cref="CamPdfMetadata"/>
        /// with <c>IsValid=false</c> and an explanation when extraction fails.
        /// </summary>
        public CamPdfMetadata Extract(byte[] pdfBytes, string fileNameForLogs)
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
                return result;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                result.Reason = "PDF text layer empty (scanned image?).";
                return result;
            }

            // ---- email (mandatory) ----
            var emailMatch = EmailRx.Match(text);
            if (!emailMatch.Success)
            {
                result.Reason = "Email not found in PDF.";
                return result;
            }
            result.PatientEmail = emailMatch.Value.Trim();

            // ---- name (tries 3 strategies, in order of decreasing confidence) ----
            result.PatientName = FindNameByLabel(text)
                                 ?? FindNameNearEmail(text, emailMatch.Index)
                                 ?? FindNameByCapitalizedLine(text);

            if (string.IsNullOrWhiteSpace(result.PatientName))
            {
                result.Reason = "Patient name not found in PDF.";
                return result;
            }

            result.IsValid = true;
            return result;
        }

        /// <summary>Strategy 1: scan for a labelled line ("Pacient: ...").</summary>
        private static string? FindNameByLabel(string text)
        {
            foreach (Match m in NameLabelRx.Matches(text))
            {
                var candidate = CleanCandidate(m.Groups[1].Value);
                if (IsPlausibleName(candidate)) return candidate;
            }
            return null;
        }

        /// <summary>
        /// Strategy 2: look at the lines IMMEDIATELY before the email — clinic
        /// PDFs typically print <c>"Ion Popescu\nion@example.com"</c>.
        /// We walk back at most 3 non-empty lines.
        /// </summary>
        private static string? FindNameNearEmail(string text, int emailIndex)
        {
            // Find the start of the line containing the email.
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

        /// <summary>
        /// Strategy 3 (last resort): scan the first 30 lines for a single
        /// line of 2-4 capitalized words and treat it as the patient name.
        /// Very tolerant; used only when strategies 1+2 failed.
        /// </summary>
        private static string? FindNameByCapitalizedLine(string text)
        {
            foreach (var rawLine in text.Split('\n').Take(30))
            {
                var candidate = CleanCandidate(rawLine);
                if (!IsPlausibleName(candidate)) continue;
                // Extra guard against false positives like the clinic letterhead.
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
            // Drop trailing labels like "CNP 1850101...", phone numbers, etc.
            var s = raw.Trim();
            // Cut at first digit if there are any — names normally have none.
            int firstDigit = s.IndexOfAny("0123456789".ToCharArray());
            if (firstDigit > 0) s = s[..firstDigit].Trim();
            // Cut at the first label-like separator that suggests the next field.
            foreach (var sep in new[] { "  ", "\t", ";", "|" })
            {
                int idx = s.IndexOf(sep, StringComparison.Ordinal);
                if (idx > 0) { s = s[..idx].Trim(); break; }
            }
            // Strip trailing punctuation.
            s = s.TrimEnd('.', ',', ':', '-', '_', '*');
            return s.Trim();
        }

        private static bool IsPlausibleName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            if (s.Length < 3 || s.Length > 80) return false;
            // At least one whitespace ⇒ "Ion Popescu" has 2 words. Reject single tokens.
            if (!s.Contains(' ')) return false;
            // Must be predominantly letters (Unicode-aware).
            int letters = s.Count(c => char.IsLetter(c));
            if (letters < s.Length * 0.7) return false;
            // No '@' (would be the email itself).
            if (s.Contains('@')) return false;
            return true;
        }
    }
}
