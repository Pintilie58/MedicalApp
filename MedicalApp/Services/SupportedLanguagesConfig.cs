namespace MedicalApp.Services
{
    /// <summary>
    /// Single source of truth for every language MedicalApp+ supports.
    ///
    /// <para>
    /// <b>Why this file exists</b>: historically, "add a new language" required
    /// touching 9 different files with 9 different hardcoded lists / dictionaries
    /// / switches, and forgetting even one of them produced subtle bugs (e.g.
    /// the Italian regression in Feb 2026: UI was correctly translated via
    /// <see cref="Loc"/> but <see cref="GeminiMedicalInterpretationService"/>
    /// fell back to English because its private <c>LanguageNames</c> dictionary
    /// did not contain <c>"it"</c>). This class centralizes ALL of those lists
    /// so adding a new language requires editing exactly ONE array entry below,
    /// plus populating the <see cref="Loc"/> dictionary.
    /// </para>
    ///
    /// <para>
    /// <b>Consumers</b> (to be wired in Phase 3 Step 2, currently still using
    /// their own local copies):
    /// </para>
    /// <list type="bullet">
    ///   <item><see cref="Loc"/> — the <c>_translations</c> per-language dict keys</item>
    ///   <item><c>Program.cs</c> — RequestLocalization <c>supportedCultures</c></item>
    ///   <item><see cref="GeminiMedicalInterpretationService"/> — the <c>LanguageNames</c> dict used to fill <c>{LANGUAGE_NAME}</c> in the system prompt</item>
    ///   <item><c>MedicalInterpretationService</c> — same dict for the OpenAI fallback provider</item>
    ///   <item><c>CamBatchService.SetBatchCultureAsync</c> — the <c>cultureName</c> switch that sets <c>CurrentUICulture</c> for a batch thread</item>
    ///   <item><see cref="SamplingDateParser"/> — the <c>MonthLookup</c> dict + culture fallback list used to parse sample dates from PDFs</item>
    ///   <item><c>Views/Shared/_Layout.cshtml</c> — the JS <c>supported</c> array used by the auto-detect helper</item>
    ///   <item><c>Views/Home/Landing.cshtml</c> — the flag+label dropdown</item>
    ///   <item><c>Views/Home/Index.cshtml</c> — the Auth-page language selector</item>
    /// </list>
    ///
    /// <para>
    /// <b>How to add a new language</b>: append a new <see cref="LangDef"/>
    /// entry to <see cref="All"/> below, then populate the corresponding
    /// dictionary in <see cref="Loc"/>. All nine consumer sites listed above
    /// will pick it up automatically.
    /// </para>
    /// </summary>
    public static class SupportedLanguagesConfig
    {
        /// <summary>Definition of one supported language / locale.</summary>
        /// <param name="Code">ISO 639-1 two-letter code, lowercase. Used as the
        ///     dictionary key throughout the codebase (e.g. <c>"it"</c>).</param>
        /// <param name="CultureCode">Full .NET culture code (e.g. <c>"it-IT"</c>).
        ///     Used by <c>CultureInfo.GetCultureInfo</c> and by <c>SamplingDateParser</c>'s
        ///     fallback list.</param>
        /// <param name="LangName">Human-readable name injected into the Gemini
        ///     system prompt at the <c>{LANGUAGE_NAME}</c> placeholder. Format:
        ///     "English name (Native name)" — e.g. <c>"Italian (Italiano)"</c>.
        ///     Both names give Gemini the maximum chance of disambiguation.</param>
        /// <param name="NativeName">The name of the language IN that language
        ///     (endonym). Used in the UI dropdowns so a user sees
        ///     "Italiano" not "Italian".</param>
        /// <param name="FlagEmoji">Two regional-indicator codepoints producing
        ///     a flag on modern browsers/OSes. Falls back to "II" pill on
        ///     older Windows Chrome — still recognizable.</param>
        /// <param name="MonthsLong">Long month names in lowercase, index 0 = January.
        ///     Consumed by <c>SamplingDateParser.MonthLookup</c> to recognize
        ///     e.g. "27 gennaio 2014" in Italian PDFs.</param>
        /// <param name="MonthsShort">Short month names (3-4 chars) in lowercase,
        ///     index 0 = January. Same purpose as <paramref name="MonthsLong"/>.</param>
        public record LangDef(
            string Code,
            string CultureCode,
            string LangName,
            string NativeName,
            string FlagEmoji,
            string[] MonthsLong,
            string[] MonthsShort);

        /// <summary>
        /// The complete list of supported languages. ORDER MATTERS — this is
        /// the order in which languages appear in the UI dropdowns and in the
        /// Admin "Translation Coverage" table. English first (default fallback),
        /// then Romanian (project home country), then FR/ES/DE/IT in Romance
        /// family / add-order.
        /// </summary>
        public static readonly IReadOnlyList<LangDef> All = new List<LangDef>
        {
            new(
                Code:        "en",
                CultureCode: "en-US",
                LangName:    "English",
                NativeName:  "English",
                FlagEmoji:   "\U0001F1EC\U0001F1E7",  // 🇬🇧
                MonthsLong:  new[] { "january","february","march","april","may","june",
                                     "july","august","september","october","november","december" },
                MonthsShort: new[] { "jan","feb","mar","apr","may","jun",
                                     "jul","aug","sep","oct","nov","dec" }
            ),
            new(
                Code:        "ro",
                CultureCode: "ro-RO",
                LangName:    "Romanian (Română)",
                NativeName:  "Română",
                FlagEmoji:   "\U0001F1F7\U0001F1F4",  // 🇷🇴
                MonthsLong:  new[] { "ianuarie","februarie","martie","aprilie","mai","iunie",
                                     "iulie","august","septembrie","octombrie","noiembrie","decembrie" },
                MonthsShort: new[] { "ian","feb","mar","apr","mai","iun",
                                     "iul","aug","sep","oct","noi","dec" }
            ),
            new(
                Code:        "fr",
                CultureCode: "fr-FR",
                LangName:    "French (Français)",
                NativeName:  "Français",
                FlagEmoji:   "\U0001F1EB\U0001F1F7",  // 🇫🇷
                MonthsLong:  new[] { "janvier","février","mars","avril","mai","juin",
                                     "juillet","août","septembre","octobre","novembre","décembre" },
                MonthsShort: new[] { "janv","févr","mars","avr","mai","juin",
                                     "juil","août","sept","oct","nov","déc" }
            ),
            new(
                Code:        "es",
                CultureCode: "es-ES",
                LangName:    "Spanish (Español)",
                NativeName:  "Español",
                FlagEmoji:   "\U0001F1EA\U0001F1F8",  // 🇪🇸
                MonthsLong:  new[] { "enero","febrero","marzo","abril","mayo","junio",
                                     "julio","agosto","septiembre","octubre","noviembre","diciembre" },
                MonthsShort: new[] { "ene","feb","mar","abr","may","jun",
                                     "jul","ago","sep","oct","nov","dic" }
            ),
            new(
                Code:        "de",
                CultureCode: "de-DE",
                LangName:    "German (Deutsch)",
                NativeName:  "Deutsch",
                FlagEmoji:   "\U0001F1E9\U0001F1EA",  // 🇩🇪
                MonthsLong:  new[] { "januar","februar","märz","april","mai","juni",
                                     "juli","august","september","oktober","november","dezember" },
                MonthsShort: new[] { "jan","feb","mär","apr","mai","jun",
                                     "jul","aug","sep","okt","nov","dez" }
            ),
            new(
                Code:        "it",
                CultureCode: "it-IT",
                LangName:    "Italian (Italiano)",
                NativeName:  "Italiano",
                FlagEmoji:   "\U0001F1EE\U0001F1F9",  // 🇮🇹
                MonthsLong:  new[] { "gennaio","febbraio","marzo","aprile","maggio","giugno",
                                     "luglio","agosto","settembre","ottobre","novembre","dicembre" },
                MonthsShort: new[] { "gen","feb","mar","apr","mag","giu",
                                     "lug","ago","set","ott","nov","dic" }
            )
        };

        // ============================================================
        //                     Convenience helpers
        // ============================================================

        /// <summary>All 2-letter language codes in declaration order.</summary>
        public static IReadOnlyList<string> Codes { get; } =
            All.Select(l => l.Code).ToList().AsReadOnly();

        /// <summary>All full culture codes in declaration order.</summary>
        public static IReadOnlyList<string> CultureCodes { get; } =
            All.Select(l => l.CultureCode).ToList().AsReadOnly();

        /// <summary>The default language (currently English). Used by
        ///     <see cref="Loc"/> as the fallback source and by ASP.NET
        ///     RequestLocalization as <c>DefaultRequestCulture</c>.</summary>
        public static LangDef Default => All[0];

        /// <summary>Look up a language by its 2-letter code (case-insensitive).
        ///     Returns <c>null</c> if the code is not supported — callers
        ///     should fall back to <see cref="Default"/> explicitly, so a
        ///     silently-mis-typed code shows up in logs instead of leaking
        ///     English into a partially-translated experience.</summary>
        public static LangDef? GetByCode(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            var c = code.Trim().ToLowerInvariant();
            for (int i = 0; i < All.Count; i++)
                if (All[i].Code == c) return All[i];
            return null;
        }

        /// <summary>Shortcut used by <see cref="GeminiMedicalInterpretationService"/>
        ///     and the OpenAI provider — returns "English" for any unknown
        ///     code so the prompt is never left with an empty placeholder.</summary>
        public static string GetLangName(string? code) =>
            GetByCode(code)?.LangName ?? Default.LangName;

        /// <summary>Shortcut used by <see cref="CamBatchService"/> —
        ///     returns "en-US" as the default culture for any unknown code.</summary>
        public static string GetCultureCode(string? code) =>
            GetByCode(code)?.CultureCode ?? Default.CultureCode;
    }
}
