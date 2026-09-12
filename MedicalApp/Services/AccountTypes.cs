namespace MedicalApp.Services
{
    /// <summary>
    /// The three account types, in one place, so nobody has to remember that
    /// the string is "Clinic" and not "clinic" or "CAM".
    ///
    ///   Individual — B2C: a person and their family (cap: 20 profiles).
    ///   Clinic     — B2B / CAM: a medical analysis laboratory, batch module,
    ///                its own packages, its own dashboard.
    ///   Cabinet    — CM (June 2026): a family doctor's practice. Uses the SAME
    ///                screens as B2C (one PDF per patient, archive, charts) but
    ///                gets its own credit package and a cap of 2000 patients.
    ///
    /// Anything unrecognised normalises to Individual: the safest of the three
    /// (no CAM area, no cabinet pricing, smallest cap).
    /// </summary>
    public static class AccountTypes
    {
        public const string Individual = "Individual";
        public const string Clinic = "Clinic";
        public const string Cabinet = "Cabinet";

        public static string Normalize(string? raw) =>
            IsClinic(raw) ? Clinic : IsCabinet(raw) ? Cabinet : Individual;

        public static bool IsClinic(string? value) => Is(value, Clinic);
        public static bool IsCabinet(string? value) => Is(value, Cabinet);

        /// <summary>Individual or Cabinet: the accounts that use the B2C screens.</summary>
        public static bool UsesPersonalScreens(string? value) => !IsClinic(value);

        private static bool Is(string? value, string type) =>
            string.Equals(value?.Trim(), type, StringComparison.OrdinalIgnoreCase);
    }
}
