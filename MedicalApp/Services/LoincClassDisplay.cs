namespace MedicalApp.Services
{
    /// <summary>
    /// Maps LOINC CLASS codes (HEM, CHEM, SERO, ENDO, COAG, UA, ...) to:
    ///   - a Romanian display label (used as a group header in the Compare view),
    ///   - a sort priority (lower = appears first in the medical-specialty ordering).
    ///
    /// LOINC CLASS values are the OFFICIAL specialty classification published
    /// by Regenstrief in <c>Loinc.csv</c> (column <c>CLASS</c>). They are
    /// stable across releases. We curate the mapping here for the classes
    /// that show up in routine outpatient lab reports; uncurated classes
    /// fall through to a generic "Alte analize" bucket (priority 999) so
    /// nothing is ever LOST in the Compare view.
    /// </summary>
    public static class LoincClassDisplay
    {
        // Lower priority -> appears earlier on the page. Gaps (10, 20, 30 ...)
        // make future insertions easy without renumbering everything.
        // The ordering follows the typical structure of a Romanian lab report
        // PDF: hematology first, then coagulation, biochemistry, endocrinology,
        // immunology/serology, urine, microbiology, toxicology, others.
        private static readonly Dictionary<string, (int Priority, string Label)> _Map = new(System.StringComparer.OrdinalIgnoreCase)
        {
            // ----- Hematology family -----
            ["HEM/BC"]     = (10, "Hematologie"),
            ["HEM"]        = (10, "Hematologie"),
            ["CELLMARK"]   = (15, "Imunofenotipare / Markeri celulari"),

            // ----- Coagulation -----
            ["COAG"]       = (20, "Coagulare"),

            // ----- Chemistry (the bulk of biochemistry) -----
            ["CHEM"]       = (30, "Biochimie serică"),

            // ----- Endocrinology / hormones -----
            ["DRUG/TOX"]   = (35, "Toxicologie / Medicamente"),
            ["HORMONE"]    = (40, "Endocrinologie / Hormoni"),
            ["ENDO"]       = (40, "Endocrinologie / Hormoni"),

            // ----- Tumor markers -----
            ["TUMOR MARKERS"] = (45, "Markeri tumorali"),

            // ----- Immunology / serology / allergy -----
            ["SERO"]       = (50, "Serologie / Imunologie"),
            ["ABXBACT"]    = (52, "Anticorpi antibacterieni"),
            ["ABXVIR"]     = (52, "Anticorpi antivirali"),
            ["ABXFUNG"]    = (52, "Anticorpi antifungici"),
            ["ABXPARA"]    = (52, "Anticorpi antiparazitari"),
            ["ALLERGY"]    = (55, "Alergologie"),

            // ----- Urinalysis -----
            ["UA"]         = (60, "Biochimie urinară"),
            ["URINE"]      = (60, "Biochimie urinară"),

            // ----- Microbiology / parasitology -----
            ["MICRO"]      = (70, "Microbiologie"),
            ["BACT"]       = (70, "Microbiologie"),
            ["MYCO"]       = (72, "Micologie"),
            ["PARASITE"]   = (74, "Parazitologie"),
            ["VIR"]        = (76, "Virusologie"),

            // ----- Toxicology / drug monitoring -----
            ["TOX"]        = (80, "Toxicologie"),
            ["DRUG"]       = (80, "Toxicologie / Medicamente"),

            // ----- Cytology / pathology -----
            ["CYTO"]       = (90, "Citologie"),
            ["PATH"]       = (90, "Anatomie patologică"),

            // ----- Histocompatibility / molecular -----
            ["HLA"]        = (95, "Histocompatibilitate (HLA)"),
            ["MOLPATH"]    = (96, "Genetică / Patologie moleculară"),

            // ----- Vitamins / nutrition (chemistry subset) -----
            ["NUTRITION"]  = (110, "Nutriție / Vitamine"),

            // ----- Imaging / functional (rare in lab reports) -----
            ["RAD"]        = (200, "Imagistică"),
            ["CARD"]       = (210, "Cardiologie"),
        };

        /// <summary>Display label for the group header in the Compare view.</summary>
        public static string GetLabel(string? classCode)
        {
            if (string.IsNullOrWhiteSpace(classCode))
                return "Alte analize";

            // Some Loinc.csv rows put two slash-separated tokens, e.g. "HEM/BC".
            // We try the full token first, then the head before the slash.
            if (_Map.TryGetValue(classCode.Trim(), out var v))
                return v.Label;

            var head = classCode.Trim().Split('/')[0];
            if (_Map.TryGetValue(head, out v))
                return v.Label;

            // Unknown class — show the raw code so the operator can see what
            // came in and decide whether to add it to the mapping.
            return $"Alte ({classCode.Trim()})";
        }

        /// <summary>Sort priority. Lower comes first.</summary>
        public static int GetPriority(string? classCode)
        {
            if (string.IsNullOrWhiteSpace(classCode))
                return 999;

            if (_Map.TryGetValue(classCode.Trim(), out var v))
                return v.Priority;

            var head = classCode.Trim().Split('/')[0];
            if (_Map.TryGetValue(head, out v))
                return v.Priority;

            // Unknown class — between curated ones and the truly-empty bucket.
            return 900;
        }
    }
}
