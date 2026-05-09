namespace MedicalApp.Services
{
    /// <summary>
    /// Lightweight context about the patient that the AI prompt uses to resolve
    /// risk-tiered reference thresholds (e.g. lipid panel targets that depend on
    /// the patient's cardiovascular-risk category).
    ///
    /// All fields are optional. Null means "unknown" — the prompt falls back to
    /// the multi-threshold rule (strictest-satisfied) when no risk is declared.
    /// </summary>
    public record PatientContext(
        string? CardiovascularRisk,   // "low_moderate" | "high" | "very_high" | null
        int? AgeYears,                // computed from BirthYear when available
        string? Gender);              // "M" | "F" | null
}
