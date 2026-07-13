using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MedicalApp.Services
{
    /// <summary>
    /// Tolerant parser for the "sampling date" string that Gemini extracts from
    /// medical PDFs. Lab reports print the recoltare date in dozens of
    /// inconsistent ways:
    ///   "27/01/2014"
    ///   "27.01.2014"
    ///   "27-01-2014"
    ///   "2014-01-27"
    ///   "27/01/2014 14:30"
    ///   "06.12.2023 - 10:27"
    ///   "Data - ora recoltare: 06.12.2023 - 10:27"
    ///   "Data recoltării: 06.12.2023, ora 10:27"
    ///   "27 Jan 2014"
    /// The previous DateTime.TryParseExact-only logic was strict on whitespace
    /// and label prefixes, so anything fancier than "dd.MM.yyyy" silently
    /// returned null and the Compare PDF fell back to the processing date.
    ///
    /// Strategy: scan the raw string with regex, extract the FIRST date-like
    /// token (numeric or named-month), parse that token. This works regardless
    /// of any Romanian / French label, separator, or trailing time fragment.
    /// </summary>
    public static class SamplingDateParser
    {
        // Numeric date patterns. Two distinct regexes — one for ISO-leading
        // (yyyy first), one for European/US-leading (day or month first) — so
        // we never need duplicate named groups across alternations.
        private static readonly Regex IsoDate = new(
            @"(?<!\d)(?<y>\d{4})[-/\.](?<m>\d{1,2})[-/\.](?<d>\d{1,2})(?!\d)",
            RegexOptions.Compiled);

        private static readonly Regex DmyDate = new(
            @"(?<!\d)(?<d>\d{1,2})[\-/\.](?<m>\d{1,2})[\-/\.](?<y>\d{2,4})(?!\d)",
            RegexOptions.Compiled);

        // Named-month patterns (English/Romanian/French/Spanish/German/Italian/Portuguese
        // short or long). Two distinct regexes — "27 Jan 2014" vs "Jan 27, 2014" — so we
        // don't need duplicate named groups inside a single pattern.
        //
        // Character class is `\p{L}+` (any Unicode letter) so we correctly capture
        // month names with German umlauts ("März"), Portuguese cedilla ("março"),
        // and any future language added to SupportedLanguagesConfig (e.g. Polish
        // "styczeń"). The surrounding \d and \s anchors keep the match tight.
        private static readonly Regex NamedDateDmy = new(
            @"(?<d>\d{1,2})\s+(?<mon>\p{L}+)\.?\s+(?<y>\d{4})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex NamedDateMdy = new(
            @"(?<mon>\p{L}+)\.?\s+(?<d>\d{1,2}),?\s+(?<y>\d{4})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // ---- MonthLookup: built ONCE at class init from
        // ---- SupportedLanguagesConfig × CultureInfo (CLDR) data.
        // ----
        // ---- Adding a new supported language auto-populates its months here
        // ---- (no code edit needed). CLDR-provided names are stored both as
        // ---- printed and — for names that carry diacritics — in an accent-
        // ---- stripped form, since lab OCR sometimes drops the diacritics
        // ---- ("aout" instead of "août", "marco" instead of "março").
        // ----
        // ---- BuildMonthLookup() is separated for readability & unit-testability.
        // ---- StringComparer.OrdinalIgnoreCase mirrors the previous behaviour.
        private static readonly Dictionary<string, int> MonthLookup = BuildMonthLookup();

        private static Dictionary<string, int> BuildMonthLookup()
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            // 1) CLDR data for every supported culture.
            //    MonthGenitiveNames matters for Slavic langs (Polish, Czech…) where
            //    the printed form differs from the lookup form. For our current
            //    Romance/Germanic set it returns the same as MonthNames.
            foreach (var cultureCode in SupportedLanguagesConfig.CultureCodes)
            {
                CultureInfo ci;
                try { ci = CultureInfo.GetCultureInfo(cultureCode); }
                catch (CultureNotFoundException) { continue; }

                var dtf = ci.DateTimeFormat;
                for (int m = 0; m < 12; m++)
                {
                    Register(map, dtf.MonthNames[m], m + 1);
                    Register(map, dtf.AbbreviatedMonthNames[m], m + 1);
                    Register(map, dtf.MonthGenitiveNames[m], m + 1);
                    Register(map, dtf.AbbreviatedMonthGenitiveNames[m], m + 1);
                }
            }

            // 2) Real-world lab variants NOT in CLDR. Each entry is a colloquial
            //    or historical form observed in production lab reports. This list
            //    is verified against the previous hardcoded lookup: every alias
            //    that used to work still resolves to the same month.
            var supplement = new (string alias, int month)[]
            {
                ("sept", 9),      // English/Italian colloquial for September
                ("noi",  11),     // Romanian colloquial for noiembrie
                ("sett", 9),      // Italian colloquial for settembre
                ("marco", 3),     // Portuguese "março" without cedilla (some labs)
                ("aout", 8),      // French "août" without circumflex
                ("decembre", 12), // French "décembre" without accent
                ("fevrier", 2),   // French "février" without accent
            };
            foreach (var (alias, month) in supplement) map[alias] = month;

            return map;
        }

        /// <summary>Adds <paramref name="name"/> to <paramref name="map"/>, plus an
        /// accent-stripped duplicate when the name carries diacritics. No-op on
        /// null/whitespace input — safe to call with any DateTimeFormatInfo entry.</summary>
        private static void Register(Dictionary<string, int> map, string? name, int month)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var trimmed = name.Trim().TrimEnd('.');
            if (trimmed.Length == 0) return;

            map[trimmed] = month;

            var ascii = StripDiacritics(trimmed);
            if (!string.Equals(ascii, trimmed, StringComparison.Ordinal))
                map[ascii] = month;
        }

        /// <summary>Returns <paramref name="s"/> with combining diacritical marks
        /// removed — Unicode NFD → drop <c>Mn</c> category → NFC. Used so
        /// "août" also matches "aout" and "März" also matches "Marz".</summary>
        private static string StripDiacritics(string s)
        {
            var normalized = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (var c in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        public static DateTime? TryParse(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var s = raw.Trim();

            // 1. Numeric pattern (most common). Try ISO (yyyy-first) before
            //    dd-first so "2023-12-06" doesn't get mis-read as 23-12-(20)06.
            var iso = IsoDate.Match(s);
            if (iso.Success)
            {
                int y = int.Parse(iso.Groups["y"].Value, CultureInfo.InvariantCulture);
                int m = int.Parse(iso.Groups["m"].Value, CultureInfo.InvariantCulture);
                int d = int.Parse(iso.Groups["d"].Value, CultureInfo.InvariantCulture);
                if (TryBuild(y, m, d, out var result)) return result;
            }
            var dmy = DmyDate.Match(s);
            if (dmy.Success)
            {
                int day = int.Parse(dmy.Groups["d"].Value, CultureInfo.InvariantCulture);
                int month = int.Parse(dmy.Groups["m"].Value, CultureInfo.InvariantCulture);
                int year = int.Parse(dmy.Groups["y"].Value, CultureInfo.InvariantCulture);
                if (year < 100) year += year < 50 ? 2000 : 1900; // 2-digit year heuristic

                // If "day" looks like a month and "month" looks like a US-day
                // (e.g. raw was "01/27/2014" — d=1, m=27), swap. Otherwise keep dd/mm.
                if (month > 12 && day <= 12)
                {
                    (day, month) = (month, day);
                }
                if (TryBuild(year, month, day, out var result)) return result;
            }

            // 2. Named-month pattern.
            foreach (var rx in new[] { NamedDateDmy, NamedDateMdy })
            {
                var nm = rx.Match(s);
                if (!nm.Success) continue;
                var monRaw = nm.Groups["mon"].Value.Trim().TrimEnd('.').ToLowerInvariant();
                if (!MonthLookup.TryGetValue(monRaw, out var month)) continue;
                int day = int.Parse(nm.Groups["d"].Value, CultureInfo.InvariantCulture);
                int year = int.Parse(nm.Groups["y"].Value, CultureInfo.InvariantCulture);
                if (TryBuild(year, month, day, out var result)) return result;
            }

            // 3. Last-ditch: hand the whole string to .NET's culture-aware parser.
            // Culture list routed through SupportedLanguagesConfig — adding a
            // new language auto-registers it here.
            var cultures = SupportedLanguagesConfig.CultureCodes;
            foreach (var cult in cultures)
            {
                var ci = CultureInfo.GetCultureInfo(cult);
                if (DateTime.TryParse(s, ci, DateTimeStyles.AssumeLocal, out var any))
                    return any;
            }
            return DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var any2)
                ? any2 : (DateTime?)null;
        }

        private static bool TryBuild(int y, int m, int d, out DateTime when)
        {
            try
            {
                when = new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Local);
                return true;
            }
            catch
            {
                when = default;
                return false;
            }
        }
    }
}
