using System.Globalization;
using System.Text;

namespace MedicalApp.Services
{
    /// <summary>
    /// Produces a stable, language-agnostic lookup key for a patient name.
    /// Used as the <c>NameKey</c> column of <c>ClinicPatient</c> and is the
    /// reason "Ion Popescu" and "POPESCU ION" and "Ștefan ȚEPEȘ" all map to
    /// the SAME patient row across uploads.
    ///
    /// Pipeline applied to the raw display name:
    ///   1. Trim + collapse multiple whitespaces.
    ///   2. Strip diacritics (Unicode NFD + remove non-spacing marks).
    ///      "Ștefan" → "Stefan", "Țepeș" → "Tepes", "Müller" → "Muller",
    ///      "Чехов" → "Чехов" (Cyrillic has no separable diacritics, so it
    ///      passes through unchanged — which is the desired behaviour).
    ///   3. Lowercase (invariant culture, so locale-specific casing like
    ///      Turkish "İ" doesn't surprise us).
    ///   4. Split by whitespace, sort the tokens alphabetically and rejoin
    ///      with a single space. This makes "Popescu Ion" and "Ion Popescu"
    ///      produce the SAME key — a common lab-PDF inconsistency.
    ///   5. Keep only letters, digits and single spaces — drop punctuation
    ///      ("." in middle initials, ",", "-", quotes, etc.).
    /// </summary>
    public static class CamPatientKey
    {
        public static string Normalize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            // 1+2: NFD + strip non-spacing marks (diacritics).
            var nfd = raw.Trim().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(nfd.Length);
            foreach (var c in nfd)
            {
                var cat = CharUnicodeInfo.GetUnicodeCategory(c);
                if (cat == UnicodeCategory.NonSpacingMark) continue;
                sb.Append(c);
            }
            var stripped = sb.ToString().Normalize(NormalizationForm.FormC);

            // 3: invariant lowercase
            stripped = stripped.ToLowerInvariant();

            // 5 (before 4 so tokens are clean): keep letters/digits/spaces only.
            var clean = new StringBuilder(stripped.Length);
            foreach (var c in stripped)
            {
                if (char.IsLetterOrDigit(c)) clean.Append(c);
                else if (char.IsWhiteSpace(c)) clean.Append(' ');
                // anything else (.,'"- etc.) is dropped silently.
            }

            // 4: split, sort alphabetically, rejoin with single space.
            var tokens = clean.ToString()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToArray();

            return string.Join(' ', tokens);
        }
    }
}
