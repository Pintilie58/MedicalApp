using System.Text.Json.Serialization;

namespace MedicalApp.Models
{
    /// <summary>
    /// Strongly-typed result returned by GPT-4o-mini for a medical analysis PDF.
    /// Mirrors the JSON schema described in the system prompt.
    /// </summary>
    public class InterpretationResult
    {
        [JsonPropertyName("is_medical_analysis")]
        public bool IsMedicalAnalysis { get; set; }

        [JsonPropertyName("rejection_reason")]
        public string? RejectionReason { get; set; }

        [JsonPropertyName("patient_info")]
        public PatientInfo? PatientInfo { get; set; }

        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("key_results")]
        public List<KeyResult>? KeyResults { get; set; }

        [JsonPropertyName("abnormal_findings")]
        public List<AbnormalFinding>? AbnormalFindings { get; set; }

        [JsonPropertyName("correlations")]
        public string? Correlations { get; set; }

        [JsonPropertyName("recommendations")]
        public string? Recommendations { get; set; }

        [JsonPropertyName("disclaimer")]
        public string? Disclaimer { get; set; }

        /// <summary>
        /// Self-audit field added by the model so we can detect silent extraction
        /// gaps. If <see cref="ExtractionAudit.ExpectedCount"/> &gt; the number of
        /// items in <see cref="KeyResults"/>, the model skipped parameters and we
        /// retry the call.
        /// </summary>
        [JsonPropertyName("_extraction_audit")]
        public ExtractionAudit? Audit { get; set; }
    }

    public class ExtractionAudit
    {
        [JsonPropertyName("expected_count")]
        public int ExpectedCount { get; set; }

        [JsonPropertyName("parameter_names")]
        public List<string>? ParameterNames { get; set; }
    }

        public class PatientInfo
        {
            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("age")]
            public string? Age { get; set; }

            [JsonPropertyName("sex")]
            public string? Sex { get; set; }

            [JsonPropertyName("date_taken")]
            public string? DateTaken { get; set; }

            [JsonPropertyName("laboratory")]
            public string? Laboratory { get; set; }

            [JsonPropertyName("doctor_requesting")]
            public string? DoctorRequesting { get; set; }
        }

        public class KeyResult
        {
            [JsonPropertyName("parameter")]
            public string Parameter { get; set; } = string.Empty;

            [JsonPropertyName("value")]
            public string? Value { get; set; }

            [JsonPropertyName("unit")]
            public string? Unit { get; set; }

            [JsonPropertyName("reference_range")]
            public string? ReferenceRange { get; set; }

            /// <summary>normal | high | low | borderline</summary>
            [JsonPropertyName("status")]
            public string Status { get; set; } = "normal";

            [JsonPropertyName("explanation")]
            public string? Explanation { get; set; }

            // -------- LOINC mapping (Pas 2) --------
            // The model is asked to identify a LOINC code for each extracted
            // parameter. The three fields below are OPTIONAL on the wire:
            // older PDFs re-interpreted on cached prompts may not have them,
            // and the model is explicitly allowed to emit nulls when it is
            // not confident. Validation against our LoincDictionary happens
            // later (Pas 3) - here we only DESERIALIZE what came back.

            /// <summary>
            /// Official LOINC code chosen by Gemini for this parameter,
            /// e.g. "2324-2" for GGT. May be null when Gemini is unsure or
            /// when the test has no LOINC counterpart (rare lab-specific
            /// indices).
            /// </summary>
            [JsonPropertyName("loinc_code")]
            public string? LoincCode { get; set; }

            /// <summary>
            /// Long common name of the LOINC code as recalled by Gemini.
            /// Used by the future validator (Pas 3) to sanity-check the code:
            /// if the model's <see cref="LoincCode"/> resolves to a different
            /// long name in our dictionary, the mapping is suspect.
            /// </summary>
            [JsonPropertyName("loinc_long_name")]
            public string? LoincLongName { get; set; }

            /// <summary>
            /// Self-reported confidence: "high" | "medium" | "low" | null.
            /// "low" or null = treat the mapping as a guess; do not group
            /// by code in evolution charts until reviewed.
            /// </summary>
            [JsonPropertyName("loinc_confidence")]
            public string? LoincConfidence { get; set; }
        }

        public class AbnormalFinding
        {
            [JsonPropertyName("parameter")]
            public string Parameter { get; set; } = string.Empty;

            [JsonPropertyName("explanation")]
            public string? Explanation { get; set; }

            /// <summary>mild | moderate | severe</summary>
            [JsonPropertyName("severity")]
            public string Severity { get; set; } = "mild";
        }
    }

