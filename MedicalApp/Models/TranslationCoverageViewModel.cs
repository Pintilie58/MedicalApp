using System.Collections.Generic;

namespace MedicalApp.Models
{
    /// <summary>
    /// Data for the Admin "Translation coverage" dashboard. EN is treated as
    /// the master/source of truth (every key must exist in EN), and every
    /// other language is reported by how completely it covers that master key
    /// set. Used to spot keys forgotten during translation phases.
    /// </summary>
    public class TranslationCoverageViewModel
    {
        /// <summary>Total number of keys in the EN dictionary (the "master").</summary>
        public int TotalEnKeys { get; set; }

        /// <summary>One row per non-EN language with coverage stats + the missing keys list.</summary>
        public List<LanguageCoverage> Languages { get; set; } = new();

        /// <summary>Top 10 longest translation values across all languages — handy for layout testing.</summary>
        public List<LongestTranslation> Longest { get; set; } = new();

        public class LanguageCoverage
        {
            public string Lang { get; set; } = "";          // "ro", "fr", ...
            public int TotalKeys { get; set; }              // count of keys present in this language
            public List<string> MissingKeys { get; set; } = new();  // EN keys NOT in this language
            public List<string> ExtraKeys { get; set; } = new();    // keys in this lang NOT in EN (drift)
            public double CoveragePct { get; set; }         // 0..100
        }

        public class LongestTranslation
        {
            public string Lang { get; set; } = "";
            public string Key { get; set; } = "";
            public int Length { get; set; }
            public string Preview { get; set; } = "";       // first ~120 chars for the table
        }
    }
}
