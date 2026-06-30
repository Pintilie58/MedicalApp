namespace MedicalApp.Services
{
    /// <summary>
    /// Maps LOINC CLASS codes (HEM, CHEM, SERO, ENDO, COAG, UA, ...) to:
    ///   - a localized display label (used as a group header in the Compare view),
    ///   - a sort priority (lower = appears first in the medical-specialty ordering).
    ///
    /// LOINC CLASS values are the OFFICIAL specialty classification published
    /// by Regenstrief in <c>Loinc.csv</c> (column <c>CLASS</c>). They are
    /// stable across releases. We curate the mapping here for the classes
    /// that show up in routine outpatient lab reports; uncurated classes
    /// fall through to a generic "Other analyses" bucket (priority 999) so
    /// nothing is ever LOST in the Compare view.
    ///
    /// Labels are resolved via <see cref="Loc"/> using the current UI culture
    /// so the Compare view headers (and any other consumer) follow the
    /// language the user has selected.
    /// </summary>
    public static class LoincClassDisplay
    {
        // Lower priority -> appears earlier on the page. Gaps (10, 20, 30 ...)
        // make future insertions easy without renumbering everything.
        // The ordering follows the typical structure of a Romanian lab report
        // PDF: hematology first, then coagulation, biochemistry, endocrinology,
        // immunology/serology, urine, microbiology, toxicology, others.
        // Labels are stored as Loc keys; resolved at lookup time.
        private static readonly Dictionary<string, (int Priority, string LocKey)> _Map = new(System.StringComparer.OrdinalIgnoreCase)
        {
            // ----- Hematology family -----
            ["HEM/BC"]     = (10, "LoincClass_Hematology"),
            ["HEM"]        = (10, "LoincClass_Hematology"),
            ["CELLMARK"]   = (15, "LoincClass_CellMark"),

            // ----- Coagulation -----
            ["COAG"]       = (20, "LoincClass_Coagulation"),

            // ----- Chemistry (the bulk of biochemistry) -----
            ["CHEM"]       = (30, "LoincClass_Chemistry"),

            // ----- Endocrinology / hormones -----
            ["DRUG/TOX"]   = (35, "LoincClass_DrugTox"),
            ["HORMONE"]    = (40, "LoincClass_Hormone"),
            ["ENDO"]       = (40, "LoincClass_Hormone"),

            // ----- Tumor markers -----
            ["TUMOR MARKERS"] = (45, "LoincClass_TumorMarkers"),

            // ----- Immunology / serology / allergy -----
            ["SERO"]       = (50, "LoincClass_Serology"),
            ["ABXBACT"]    = (52, "LoincClass_AbxBact"),
            ["ABXVIR"]     = (52, "LoincClass_AbxVir"),
            ["ABXFUNG"]    = (52, "LoincClass_AbxFung"),
            ["ABXPARA"]    = (52, "LoincClass_AbxPara"),
            ["ALLERGY"]    = (55, "LoincClass_Allergy"),

            // ----- Urinalysis -----
            ["UA"]         = (60, "LoincClass_Urine"),
            ["URINE"]      = (60, "LoincClass_Urine"),

            // ----- Microbiology / parasitology -----
            ["MICRO"]      = (70, "LoincClass_Microbiology"),
            ["BACT"]       = (70, "LoincClass_Microbiology"),
            ["MYCO"]       = (72, "LoincClass_Mycology"),
            ["PARASITE"]   = (74, "LoincClass_Parasitology"),
            ["VIR"]        = (76, "LoincClass_Virology"),

            // ----- Toxicology / drug monitoring -----
            ["TOX"]        = (80, "LoincClass_Toxicology"),
            ["DRUG"]       = (80, "LoincClass_DrugTox"),

            // ----- Cytology / pathology -----
            ["CYTO"]       = (90, "LoincClass_Cytology"),
            ["PATH"]       = (90, "LoincClass_Pathology"),

            // ----- Histocompatibility / molecular -----
            ["HLA"]        = (95, "LoincClass_HLA"),
            ["MOLPATH"]    = (96, "LoincClass_MolPath"),

            // ----- Vitamins / nutrition (chemistry subset) -----
            ["NUTRITION"]  = (110, "LoincClass_Nutrition"),

            // ----- Imaging / functional (rare in lab reports) -----
            ["RAD"]        = (200, "LoincClass_Imaging"),
            ["CARD"]       = (210, "LoincClass_Cardiology"),
        };

        /// <summary>Display label for the group header in the Compare view, in the current UI culture.</summary>
        public static string GetLabel(string? classCode)
        {
            if (string.IsNullOrWhiteSpace(classCode))
                return Loc.T("LoincClass_OtherAnalyses");

            // Some Loinc.csv rows put two slash-separated tokens, e.g. "HEM/BC".
            // We try the full token first, then the head before the slash.
            if (_Map.TryGetValue(classCode.Trim(), out var v))
                return Loc.T(v.LocKey);

            var head = classCode.Trim().Split('/')[0];
            if (_Map.TryGetValue(head, out v))
                return Loc.T(v.LocKey);

            // Unknown class — show the raw code so the operator can see what
            // came in and decide whether to add it to the mapping.
            return string.Format(Loc.T("LoincClass_OtherFmt"), classCode.Trim());
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
