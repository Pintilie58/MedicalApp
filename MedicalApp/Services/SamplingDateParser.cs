using System.Globalization;
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

        // Named-month patterns (English/Romanian/French short or long).
        // Two distinct regexes — "27 Jan 2014" vs "Jan 27, 2014" — so we
        // don't need duplicate named groups inside a single pattern.
        private static readonly Regex NamedDateDmy = new(
            @"(?<d>\d{1,2})\s+(?<mon>[A-Za-zĂÂÎȘȚăâîșțéèêûôàâç]+)\.?\s+(?<y>\d{4})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex NamedDateMdy = new(
            @"(?<mon>[A-Za-zĂÂÎȘȚăâîșțéèêûôàâç]+)\.?\s+(?<d>\d{1,2}),?\s+(?<y>\d{4})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Months in English, Romanian, French — short + long, lowercased.
        private static readonly Dictionary<string, int> MonthLookup = new(StringComparer.OrdinalIgnoreCase)
        {
            // English
            { "jan", 1 }, { "january", 1 },
            { "feb", 2 }, { "february", 2 },
            { "mar", 3 }, { "march", 3 },
            { "apr", 4 }, { "april", 4 },
            { "may", 5 },
            { "jun", 6 }, { "june", 6 },
            { "jul", 7 }, { "july", 7 },
            { "aug", 8 }, { "august", 8 },
            { "sep", 9 }, { "sept", 9 }, { "september", 9 },
            { "oct", 10 }, { "october", 10 },
            { "nov", 11 }, { "november", 11 },
            { "dec", 12 }, { "december", 12 },
            // Romanian
            { "ian", 1 }, { "ianuarie", 1 },
            { "februarie", 2 },
            { "martie", 3 },
            { "aprilie", 4 },
            { "mai", 5 },
            { "iun", 6 }, { "iunie", 6 },
            { "iul", 7 }, { "iulie", 7 },
            { "noi", 11 }, { "noiembrie", 11 },
            { "decembrie", 12 },
            // French
            { "janv", 1 }, { "janvier", 1 },
            { "févr", 2 }, { "fevrier", 2 }, { "février", 2 },
            { "mars", 3 },
            { "avr", 4 }, { "avril", 4 },
            { "juin", 6 },
            { "juil", 7 }, { "juillet", 7 },
            { "août", 8 }, { "aout", 8 },
            { "déc", 12 }, { "décembre", 12 }, { "decembre", 12 },
        };

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
            string[] cultures = { "en-US", "ro-RO", "fr-FR", "es-ES", "de-DE" };
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
