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
