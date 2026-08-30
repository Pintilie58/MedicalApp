using System.Text.RegularExpressions;
using MedicalApp.Models;

namespace MedicalApp.Services
{
    /// <summary>
    /// Removes the lab's internal ROUTING MARKERS that some Romanian labs print
    /// in the left margin of the report ("LLIS", "#LC", "*BC", ...) to say which
    /// machine or partner lab processed the sample. In TEXT mode the PDF text
    /// layer puts the margin and the analyte name on the same line, so the model
    /// reads "LLIS Trigliceride" as the analyte name.
    ///
    /// The detection is GENERIC — no hardcoded marker list. A routing marker is
    /// printed on MANY lines of the same report, while a real medical prefix
    /// ("LDL colesterol", "HDL colesterol") appears once or twice. So a leading
    /// token is dropped only when it prefixes at least <see cref="MinOccurrences"/>
    /// DISTINCT parameters of the same report.
    ///
    /// A short allow-list of abbreviations that legitimately start an analyte
    /// name is never touched, whatever its frequency — that is the anti-regression
    /// belt, not the mechanism.
    /// </summary>
    public static class LabMarkerSanitizer
    {
        /// <summary>How many distinct parameters a token must prefix to be a marker.</summary>
        public const int MinOccurrences = 3;

        /// <summary>Candidate marker: 2-6 uppercase letters, optional #/* prefix and trailing digit.</summary>
        private static readonly Regex MarkerShape = new(@"^[#*]?[A-Z]{2,6}\d?$", RegexOptions.Compiled);

        /// <summary>
        /// Abbreviations that really can open an analyte name. Never removed,
        /// no matter how often they appear.
        /// </summary>
        private static readonly HashSet<string> NeverRemove = new(StringComparer.OrdinalIgnoreCase)
        {
            "AC", "ACTH", "AFP", "ABO", "ALB", "ALP", "ALT", "AMH", "ANA", "APTT", "ASLO", "AST",
            "CA", "CEA", "CK", "CKMB", "CRP", "DFG", "DHEA", "DHEAS", "EGFR", "ESR", "FSH", "FT3",
            "FT4", "GFR", "GGT", "GOT", "GPT", "HB", "HBA", "HBS", "HCT", "HCV", "HDL", "HGB",
            "HIV", "IG", "IGA", "IGE", "IGG", "IGM", "INR", "LDH", "LDL", "LH", "MCH", "MCHC",
            "MCV", "MPV", "PCR", "PCT", "PDW", "PH", "PLT", "PSA", "PT", "RBC", "RDW", "RH",
            "SHBG", "TG", "TGO", "TGP", "TPHA", "TPO", "TRAB", "TSH", "VDRL", "VLDL", "VSH", "WBC",
            "T3", "T4", "B12", "B6", "D3", "K1"
        };

        /// <summary>
        /// Cleans <see cref="KeyResult.Parameter"/> (and the matching abnormal
        /// findings) in place. Returns how many parameters were changed.
        /// </summary>
        public static int Clean(InterpretationResult? result)
        {
            var keyResults = result?.KeyResults;
            if (keyResults == null || keyResults.Count == 0) return 0;

            // How many DISTINCT parameter names each candidate token opens.
            var namesByToken = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var kr in keyResults)
            {
                var token = LeadingMarkerToken(kr?.Parameter);
                if (token == null) continue;
                if (!namesByToken.TryGetValue(token, out var names))
                    namesByToken[token] = names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                names.Add(kr!.Parameter.Trim());
            }

            var markers = namesByToken
                .Where(kv => kv.Value.Count >= MinOccurrences)
                .Select(kv => kv.Key)
                .ToHashSet(StringComparer.Ordinal);

            if (markers.Count == 0) return 0;

            // Old name -> new name, so abnormal_findings stay in sync.
            var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int changed = 0;

            foreach (var kr in keyResults)
            {
                var token = LeadingMarkerToken(kr?.Parameter);
                if (token == null || !markers.Contains(token)) continue;

                var oldName = kr!.Parameter.Trim();
                var newName = StripLeadingToken(oldName);
                if (newName == null) continue;

                kr.Parameter = newName;
                renamed[oldName] = newName;
                changed++;

                // The model sometimes carries the marker into the English name
                // it sends to the LOINC matcher — clean that too.
                if (!string.IsNullOrWhiteSpace(kr.ParameterNormalizedEn) &&
                    string.Equals(LeadingMarkerToken(kr.ParameterNormalizedEn), token, StringComparison.Ordinal))
                {
                    kr.ParameterNormalizedEn = StripLeadingToken(kr.ParameterNormalizedEn!.Trim())
                                               ?? kr.ParameterNormalizedEn;
                }
            }

            foreach (var af in result!.AbnormalFindings ?? new())
                if (!string.IsNullOrWhiteSpace(af.Parameter) &&
                    renamed.TryGetValue(af.Parameter.Trim(), out var better))
                    af.Parameter = better;

            return changed;
        }

        /// <summary>
        /// The first word of <paramref name="parameter"/> when it looks like a
        /// routing marker AND a usable analyte name remains behind it,
        /// otherwise null.
        /// </summary>
        private static string? LeadingMarkerToken(string? parameter)
        {
            if (string.IsNullOrWhiteSpace(parameter)) return null;

            var parts = parameter.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;

            var token = parts[0];
            if (!MarkerShape.IsMatch(token)) return null;
            if (NeverRemove.Contains(token.TrimStart('#', '*'))) return null;

            // The rest must still read like a name: long enough and not just
            // another all-caps code (protects "TSH REFLEX", "HDL LDL RATIO").
            var rest = parts[1].Trim();
            return rest.Length >= 3 && rest.Any(char.IsLower) ? token : null;
        }

        private static string? StripLeadingToken(string parameter)
        {
            var parts = parameter.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;
            var rest = parts[1].Trim().TrimStart('-', ':', '.', '\u2013', '\u2014').Trim();
            return rest.Length >= 3 ? rest : null;
        }
    }
}
