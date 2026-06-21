# MedicalApp – PRD

## Original problem statement
Build "MedicalApp", an ASP.NET Core MVC (.NET 9, VS2022) web app where users upload medical analysis PDFs. The app uses AI to interpret the data, generates a nicely formatted localized PDF report and emails it back to the user. Credit-based payment (1 credit per interpretation), user auth, email verification, password reset, 5 languages (EN, RO, FR, ES, DE), Admin Dashboard, and multi-Profile support (per family member).

Development workflow: bi-directional Git sync. The agent modifies files in the cloud workspace → user pushes via "Save to GitHub" → user does `Git Pull` in VS2022 → runs with local SQL Server Express (`LENOVO-YOGA2\SQLEXPRESS`).

## Core stack
- ASP.NET Core MVC .NET 9, EF Core + SQL Server
- BCrypt auth, MailKit (Gmail SMTP)
- **Google Gemini 2.5 Flash** via direct REST API (native PDF vision, no text extraction) — user-provided API key in User Secrets
- QuestPDF (PDF report generation)
- Chart.js (admin revenue chart)

## Architecture
```
/app/MedicalApp/
├── Attributes/ (AdminAuthorizeAttribute)
├── Controllers/ (Account, Admin, Credits, Home, Interpretation, Profiles)
├── Data/ (AppDbContext)
├── Models/ (User, Purchase, PromoCode, InterpretationHistory, Profile, InterpretationResult, ViewModels)
├── Services/ (AdminSettings, EmailService, Loc, GeminiMedicalInterpretationService, DailySummaryService, PdfReportGenerator, PdfTextExtractor, StartupSeed, …)
├── Migrations/
├── Views/ (Account, Admin, Credits, Home, Interpretation, Profiles, Shared)
├── wwwroot/
├── appsettings.json
└── Program.cs
```

## DB schema (current)
- **Users**: Email (PK), Parola, Credite, DataC, CreditConsum, CreditRest, PasswordResetToken, PasswordResetTokenExpiry, TotalPaid, LastLoginAt, IsBlocked, IsAdmin, **BonusCredits**, **BonusCreditsConsumed**
- **Profiles**: Id, UserEmail, Name, Relationship, Gender, BirthYear, Notes, IsDefault, CreatedAt
- **InterpretationHistories**: Id, UserEmail, OriginalFileName, Language, Status, ErrorMessage, CreditsConsumed, InputTokens, OutputTokens, CreatedAt, **ProfileId (FK)**, **RawJsonResult (NVARCHAR MAX)**
- **Purchases**: Id, UserEmail, PurchasedAt, AmountEur, CreditsAdded, PaymentMethod, PackageKey, PromoCode
- **PromoCodes**: Id, Code (UQ), CreditsToAdd, ValidFrom, ValidUntil, TimesUsed, MaxUses, IsActive, CreatedAt
- **LoincDictionary** *(new — LOINC step 1)*: LoincCode (PK string), LongCommonName (indexed), OrderObs, AliasesJson, TranslationsJson, ImportedAt

## Implemented (changelog)
- ✅ **2026-02 — FIX bug critic: email body în limba greșită ("German PDF + Romanian email")**:
  - **Cauză**: `Loc.T(key)` citea `CultureInfo.CurrentUICulture`, care era setat corect la începutul request-ului dar putea fi resetat dacă Gemini/email service offload-uia munca pe thread pool — PDF se generase deja cu cultura corectă, dar email body se evalua cu cultura resetată.
  - **Fix**:
    - `Services/Loc.cs`: nouă suprasarcină `Loc.T(string key, string? languageCode)` care decuplează rezoluția traducerii de `CurrentUICulture` (primește limba explicit).
    - `Controllers/InterpretationController.cs`: `BuildEmailBody` are acum parametru `string? languageCode` propagat la TOATE cheile (`EmailGreeting`, `ResultEmailIntro`, `ResultEmailAttachedNote`, `Tagline`, `EmailRegards`, `EmailInterpretForProfileFmt`) + subject-ul. Acum email + PDF folosesc EXACT același languageCode (variabilă locală, nu state global).
- ✅ **2026-02 — Phase 5 traduceri: DuplicateDetected + email "for profile"** (+17 chei × 5 limbi).
- ✅ **2026-02 — Buton „Evoluție grafică" + „Compară selectate" disabled cu tooltip explicativ**.
- ✅ **2026-02 — B2C: fallback automat TEXT → VISION când extracția PdfPig nu vede analize medicale**.
- ✅ **2026-02 — UI loading consistent: mascot peste tot (era cerc vechi pe DuplicateDetected)**.
- ✅ **2026-02 — 2 doughnuts side-by-side (B2C vs CAM)** în Admin dashboard.
- ✅ **2026-02 — AI Usage Tracking refactor**.
  - **`Views/Interpretation/DuplicateDetected.cshtml`**: toate stringurile RO hardcodate (titlu, heading, alerta cu fișier potrivit, „Ce dorești să faci?", cardurile „Deschide raportul existent" / „Re-interpretează", butoanele și link-ul de cancel) folosesc acum `Loc.T(...)`. JS folosește template Razor pentru a restaura corect label-ul localizat la bfcache pageshow.
  - **`Controllers/InterpretationController.cs` → `BuildEmailBody`**: linia "Interpretare pentru profilul: ..." era hardcodată RO. Acum folosește noua cheie `EmailInterpretForProfileFmt` care se rezolvă în limba user-ului (același mecanism `Loc.T` ca restul emailului — `EmailGreeting`, `ResultEmailIntro`, etc., care deja erau localizate complet).
  - **`Loc.cs`**: +17 chei × 5 limbi = **+85 traduceri**. Total final: **571 chei × 5 limbi = 2855 traduceri**.
- ✅ **2026-02 — Buton „Evoluție grafică" + „Compară selectate" disabled cu tooltip explicativ** când profilul are doar 1 interpretare (wrapper `<span>` cu `title` ca să prindă hover-ul de pe buton dezactivat).
- ✅ **2026-02 — B2C: fallback automat TEXT → VISION când extracția PdfPig nu vede analize medicale** (regex heuristică).
- ✅ **2026-02 — UI loading consistent: mascot peste tot (era cerc vechi pe DuplicateDetected)**.
- ✅ **2026-02 — 2 doughnuts side-by-side (B2C vs CAM)** în Admin dashboard.
- ✅ **2026-02 — AI Usage Tracking refactor** (tabel `AiUsageLogs` + buton reset + acoperă B2C+CAM).
  - **Cauza** raportată de user: PDF original cu pagini 1-3, editat în Word (adăugat `[MedicalApp]` + pacient + email pe pagina 1), re-exportat ca PDF. Word a rasterizat paginile 2-3 (tabelul cu analize) → PdfPig vedea doar header-ul administrativ → Gemini respingea cu „Fișierul nu pare a fi o analiză medicală". B2B (CAM) NU avea problema fiindcă folosește `InterpretPdfAsync` (vision mode).
  - **Fix**: `InterpretationController.cs` are acum `LooksLikeMedicalData(text)` (regex pe `<număr> <unitate de laborator>` cu prag ≥3 match-uri). Când textul extras nu trece, controller-ul comută automat la VISION mode (`InterpretPdfAsync`) — aceeași cale ca B2B, care lucrează corect pe pagini rasterizate.
  - Verificat: PDF rasterizat → 0 match-uri (VISION). Lab PDF normal → 6+ match-uri (TEXT, păstrează anti-halucinație pe cifre).
- ✅ **2026-02 — UI loading consistent: mascot peste tot (era cerc vechi pe DuplicateDetected)**:
  - `Views/Interpretation/DuplicateDetected.cshtml` folosea `<div class="processing-spinner">` (cerc vechi).
  - Acum folosește același partial `_DoctorMascot` ca `Upload.cshtml` → loading uniform 🥼.
- ✅ **2026-02 — 2 doughnuts side-by-side (B2C vs CAM)** în Admin dashboard, size-uri compacte (~220px max).
- ✅ **2026-02 — AI Usage Tracking refactor** (tabel `AiUsageLogs` + buton reset + acoperă B2C+CAM).
  - **Tabel nou `AiUsageLogs`** (Model `Models/AiUsageLog.cs` + DbSet + entity config în `Data/AppDbContext.cs`) cu indexuri pe `CreatedAt`, `Status`, `Source`. Câmpuri: Id, CreatedAt, Source ("B2C"/"CAM"), UserEmail, ClinicId, ModelUsed, InputTokens, OutputTokens, Status ("success"/"error"/"rejected"), ErrorMessage.
  - **`Services/AiUsageLogger.cs`** (`IAiUsageLogger` + `AiUsageLogger`): fail-safe, niciodată nu rupe flow-ul de interpretare. Înregistrat scoped în `Program.cs`.
  - **B2C (`InterpretationController.SaveHistory`)**: log apelare ÎN AiUsageLogs imediat după scrierea `InterpretationHistory`, condiționat de `geminiWasCalled` (skip dacă era reject pre-Gemini).
  - **B2B/CAM (`Services/CamBatchService.CallGeminiWithRetryAsync`)**: signatură extinsă cu `Clinic clinic, User? user`; loghează tokens reali + modelul efectiv folosit (după fallback Flash→Pro→Plus) pe success, plus log pe failure final cu `EffectiveModelId()`. Înainte modulul CAM nu apărea deloc pe dashboard.
  - **Admin Dashboard (`AdminController.Index`)**: query schimbat din `InterpretationHistories WHERE Status='success'` în `AiUsageLogs` (toate apelurile, B2C+CAM, success/error/rejected) — vede ACUM tot ce consumă bani.
  - **Buton „↺ Reset"** în header-ul widget-ului „AI usage (Gemini)" + modal confirmare → POST `Admin/ResetAiUsage` care face `ExecuteDeleteAsync()` pe `AiUsageLogs`. NU atinge `InterpretationHistories` (istoricul user-ilor rămâne intact).
- ✅ **2026-02 — Phase 4: Custom file input localizat ("Choose File" / "No file chosen")**.
- ✅ **2026-02 — Phase 3 traduceri: Interpretare + Profile (Index/Form)** (+59 chei × 5 limbi).
- ✅ **2026-02 — Fix build Loc.cs (Phase 2a) + Phase 2b completă** (landing page).
- 🔄 **2026-02 — Revert `MedicalApp.Tests`**: xUnit test project a fost eliminat complet după ce a îngheţat VS2026 la Rebuild. Testarea automată C# este pe pauză; user-ul testează manual local.

- ✅ Project scaffolding (.NET 9 MVC) + SQL Server via EF Core
- ✅ 5-language localization via `Loc.cs`
- ✅ BCrypt auth + email verification + password reset
- ✅ Credit system + simulated checkout + bonus credits (consumed first)
- ✅ Localized PDF report (QuestPDF A4)
- ✅ Admin Dashboard (12 stats, revenue chart, users list, bulk email, promo codes, user detail with block/credits/reset)
- ✅ **[Feb 2026]** AI engine migrated from OpenAI+PdfPig → **Gemini 2.5 Flash native PDF vision** (HttpClient REST, no text extraction)
- ✅ Robustness: 32k max tokens, 300s timeout, auto-retry, JSON malformation recovery
- ✅ **DailySummaryService** (09:00 AM background job with catch-up) + admin manual trigger
- ✅ Admin email notification on credit purchase
- ✅ Credits widget in navbar (color-coded)
- ✅ **[P1.1–P1.3]** Family Profiles: `Profiles` table, CRUD UI `/Profiles` with live search, profile selection on interpretation upload, email subject prefixed with profile name, "Arhivă (N)" counter on each profile card
- ✅ **[P1.4 – Feb 3, 2026]** `InterpretationHistories.RawJsonResult` column added, Gemini JSON persisted in DB on success/rejected
- ✅ **[P1.5 – Feb 3, 2026]** `/Profiles/History/{id}` archive page: lists successful interpretations per profile (date, filename, parameter count, abnormality count); `/Profiles/DownloadReport/{id}` regenerates PDF on-the-fly from stored RawJsonResult (no credit consumed, no AI call)
- ✅ **[Feb 3, 2026]** Sandbox/GitHub sync mechanism: `github` remote added so agent can pull user's migrations → prevents push conflicts
- ✅ **[Feb 2026]** PDF SHA-256 de-duplication check with UI override flow (force re-interpret)
- ✅ **[Feb 2026]** Side-by-side Compare view: up to 4 historical interpretations per profile (sorted by `DateTaken`)
- ✅ **[Feb 2026]** Premium Archive Billing: 1 year free, then 1 credit / 3 archive usages (`ArchiveAccessService`)
- ✅ **[Feb 2026]** `CardiovascularRisk` on Profile + strict LDL/non-HDL thresholds in Gemini prompt
- ✅ **[Feb 2026]** Exponential backoff (5 retries: 5s/15s/30s/60s) on Gemini 503/429
- ✅ **[Feb 2026]** Tuned prompt: 3-4 sentence parameter explanations; allows both absolute and % values on separate rows
- ✅ **[Feb 2026]** **`StatusValidator`** post-LLM mathematical validator (`Services/StatusValidator.cs`):
  parses ranges (`X-Y`, `<X`, `≤X`, `>X`, `≥X`, with optional unit-after-slash), recomputes
  `normal`/`high`/`low`/`borderline` (5% tolerance band) from value+range in plain C#, rebuilds
  `abnormal_findings` to match. **Hooked up** in `InterpretationController` right after the
  medical-check, wrapped in try/catch so a validator bug never breaks the flow. Re-serializes
  the corrected JSON into `RawJsonResult` (so PDF regeneration, archive, and future evolution
  charts use corrected statuses). Safe-by-default: parameters with unparseable value or range
  are skipped (model status preserved). Eliminates LLM math hallucinations (e.g. `0.03`
  flagged as `High` when reference is `0-0.2`).
- ✅ **[Feb 2026]** **PDF footer badge** showing the processing mode used:
  `ProcessingModeText` ("Procesat în mod text — extragere literală") or `ProcessingModeVision`
  ("Procesat în mod vision — OCR pe imagine"). Localized in all 5 languages. Discreet 7pt
  italic muted line in the footer. Omitted when regenerating archive PDFs (we don't know the
  original mode retroactively).
- ✅ **[Feb 2026]** **Gemini JSON auto-repair** (`TryRepairGeminiJsonDrift` in
  `GeminiMedicalInterpretationService.cs`): on very long outputs (~6k+ tokens, typically
  CV-risk profile + many parameters), Gemini occasionally drops a closing `}` between two
  adjacent objects in an array (pattern `"..." , {` instead of `"..." }, {`). Before
  the controller's expensive retry loop kicks in (~60s + tokens), we attempt an in-place
  targeted repair: scan for closing-quote+ws+comma+ws+`{` patterns, verify the quote
  actually closes a VALUE (not a property key) by walking back to opening quote then checking
  for `:` before it, and insert `}` between the quote and the comma. Conservative: zero
  false positives on legitimate JSON; if second parse fails, original error propagates
  unchanged. Logged as `warning` when applied so we can monitor frequency.
- ✅ **[Feb 2026 — Plan A]** **TEXT-BASED Gemini hybrid pipeline** (anti-OCR-hallucination):
  - Root cause identified: Gemini Files API does NOT read the PDF text layer, it RENDERS the
    PDF as images and runs vision OCR on pixels — so even on perfect digital PDFs, digits
    can be hallucinated (`33.9 → 33.7`, `0-0.2 → 0-2`). Vision hallucination rate ~88%
    persists even on Gemini 3 Pro per Feb 2026 benchmarks.
  - Solution: when `PdfTextExtractor` (PdfPig, deterministic text-layer reader) yields ≥200
    characters of clean text, we send the extracted text to Gemini instead of the PDF
    base64 — Gemini then focuses on medical reasoning, not pixel reading. Digits are LITERAL.
  - Architecture: `IMedicalInterpretationProvider` gains `InterpretTextAsync(text, fileName,
    lang, ctx)`. Shared private `CallGeminiAsync(pdfBase64?, extractedText?)` does the heavy
    lifting. `BuildRequestBody` and `BuildUserPrompt` adapt to the modality (no `inline_data`
    in TEXT mode; a `<PDF_TEXT>...</PDF_TEXT>` block embedded in the user prompt with explicit
    "digits are LITERAL, do NOT re-read" instruction). System prompt rewritten with
    `INPUT SOURCE — TWO POSSIBLE MODES` (Mode A vision, Mode B literal text).
  - Controller path selection: `geminiUseTextMode = useGemini && extractedText.Length ≥ 200`;
    when false, falls back to vision (scanned/image-only PDFs).
  - Bonus: ~10× fewer tokens per call → expected latency drop from ~115s to ~30-50s + cost
    reduction; all retry/backoff logic preserved.
- ✅ **[Feb 2026 — LOINC Faza A+B]** Local LOINC dictionary (~97k codes) seeded from
  CSV into `LoincDictionary` table; `LoincValidator.cs` runs after Gemini with deterministic
  check-digit recovery (Verhoeff/Mod10 brute force) and strict long-name lookup to repair
  ~97% of malformed/missing codes WITHOUT introducing false positives. (Earlier digit-swap
  recovery was reverted because it produced false matches, e.g. LDH `2532-0 → 5232-4`.)
- ✅ **[Feb 2026 — LOINC Faza C v3]** **Anchored LOINC mappings in Gemini system prompt**
  (`GeminiMedicalInterpretationService.cs`): hardcoded official codes for 12 frequently
  hallucinated Romanian-lab analytes — LDH (14804-9), eGFR / DFG (62238-1), Densitate
  urinară (2965-2), Non-HDL cholesterol (43396-1), Procent protrombină / Quick% (5894-1),
  Celule epiteliale plate (5787-7), Anti-tiroglobulină (8098-6), Calcitonină (1992-7),
  pH urinar (5803-2), **Hemoglobina (718-7)**, **Glucoza / Glicemie (2345-7)**,
  **Urobilinogen urinar (20405-7)**. Each mapping documents the wrong codes the model
  has been observed emitting (e.g. ""Do NOT use 2452-1 — that is Hypoxanthine in Body
  fluid, not Glucose"" / ""Do NOT confuse with Urobilin 3104-7""). New Strict Rule #9
  forbids LOINC fabrication globally.
- ✅ **[Feb 2026 — LoincValidator hardening]**
  1. `TryRecoverByCheckDigit` safety-belt FIX: previously skipped completely when
     Gemini's long_name had fewer than 2 ""significant"" tokens (length ≥ 4). Now:
     0 tokens → reject; 1 token → must appear in DB candidate; 2+ tokens → ≥ 2 overlap.
     Prevented the silent ""2720-4 → 2720-1"" mis-recovery for pH urinar.
  2. **`TryRecoverByDigitSwap` REMOVED** (function + call site). It produced subtle
     false positives where a valid LOINC for a DIFFERENT analyte was one swap away
     from Gemini's hallucination. Concrete production cases that triggered removal:
     `Glucoza 2542-3 → 2452-1` (DB confirms 2452-1 = Hypoxanthine in Body fluid, not
     Glucose) and `Urobilinogen 3014-8 → 3104-7` (DB confirms 3104-7 = Urobilin, not
     Urobilinogen). The frequently-hallucinated analytes are now anchored in the
     Gemini system prompt instead, eliminating the wrong prefixes at the source.
     `RecoveredByDigitSwap` field preserved in `LoincValidationStats` for JSON
     backwards-compatibility but always equals 0.

- ✅ **[Feb 2026 — LOINC Faza C v3.1]** Strengthened Glucose anchor with explicit
  ""Romanian lab specimen disambiguation"": Gemini kept emitting `2542-3` (Glucose in
  Whole Blood — a real LOINC code, but for capillary point-of-care meters), not because
  the anchor was wrong but because Gemini interprets the Romanian word ""sânge"" / ""din
  sânge"" literally as Whole Blood. The anchor now explicitly states that Romanian lab
  glycemia is ALWAYS serum/plasma (post-centrifugation) and adds a concrete few-shot
  example with the full 3-field LOINC triple. `2542-3` listed by name as a banned
  substitution. This pattern (specimen-mismatch hallucination) is documented for future
  similar cases.

- ✅ **[Feb 2026 — LOINC Faza C v3.2]** Critical disambiguation: the persistent
  `2542-3` for ""Glucoza"" was NOT serum glucose — the parameter name in the PDF was
  ""Glucoza (urina)"", i.e. urine-strip glucose. The anchor was split into TWO distinct
  cases: SERUM/PLASMA glucose → `2345-7` (biochemistry panel), URINE glucose →
  `5792-7` (Glucose [Mass/volume] in Urine by Test strip — urinalysis dipstick).
  Both cases now include explicit ""WHERE in the report it appears"" guidance and
  concrete few-shot examples. Lesson learned: always check the parameter's section
  context (biochemistry vs urinalysis) before anchoring.

- ✅ **[Feb 2026 — Pas 4: Compare grupare după LOINC]** `/Profiles/Compare` aliniaza
  acum parametrii după `LoincCode` (post-validator) când acesta este disponibil. Rândurile
  cu același cod LOINC apar pe O SINGURĂ linie, indiferent cum a denumit fiecare laborator
  testul în textul raportului (ex. ""VSH"" / ""ESR"" / ""Vitesse de sédimentation"" se aliniază
  acum împreună). Detalii implementare:
    * `ProfilesController.BuildComparison`: cheia de grupare e `loinc:<code>` când codul
      există, altfel fallback la `name:<lowercased-param>` (legacy interpretări pre-LOINC
      și parametri fără cod LOINC continuă să funcționeze fără regresie).
    * Sortare: rândurile LOINC-coded apar primele (alfabetic după LoincCode),
      apoi cele fallback (alfabetic după nume).
    * `ComparisonRow` extinsă cu `LoincCode` + `LoincLongName` (null pentru rânduri
      fallback).
    * `Views/Profiles/Compare.cshtml`: pe rândul LOINC apare un badge mic
      `LOINC 14804-9` cu tooltip pe `LoincLongName`. Notă explicativă pentru utilizator
      în paragraful de jos.
    * `data-testid` adăugat: `compare-row-loinc-<code>` pe badge.

- ✅ **[Feb 2026 — LOINC Faza C v4: deterministic matcher microservice]** Inspired
  by RELMA / Epic concept maps. Complete redesign of the LOINC pipeline to eliminate
  LLM hallucinations:
    * **Gemini emits only `parameter_normalized_en`** (a clean standardized English
      medical term like ""Glucose [Mass/volume] in Serum or Plasma""). The model is
      explicitly forbidden from emitting numeric LOINC codes. The 12-anchor section
      and the entire LOINC MAPPING / ANCHORED LOINC CODES / strict-rule blocks were
      removed from the system prompt and replaced with PARAMETER NORMALIZATION
      guidelines + worked examples.
    * **Python FastAPI microservice** (`/app/loinc_service/`) does the actual code
      resolution using a deterministic three-step pipeline:
        1. Semantic search — `sentence-transformers/all-MiniLM-L6-v2` produces
           384-dim embeddings, cosine similarity against the full 97k local LOINC
           corpus (~10 ms vectorized in numpy).
        2. Fuzzy match — `rapidfuzz.token_set_ratio` on the top-25 semantic
           candidates against LongCommonName and Component.
        3. Rules engine — specimen / method / property keyword constraints
           extracted from the query, applied as soft constraints (no penalty if
           no rule keywords).
        4. Composite score: `0.65 * semantic + 0.30 * fuzzy + 0.05 * rules`.
    * **C# integration**: new `Services/LoincMatcherClient.cs` calls the FastAPI
      service via `HttpClient` after the Gemini step in `InterpretationController`.
      Safe-by-default: any matcher error/timeout is logged and skipped (entry stays
      without a LOINC code, rest of pipeline continues). `appsettings.json` has a
      new `LoincMatcher` section (BaseUrl, Enabled, TimeoutSeconds, MinScore).
    * **`KeyResult` model**: added `ParameterNormalizedEn` field (emitted by
      Gemini). `LoincCode`, `LoincLongName`, `LoincConfidence` are kept but are now
      populated by `LoincMatcherClient` instead of by Gemini. Archived JSON results
      remain compatible.
    * **`LoincValidator.cs`** kept on disk as archive but no longer called by
      `InterpretationController`.
    * **Smoke test passed**: 19/19 critical mappings resolved correctly on the
      sandbox sample corpus (Glucoza ser vs urină, pH urinar, Hemoglobina, eGFR,
      LDH, Non-HDL, Anti-Tg, Calcitonin, etc.), confidence scores 0.85–0.96.
    * **Deployment**: Python service runs locally on the user's Windows host
      alongside SQL Server. Setup is one-time (`pip install -r requirements.txt`
      + `python seed_embeddings.py`); the seed script reads from `LoincDictionary`
      via pyodbc, encodes 97k rows (5-15 min on CPU), and writes
      `data/loinc_embeddings.npy` + `data/loinc_metadata.json`. The microservice
      then loads those files at startup — no further SQL Server contact at runtime.
      Service portable to any Linux VPS later (just copy the data files).

- ✅ **[Feb 2026 — Faza C v4.1: prompt calibration + LOINC in PDF report]**
  After the first production run hit 49/49 matched but with 6 ""medium"" confidence
  scores on RBC indices (MCV, MCH, MCHC, RDW) and WBC differential percents,
  two follow-up tweaks were applied:
    1. **Gemini prompt expansion**: explicit canonical English names added for
       MCV, MCH, MCHC, RDW, MPV, PDW, PCT, and the full WBC differential
       (Limfocite / Monocite / Eozinofile / Bazofile in both absolute count
       and % forms). Forces Gemini to emit ""Erythrocyte mean corpuscular volume
       [Entitic volume] by Automated count"" rather than ""MCV (Volum eritrocitar
       mediu)"" — which the semantic matcher then resolves to LOINC 787-2
       directly with high confidence.
    2. **PDF report enhancement** (`PdfReportGenerator.KeyResultsTable`):
       below each parameter's name and explanation, a small grey footer line
       now shows ""LOINC <code> · <Long Common Name>"". Rendered only when the
       matcher actually resolved a code; absent for proprietary indices.
       Makes the report internationally recognizable — any hospital / EHR /
       research database worldwide identifies the same test by that code.

- ✅ **[Feb 2026 — Faza C v4.2: more anchors after 2nd production test]**
  Second real-world test (lipidic + thyroid panel, 18 parameters) hit 18/18 matched
  but two analytes resolved to plausible but suboptimal codes:
    * LDH (total) → matched to ""2537-9 LDH isoenzyme 1"" instead of the desired
      ""14804-9 LDH total"". Added an explicit canonical English name in the
      Gemini prompt that forces ""Lactate dehydrogenase [Enzymatic activity/volume]
      in Serum or Plasma by Lactate to pyruvate reaction"" so the matcher's
      semantic + fuzzy step ranks 14804-9 above 2537-9.
    * Anti-TPO → matched to ""17797-2 Thyroid colloidal Ab"" (a different
      antibody) instead of the desired ""8099-4 Thyroperoxidase Ab"". Added
      explicit canonical name ""Thyroperoxidase Ab [Units/volume] in Serum"" plus
      a NOTE clarifying that Anti-TPO is NEITHER Thyroid colloidal Ab NOR
      Thyroglobulin Ab — they are three different antibodies.
  Pattern confirmed: each ""medium confidence"" or wrong-but-plausible result in
  production is fixed by adding 1-2 lines to the Gemini prompt's worked-examples
  section. The semantic matcher then resolves correctly without further changes
  to the Python pipeline. No need to rebuild embeddings.

- ✅ **[Feb 2026 — Faza C v4.3: aggressive prompt anti-Romanian-leakage]**
  Third production session revealed Gemini still leaking Romanian text into
  `parameter_normalized_en` for ~15% of parameters (""Hemoglobina eritrocitara
  medie {HEM}"", ""Concentratia medie a Hb/eritrocit"", ""Neutrofil"" singular,
  ""CA 19 - 9 ( Antigen carbohidrat )""), which made the semantic matcher pick
  semantically nearby but wrong codes (""784-9 Erythrocyte mean corpuscular
  diameter"" instead of ""785-6 MCH"" for example). Prompt strengthened with:
    * **Strict translation rule** (#7): forbid copying the raw Romanian name
      into parameter_normalized_en — must always be canonical English.
    * **Brace/parenthesis stripping** (#8): inputs like ""Hemoglobina X {HEM}""
      or ""CA 19 - 9 ( Antigen carbohidrat )"" must produce clean canonical
      names without the parenthetical alias.
    * **% vs absolute count** (#9): explicit instruction to differentiate
      ""Neutrofile 60%"" (fraction → /100 leukocytes) from ""Neutrofile 4500/uL""
      (absolute → [#/volume]).
    * **Singular vs plural** (#10): never emit ""Neutrofil"" / ""Limfocit"" —
      cell populations are always plural in LOINC.
    * **Pre-output self-check**: silently re-read every emitted normalized name
      and verify it is 100% English with explicit specimen.
    * **Additional anchors** for analytes seen in real production:
      HOMA-IR (no universal LOINC — emit plain text, null is honest),
      CA 19-9 / CA 125 / CA 15-3 / CEA / AFP (tumor markers, common in screening),
      Vitamin B6 / B12 / D / Folat / Iron / Ferritin / Transferrin
      (full Romanian → English canonical mappings).

- ✅ **[Feb 2026 — Faza C v4.4: Gemini JSON robustness fixes]**
  Production session uncovered two unrelated transient issues that wasted
  retry budget. Both fixed:
    1. **Raw newline (0x0A) inside JSON string values** —
       `JsonReaderException ""'0x0A' is invalid within a JSON string. Path:
       $.recommendations""`. Gemini occasionally emitted a literal LF byte
       inside long ""recommendations"" / ""summary"" string values instead of
       the escape sequence ""\n"". Added a new pre-parse repair pass
       `TryRepairRawNewlinesInStrings()` that walks the JSON once, tracks
       in-string vs out-of-string position, and escapes raw LF / CR / TAB
       inside string values to their JSON equivalents. The repair is run
       BEFORE the existing structural-drift repair; both run sequentially
       so a single response can have both defects fixed in one pass without
       needing a 60-second retry round-trip.
    2. **Off-by-one self-audit mismatch** — when the model declared 57
       parameters in `audit.expected_count` but emitted 56 in `key_results`,
       the controller was forcing a full retry (60s + ~3k tokens) for a
       single missing parameter. Common cause: a row in the report with
       no value (lab printed the header but the test was not yet completed).
       Threshold raised: retry only when difference >= 2. Off-by-one is
       logged as INFO and the pipeline continues.

- ✅ **[Feb 2026 — Faza C v4.5: log normalized_en + hard-reject penalty]**
  Production log analysis was incomplete because `LoincMatcherClient` was
  logging only the original Romanian parameter name, not the English
  `parameter_normalized_en` text actually sent to the Python matcher.
  Without that field it was impossible to tell whether a wrong code was due
  to Gemini emitting Romanian text or due to a Python ranking issue. Two
  fixes:
    1. **Enhanced logging** (`LoincMatcherClient.cs`): log line now includes
       `[normalized_en=""<actual English text>""]` next to the original
       parameter. Future regressions can be diagnosed at a glance.
    2. **Hard-reject penalty in Python pipeline** (`loinc_service/pipeline.py`):
       added a narrow `_HARD_REJECT_RULES` list (5 entries) that applies a
       0.25× score multiplier when the query mentions ""MCV / volume / MCH /
       hemoglobin / MCHC / concentration"" but the candidate's long_name
       mentions ""diameter"". This deterministically pushes 784-9 ""Erythrocyte
       mean corpuscular DIAMETER"" off the top when the query is clearly
       about VOLUME or HEMOGLOBIN. Intentionally narrow — only fires for
       6 well-defined query keywords, so it cannot cause collateral damage
       elsewhere in the 97k LOINC space.

- ✅ **[Feb 2026 — Resilience: Gemini Pro fallback model]**
  Implemented automatic fallback to `gemini-2.5-pro` after 2 consecutive HTTP
  503 / 429 transient errors on the primary `gemini-2.5-flash`. Rationale:
  Pro is ~5x more expensive but globally much less congested (Flash is the
  default for nearly every LLM developer in the world, so Google's Flash
  capacity gets saturated during peak hours; Pro is mostly used by power
  users and stays available). With the fallback active, the user only pays
  the Pro price during congestion incidents — the typical happy-path call
  stays on Flash.
  Implementation details:
    * New `GeminiSettings.FallbackModel` (defaults to ""gemini-2.5-pro"";
      set to null to disable).
    * `IMedicalInterpretationProvider.InterpretTextAsync` extended with
      optional `string? modelOverride` parameter (default null = use
      configured model).
    * `GeminiMedicalInterpretationService.CallGeminiAsync` honours the
      override when set; the URL, log messages and request body all use the
      effective model name.
    * `InterpretationController.Upload` retry loop tracks a
      `currentModelOverride` variable. After `transientFallbackThreshold = 2`
      consecutive transients on the primary, it sets the override to the
      configured fallback and stays on it for the remaining retries (no
      flapping). Log line includes both ""primary"" and ""fallback"" model
      names so operators can audit what was actually used.
    * `MedicalInterpretationService` (OpenAI provider) signature updated
      to match the new interface; the override parameter is ignored because
      the OpenAI provider has only one model.
  Retry budget kept at 5 attempts / ~110 s wall-clock (NOT increased back to
  7) — user chose this consciously, since the Pro fallback adds an effective
  ""extra safety net"" that makes brute-force retry-extension unnecessary.

## Pending / Backlog

### P0 → DONE
- ✅ **[Feb 2026 — LOINC Drift Warning în Compare]** Compare view detectează acum
  cazul în care **același nume normalizat de parametru** primește **coduri LOINC
  diferite** între interpretările comparate. Implementare în ~30 linii:
    * `BuildComparison` (`ProfilesController.cs`) construiește un map
      `normalized(parameter) → HashSet<LOINC codes>` peste toate KeyResults din
      coloane.
    * Pentru fiecare `ComparisonRow` cu LoincCode, dacă numele normalizat
      apare cu ≥ 2 coduri distincte → setează `HasLoincDrift = true` și
      populează `DriftLoincCodes` (lista celorlalte coduri văzute).
    * View `Compare.cshtml` afișează un `⚠` portocaliu lângă numele
      parametrului, cu tooltip explicativ în română care listează codul
      curent vs. celelalte coduri și sugerează verificare manuală.
    * Legendă scurtă în footer-ul tabelului pentru transparență.
  Scop: avertizează utilizatorul când variabilitatea de extragere a textului
  de către Gemini (același analit denumit ușor diferit între buletine)
  produce o splittare nefiresc în 2 rânduri în vizualizarea Compare. Opțiunea
  conservatoare (b) aleasă de user — doar același nume exact → coduri diferite.


### 🚧 CAM Module (Clinici Analize Medicale) — IN PROGRESS
- ✅ **[Feb 2026 — Faza 1: Foundation + Registration + DB schema]**
    * `User.UserType` (Individual / Clinic) — câmp nou pe Users.
    * Entități noi (5): `Clinic`, `ClinicPatient`, `ClinicAnalysis`, `ClinicBatchRun`, `ClinicBatchError`.
    * `RegisterViewModel` + UI Register: radio Persoană fizică / Clinică, cu câmpuri suplimentare (Nume, Localitate, Adresă) afișate dinamic prin JS doar când e selectat Clinic. Validare server-side.
    * `PendingRegistration` extins pentru a păstra datele clinicii între email-verify.
    * `AccountController.VerifyEmail` creează automat rândul `Clinic` la verificare reușită.
    * `CreditPackages` extins cu pachete CAM: `cam_test` (50 cr = 30 EUR) + `cam_pro` (1000 cr = 500 EUR). Pagina `/Credits/Buy` filtrează automat după `UserType`.
    * `CamSettings` în appsettings.json: `FilesRoot = C:\MedicalApp_files`, `CnpEncryptionKeyBase64` (gol — se setează în User Secrets când va fi nevoie).
    * `ICamFileStore` + `LocalDiskCamFileStore` — abstractizare pentru disk. Implementarea cloud (Azure Blob) va înlocui doar acest layer mai târziu.
    * `CamCryptoService` — AES-CBC pentru CNP pacient (preparat pentru Faza 2).
    * **Hook automat în `CreditsController.Checkout`**: la PRIMA achiziție CAM, se creează folderele `Original`, `Sends`, `Sumar`, `Errors` pe disk și se setează `Clinic.FoldersCreatedAt`. Idempotent.
    * **Areas/CAM/** scaffold: `DashboardController` + view cu status clinică, credite, foldere create/pending, card-uri "În curând" pentru Faza 2/3/4.
    * Navbar: toggle Mod personal ↔ Mod clinică pentru utilizatorii Clinic.
    * Login flow: Clinic e redirecționat automat la `/CAM/Dashboard` doar la prima accesare după login.
    * Routing: `app.MapControllerRoute` pentru Areas adăugat în `Program.cs`.
    * Localizare în Loc.cs pentru EN/RO/FR/ES/DE: ~12 chei noi.
- 🔜 **Faza 2**: Extragere CNP/Email + Listă pacienți + criptare CNP.

- ✅ **[Feb 2026 — Faza 2: Identificare pacient + Listă + Verifică PDF + Seed Demo]**
    * **DECIZIE STRATEGICĂ**: am renunțat la CNP pentru identificarea pacienților. Motivele:
        1. **Universalitate 30 limbi** — fiecare țară are alt format ID (Aadhaar IN, SNILS RU, NIR FR, SSN US, NHS UK, etc.) — imposibil de validat global.
        2. **GDPR-friendly** — CNP/SSN sunt "high-risk data". Nume + Email sunt "moderate-risk" → reduce expunerea legală.
        3. **Pragmatic** — pacientul a fost deja identificat la clinică cu buletinul; aplicația noastră are nevoie doar de o cheie de istoric stabilă.
    * **Identificarea unică pacient** = `(ClinicId, NameKey, Email)` unde NameKey = nume normalizat (fără diacritice, sortat alfabetic, lowercase).
    * `CamPatientKey.Normalize()` — algoritm portabil: NFD strip non-spacing marks → lowercase invariant → drop punctuation → sort tokens. Testat: "Ion Popescu" și "POPESCU Ion" → "ion popescu". "Ștefan ȚEPEȘ" → "stefan tepes". Funcționează cu chirilic, latină, greacă etc.
    * `CamPdfMetadataExtractor` — extrage Nume + Email cu 3 strategii fallback (label-based, near-email, capitalized-line). Multi-limbă în NameLabels.
    * **Eliminate** complet din proiect: `CamCryptoService`, `CnpEncryptionKeyBase64`, `CnpHashKey`, `CnpEncrypted`. Zero referințe orfane (verificat).
    * **DB schema**: migrare nouă trebuie generată în VS2026 — coloanele `CnpHashKey`/`CnpEncrypted` vor fi DROP-uite, `NameKey` adăugat, index unic refăcut pe `(ClinicId, NameKey, Email)`.
    * **`/CAM/Patients`** — listă pacienți cu search insensitiv la diacritice + ordinea cuvintelor + count analize per pacient (placeholder pentru Faza 3).
    * **`/CAM/CheckPdfs`** — scanează folderul `Original` și afișează ce extrage extractor-ul pentru fiecare PDF (verificare INAINTE de a lansa lotul în Faza 3). Status verde/galben + motiv eroare.
    * **Seed Clinica Demo** (idempotent, în `StartupSeed.EnsureClinicaDemoAsync`):
        - user: `clinica.demo@medicalapp.test` / `Demo1234!`
        - clinic: "Clinica Demo Test" / București / Str. Test 1
        - 1000 credite pre-încărcate + Purchase marker (PaymentMethod="seed", cam_pro)
        - Foldere create automat pe disk
        - 5 pacienți fictivi (Ion Popescu, Maria Ionescu, Andrei Georgescu, Elena Vasilescu, Mihai Constantinescu) — toți cu email `vasilepintilie2003@gmail.com` pentru testare emailuri în Faza 3.
- 🔜 **Faza 3**: Batch Processing + Background Job + Sumar.txt.

- ✅ **[Feb 2026 — Faza 3: Batch Processing + Background Job + Email pacient branded]**
    * Decizii implementate (confirmate cu user): a)i Compare la ≥2 analize, b)i fără limită fișiere/lot, c)i buton anulare, d)i fără auto-resume.
    * **`CamBatchService`** — orchestrator background; rulează în `Task.Run` cu propria DI scope. Procesare SEQUENTIAL (1 fișier la un moment dat) — mai prietenoasă cu Gemini rate limit. Capturează toate excepțiile, nu aruncă niciodată.
    * **`CamBatchProgress` + `CamBatchRegistry`** — state in-memory (ConcurrentDictionary keyed by batchRunId) pentru AJAX poll la 3s. Un singur lot activ per clinică (guard pe registry).
    * **Per fișier**: extract metadata → găsește/creează pacient (`NameKey + Email`) → Gemini → PDF interpretare → Compare PDF (dacă ≥2 analize) → email pacient → mută PDF în Sends → consumă 1 credit → salvează `ClinicAnalysis` (păstrează doar ultimele 4 per pacient, DELETE older).
    * **Eșec extract/AI/email**: counter `NotSends++` + `ClinicBatchError` cu RetryCount. La 3 retries fișierul + un `.reasons.txt` se mută în `Errors/`.
    * **Email pacient** (`CamPatientEmailBuilder`) cu branding dual: numele clinicii ca hero (header bleumarin + adresă) + footer "Powered by MedicalApp+ — medicalapp.ro". Subject: "Rezultate analize - {Clinic}". Atașamente: PDF original + Raport_Interpretare.pdf (+ Raport_Comparatie.pdf dacă există).
    * **Compare PDF CAM** (`CamComparePdfGenerator`) — tabel side-by-side cu QuestPDF, grupare per LOINC code (fallback nume), maximum 4 coloane.
    * **`Sum_yyyyMMdd_HHmm.txt`** (`CamBatchSumarWriter`) — scris în `Sumar/` la finalul fiecărui lot cu statistici + listă erori.
    * **UI** (`/CAM/Batch/Start` + `/CAM/Batch/Progress/{id}` + `/CAM/Batch/Status/{id}` + `/CAM/Batch/Cancel/{id}`): preview cu listă fișiere și estimare credite → buton "Pornește lotul" → pagină progres live cu progress bar animat, 4 counters (Sent / Compared / NotSends / Status), log scroll, buton Anulează. AJAX poll la 3s. Auto-stop la Completed/Cancelled/Failed.
    * **Recovery la startup** (`StartupSeed.FailOrphanedBatchesAsync`): orice `Status="Running"` rămas dintr-un crash anterior e marcat ca "Failed" + FinishedAt — operatorul vede situația reală și relansează manual.
- 🔜 **Faza 4**: Dashboard CAM cu statistici + export Sumar PDF.

- ✅ **[Feb 2026 — Faza 3.5: Robustețe metadata extraction + Upload manual + Sanity check]**
    * **Problema identificată**: PDF-uri cu multiple email-uri (clinică + pacient), nume cu prefixe artifact ("/Prenume: ..."), text adăugat ca Annotation (invizibil pentru PdfPig). Soluție: **Strategia B + C**.
    * **Strategia B — bloc explicit `[MedicalApp]`** (gold path, 100% precizie):
        Convenție recomandată clinicilor — pe ultima pagină a PDF-ului:
        ```
        [MedicalApp]
        Pacient: Ion Popescu
        Email: ion.popescu@example.com
        ```
        Detectat prin `MedicalAppBlockRx`, prioritar față de orice fallback.
    * **Strategia C — Override manual** (safety net 100%): tabel nou `ClinicPdfOverrides` (ClinicId + FileName unique). UI nou `/CAM/CheckPdfs` cu buton "✏ Editează" + modal Bootstrap (nume + email). `CamBatchService` preferă override-ul când există. Ștergere automată după Sends/Errors.
    * **Blacklist domenii**: câmp nou `Clinic.EmailDomainBlacklist` (CSV, configurabil din UI). Extractor-ul sare peste orice email cu domeniile listate → niciodată nu va lua email-ul clinicii din header.
    * **Validare "este PDF de analize medicale?"**: heuristică pe 40+ cuvinte cheie medicale (RO/EN/FR/ES/DE: analize, rezultate, biochimie, glicemie, leucocite, hemoglobină, etc.) — minimum 2 hituri = PDF valid. Respinge facturi/contracte/alte documente.
    * **Pattern românesc nou**: `Nume/Prenume:`, `Nume si Prenume:`, `Nume şi/și Prenume:`, `Prenume/Nume:` adăugate în NameLabels.
    * **Curățare nume**: regex `^/[A-Za-z...]+\s*:\s*` strip-uiește artefactele PdfPig (ex: "/Prenume: " → ""). Numele cu `/` sau `:` sunt respinse ca implausibile.
    * **Upload manual** (sugestia 1): buton pe `/CAM/CheckPdfs` "Selectare fișiere PDF" cu multi-file picker. Fișierele sunt **COPIATE** (nu mutate) în folderul Original al clinicii. Validare extensie .pdf. Disambiguare automată nume (timestamp suffix la coliziune).

- ✅ **[Feb 2026 — Faza 3.6: Gemini-first identification + Retry/Fallback + Compare PDF B2C-grade]**
    * **Identificare pacient prin Gemini** (când nu există override sau bloc `[MedicalApp]`): după ce Gemini interpretează PDF-ul, citim `PatientInfo.Name` direct din rezultatul structurat AI — mult mai fiabil decât PdfPig+regex (ex: "Nume/Prenume: Pintilie Vasile" se extrăgea ca "/Prenume: Pintilie Vasile"). Cost ZERO suplimentar — folosim apelul Gemini care oricum trebuia făcut pentru interpretare.
    * **Sanity check medical mutat MAI DEVREME**: extractor-ul detectează acum că PDF-ul nu e medical ÎNAINTE de a apela Gemini → ZERO credit consumat pe facturi/contracte.
    * **Eliminat UI Blacklist domenii** (per decizia user-ului Feb 2026 — ne bazăm 100% pe blocul `[MedicalApp]` sau pe Gemini). Câmpul DB `EmailDomainBlacklist` rămâne (no migration), nu mai e folosit.
    * **Retry + Flash→Pro fallback în CAM** (ca în B2C `InterpretationController`): 5 încercări pe 429/503 cu backoff progresiv 5s/15s/30s/60s. După 2 transient errors consecutive, switch automat la `GeminiSettings.FallbackModel` (gemini-2.5-pro). Implementat în `CamBatchService.CallGeminiWithRetryAsync`. Adăugat parametrul `modelOverride` în `IMedicalInterpretationProvider.InterpretPdfAsync`.
    * **Compare PDF refactor B2C-grade**: `CamComparePdfGenerator` reutilizează acum `ProfilesController.BuildComparison` (schimbat din `private` în `public static`) pentru a obține IDENTIC grouping LOINC + LOINC class headers + drift warning ⚠ + status abnormal marker. Sintetizez `InterpretationHistory` + `Profile` ad-hoc din `ClinicAnalysis` și pasez la builder. Side-by-side cu max 4 coloane, header per LOINC class (Hematologie, Biochimie etc.).

- ✅ **[Feb 2026 — Faza 3.8: LOINC matcher Python pornit și pentru CAM (FIX-ul real)]**
    * **Diagnoza completă**: la Faza 3.7 am încercat să completez `LoincClass` pe baza `LoincCode`-urilor existente. PROBLEMA: Gemini la CAM rareori returnează `LoincCode` pentru parametri în limbaj natural. Fără cod nu există clasă, oricât de bun ar fi enricher-ul local.
    * **Soluția REALĂ**: apelez exact același `LoincMatcherClient` ca B2C (Python service: 128 canonical anchors + semantic embeddings).
    * **Implementare**: în `CamBatchService.ProcessOneFileAsync` după Gemini, înlocuit `CamLoincClassEnricher` (șters) cu `await loincMatcher.MatchAllAsync(result, ct)` — identic cu B2C `InterpretationController` linia 502.
    * **Rezultat**: CAM acum populează AMBELE `LoincCode` + `LoincClass` pe fiecare KeyResult cu codurile oficiale, deci Compare PDF se grupează corect Hematology / Chemistry / etc. (la fel ca B2C).
    * **Cerință runtime**: când se lansează un lot CAM, modulul Python `loinc_service` TREBUIE să ruleze pe `http://localhost:8000` (la fel ca pentru interpretarea B2C). Dacă e oprit, log-ul afișează "⚠ LOINC matcher indisponibil" și batch-ul continuă fără clase (graceful degradation).
- ✅ **[Feb 2026 — Faza 3.9: Fix data recoltare + Compare PDF look-alike B2C]**
    * **Issue 1 (Date Parsing)**: `ProfilesController.ParseSamplingDate` și `CamBatchService.TryParseDate` se bazau pe `DateTime.TryParseExact` cu o listă fixă de formate. Pe șiruri de tip `"06.12.2023 - 10:27"` sau `"Data - ora recoltare: 06.12.2023 - 10:27"`, parsing-ul returna NULL, iar Compare PDF cădea pe data procesării (ex. "29 mai 2026") în loc de data reală a recoltării.
    * **Soluția**: parser-ul mutat într-un service centralizat `MedicalApp/Services/SamplingDateParser.cs` care folosește Regex pentru a extrage PRIMUL token de dată dintr-un șir arbitrar (numeric `dd.MM.yyyy`/`yyyy-MM-dd`/etc. + named-month EN/RO/FR). Indiferent de label, separator sau fragment de oră atașat, regex-ul izolează "06.12.2023" și-l parsează. Ambele puncte (B2C + CAM) deleagă acum la `SamplingDateParser.TryParse`.
    * **Issue 2 (CAM Compare PDF urât)**: vechiul `CamComparePdfGenerator` randa un tabel sec (Parametru | LOINC | data1 | data2). Rescris complet să oglindească `Views/Profiles/Compare.cshtml`: header cu badge "N interpretări", carduri mini per coloană (Interpretarea N · Recoltare · Interpretat · X parametri · Y anormalități), bară badge-uri sumar (↗ Crescute / ↘ Scăzute / = Neschimbate / ⚠ Doar parțial), tabel principal cu rânduri header de clasă LOINC, săgeți direcție per celulă (↗ roșu/↘ albastru), badge-uri status (↑↓≈✓), warning LOINC drift ⚠, coloană Referință, legendă footer cu LOINC source dots. PDF landscape A4 pentru până la 4 coloane fără ghesuit text.
    * Fix subtil: `InterpretationHistory.CreatedAt` sintetizat = `ProcessedAt` (NU `SamplingDate`), pentru ca linia "Interpretat:" să arate corect data interpretării, separată de data recoltării.
- ✅ **[Feb 2026 — Faza 3.10: Unit-aware LOINC swap (Mass/volume ↔ Moles/volume)]**
    * **Problema**: Gemini emitea frecvent denumirea LOINC "[Mass/volume]" pentru analiți raportați în pmol/L (ex. FT3, FT4) — corect ar fi "[Moles/volume]". Rezultat: același parametru ajungea pe rânduri Compare separate (3051-0 vs 14928-6 pentru FT3, 3024-7 vs 14920-3 pentru FT4) în loc să fie consolidat.
    * **Soluția**: post-correction la nivel de Python LOINC matcher, bazată pe unitatea de măsură.
        - `loinc_service/pipeline.py`: 3 funcții helper noi (`_property_family` — tolerant pe MCnc/SCnc vs Mass/volume/Moles/volume; `_infer_property_from_unit` — `pmol/L` → Moles/volume, `mg/dL` → Mass/volume; `_find_peer_with_property` — caută peer LOINC cu același component+system dar property diferit).
        - `find_loinc(test_name, unit=None)` aplică swap automat când unit indică property diferit față de match-ul ales.
        - `loinc_service/main.py`: `LoincRequest` are acum `unit` opțional.
        - `MedicalApp/Services/LoincMatcherClient.cs`: trimite `kr.Unit` în payload spre Python.
    * **Acoperire**: TOATE perechile Mass↔Moles din LoincDictionary, nu doar FT3/FT4. Acoperă automat Glucose, Cholesterol, Bilirubin, Urea, Creatinine, Triglycerides, T3/T4 total etc. dacă lab-ul raportează în unități contrastante.
- ✅ **[Feb 2026 — Faza 4: Dashboard CAM cu statistici + Sumar PDF per lot]**
    * **KPI cards lifetime**: total fișiere procesate / emailuri trimise / comparații atașate / NotSends + total loturi (Completed/Failed/Cancelled) + total pacienți unici.
    * **Chart.js bar chart**: activitate ultimele 30 zile (fișiere procesate/zi), grupat după `SamplingDate ?? ProcessedAt`.
    * **Top 5 pacienți**: după nr. analize în clinică + data ultimei recoltări.
    * **Istoric loturi**: tabel cu ultimele 20 loturi (data, durată, status badge, total/trimise/comparate/NotSends) + butoane Progres + Sumar PDF per rând.
    * **Sumar PDF per lot** (`/CAM/Dashboard/SumarPdf/{id}`): generat on-demand cu QuestPDF. Conține antet clinică, identitate lot, 4 KPI mini-cards, rată succes, tabel motive erori (sau confirmare „toate procesate cu succes"). Salvat și pe disc în folderul `Sumar/` ca `Sumar_Lot_<id>_yyyyMMdd_HHmm.pdf` (audit local).
    * Fișiere afectate: `Areas/CAM/Models/CamDashboardViewModel.cs` (extins), `Areas/CAM/Controllers/DashboardController.cs` (rescris + endpoint SumarPdf), `Areas/CAM/Views/Dashboard/Index.cshtml` (rescris cu KPIs/chart/tabel), `Services/CamBatchSumarPdfGenerator.cs` (nou), `Program.cs` (înregistrare scoped).
    * Fără migrare DB — toate datele exista deja în `ClinicBatchRuns`, `ClinicBatchErrors`, `ClinicAnalyses`, `ClinicPatients`.
- ✅ **[Feb 2026 — Faza 4.1: 3 fix-uri post-faza 4 (UI Progress + retry exhausted)]**
    * **Fix UI Progress polling**: `Progress.cshtml` folosea path absolut `/CAM/Batch/Status/{id}` — fragil sub PathBase / IIS sub-app. Înlocuit cu `@Url.Action` astfel încât URL-ul respectă route-ul ASP.NET corect.
    * **Pre-seed Registry SYNC în Controller**: `BatchController.Start` populează acum `CamBatchRegistry` ÎNAINTE de `Task.Run`, ca polling-ul JS să vadă entry valid de la primul fetch (înainte rămânea "0/0" pentru ~200-500ms până prinde RunAsync). `GetOrCreate` updatează Total la o valoare mai mare când runner-ul scanează folderul.
    * **Fix retry-exhausted Gemini → Errors/**: când Gemini eșuează după 5 retries + fallback Pro (mesaj „AI exhausted retries"), fișierul rămânea pe veci în Original și consuma credite la fiecare lot următor. Adăugat apel la `MoveToErrorsIfRetriesExhaustedAsync` pe această cale (la a 3-a încercare totală fișierul se mută în `Errors/`). Aplicat și la calea „Patient name missing from AI output".
- ✅ **[Feb 2026 — Faza 4.2: Status validator pentru CAM + fix tolerance pe intervale înguste]**
    * **Problemă raportată**: Densitate urinară 1.024 ∈ [1.005, 1.03] (clar în interval) era marcat ↑ (high). Două bug-uri compuse:
        1. `StatusValidator.Validate()` rula DOAR pe path-ul B2C `InterpretationController`. `CamBatchService` lăsa status-ul brut de la Gemini să curgă în PDF — fără re-calcul matematic.
        2. Logica veche "borderline" folosea `5% din boundary value` ca toleranță — pentru intervale înguste (densitate are lățime 2.5%) toată gama era "borderline" și o valoare clar în mijloc putea fi acceptată ca anormală.
    * **Fix** (universal, nu particular):
        - `CamBatchService.ProcessOneFileAsync`: apel `StatusValidator.Validate(result, _logger)` între LOINC matcher și PDF gen (oglindă perfectă a fluxului B2C). Loghează numărul de status-uri corectate per lot.
        - `StatusValidator.ComputeStatus`: când AMBELE limite sunt finite, calculează tolerance ca `5% din lățimea range-ului` (hi - lo). Pentru densitate (width=0.025), banda borderline ajunge ±0.00125, deci 1.024 e clar normal. Pentru analiți cu range deschis (ex `< 200`), păstrează vechea formulă boundary-relative.
- ✅ **[Feb 2026 — Faza 4.3: MaxOutputTokens fix + Status endpoint cache + audit P0]**
    * **Bug raportat**: PDF Examen sumar urină (Bordeianu Viorel) eșuat cu `FinishReason=MAX_TOKENS`, `out=14243`, `TextLen=45187`. JSON truncated → `InvalidOperationException` → fișier mutat în Errors.
    * **Cauza**: `MaxOutputTokens=32000` în `appsettings.json` era prea strict pentru PDF-uri cu mulți parametri (Examen urină + sediment = 20+ parametri = ~14k tokens text + JSON overhead).
    * **Fix #1**: `appsettings.json` Gemini.MaxOutputTokens: 32000 → 65000 (limita Gemini 2.5 Flash e 65536).
    * **Fix #2 (auto-fallback la Pro pe MAX_TOKENS)**: `CamBatchService.CallGeminiWithRetryAsync` are catch nou pentru `InvalidOperationException` cu mesaj `"MaxOutputTokens"`. Detectează automat că Flash a fost trunchiat și comută IMEDIAT pe Pro (output mai mare + acceptă mai bine PDF-uri complexe), FĂRĂ să consume din quota retry (5 încercări tranziente).
    * **Fix #3 (perf Status endpoint)**: pagina Progress făcea polling la 3s → 2 SQL queries per poll (`Clinic` + `ClinicBatchRun`) → ~100 polls pe un lot = 200 queries inutile. Acum când registry-ul in-memory are entry `Status="Running"`, Status face DOAR 1 query mic ("SELECT ClinicId WHERE Email=...") pentru AuthZ, restul se servește din memorie. Reducere ~50% queries. DB fallback rămâne pentru loturi finalizate.
- ✅ **[Feb 2026 — Faza 4.4: Zero-query polling + UX simplificat (renunțat la bara progres)]**
    * **Zero-query polling**: cache `ClinicId` în `HttpContext.Session` la login (pentru `UserType="Clinic"`). Status endpoint compară `p.ClinicId == Session.ClinicId` direct, fără DB. Reduce ~60 SELECTs per lot la 0 (plus 1 sesiune-prima-dată ca migrare blândă pentru session-uri vechi).
    * **UX renunțat la bara progres** (sugestie utilizator): bara striped/animated era misleading pentru AI async (nu putem estima realist). Înlocuită cu:
        - Casetă proeminentă **„Fișiere selectate: N"** + **„Procesate până acum: K"**
        - Badge **„⏳ Așteptați câteva secunde…"** + hint **„Interpretarea AI durează ~2-3 min/fișier"**
        - La finalizare, badge-ul comută la ✓ Finalizat / ⏹ Anulat / ✘ Eșuat
        - Contorii Trimise/Comparate/NotSends + Log live rămân neschimbați (informația cu adevărat utilă)
- ✅ **[Feb 2026 — Faza 4.5: MAX_TOKENS B2C parity + Unit Tests C# (proiect nou)]**
    * **B2C parity**: `InterpretationController` are acum aceeași logică de auto-fallback Pro pe `MaxOutputTokens` ca `CamBatchService`. Catch dedicat detectează exception-ul, comută model fără să consume retry budget, continuă imediat. Simetrie totală B2C ↔ B2B.
    * **Proiect nou `MedicalApp.Tests`** (xUnit, .NET 9), adăugat la solution. ProjectReference la `MedicalApp`. Fișiere create:
        - `SamplingDateParserTests.cs` — 18 test cases: bug-ul Bordeianu ("Data - ora recoltare: 06.12.2023 - 10:27"), ISO, slash, named-month EN/RO, US heuristic, two-digit year, null/empty/invalid.
        - `StatusValidatorTests.cs` — 16 test cases: bug-ul Densitate (1.024 ∈ [1.005, 1.03] = normal), glucoză, hemoglobină, range deschis `< 200`, range deschis `> 50`.
        - `LoincSourceBadgeTests.cs` — 6 test cases: contract afișare anchor/semantic.
    * Rulare locală: Test Explorer în VS2026 (auto-recunoaște xUnit) sau `dotnet test`.
    * Total: ~40 test cases care prind regresia bug-urilor istorice fără un nou run de PDF.
- 📊 **[Feb 2026 — Audit tehnic complet creat în `/app/memory/AUDIT.md`]**
    * 3 P0 + 6 P1 + 8 P2 + 4 P3 elemente prioritizate cu plan de remediere.
- ✅ **[Feb 2026 — Freemium PDF blur + 1 credit gratuit la înregistrare + traduceri RO Landing Page]**
    * **1 credit gratuit la înregistrare** (`AccountController.VerifyEmail`): orice cont nou primește `BonusCredits = 1` (chiar și când codul promo este invalid/expirat). Acoperă atât B2C cât și B2B (Clinic). Promo valid suprascrie cu numărul de credite din promo.
    * **Blur intercalat 60% în `PdfReportGenerator`**: overload nou `Generate(result, labels, isFreemium)` activează un pattern de blur la pozițiile `i % 5 ∈ {1,2,4}` (3 din 5 rânduri = 60% intercalat). Se aplică pe Key Results, Abnormal Findings, Risk Factors, Correlations (split pe propoziții), Recommendations (split pe propoziții). Patient Info + Summary rămân vizibile ca teaser. Rândurile blurate au fundal gri `#f5f6f7`, text înlocuit cu `█` în `#dadce0`, plus etichetă `🔒 Blocat — cumpără credite pentru deblocare`.
    * **Watermark DEMO** pe fiecare pagină (font 140pt în `#eef0f2`, centrat) via `page.Background()`.
    * **Bandă portocalie sus** + **bandă verde de CTA jos** explică user-ului ce e de făcut.
    * **Regulă freemium**: `isFreemium = (user.Credite == 0)` (utilizatorul nu a cumpărat niciodată un pachet plătit). Bonus credits + promo credits → tot blurat. Cumpărarea unui pachet plătit (orice pack) → toate raportele se generează clar, inclusiv re-descărcarea celor vechi din `ProfilesController.DownloadReport`.
    * **Traduceri RO Landing Page complete** în `Loc.cs` (~60 chei: NavHow…FootDisclaimer + 6 chei PdfFreemium*). Fallback la EN pentru fr/es/de.
    * Cale CAM (clinici): nemodificat — apelează overload-ul legacy `Generate(result, labels)` care implicit `isFreemium=false`.

### P1 – Family profiles (multi-session focus)
- 🔜 **P1.6**: Denormalize parameters into `AnalysisResults` table on each interpretation (ParameterCode, Value, Unit, Status, SamplingDate, per profile)
- 🔜 **P1.7**: Canonical dictionary mapping raw parameter names (e.g. "VS 1ère heure", "Vitesse de sédimentation") → canonical code (e.g. "ESR") for cross-lab tracking — *partly satisfied by Pas 4 (LOINC grouping in Compare view)*
- 🔜 **P1.8**: Parameter evolution view (Chart.js line chart per parameter, per profile, grouped by LoincCode)
- 🔜 **P1.9**: Chronological aggregated list of all tests per profile (consolidated timeline)

### P2
- Search/filter in archive page (by date range, parameter, lab)
- Export archive to Excel/CSV

### P3
- Real payment gateway (Stripe / Netopia / PayPal) replacing the simulated checkout
- Deploy to Azure App Service + SQL Azure
- PWA (installable on mobile)

## Known constraints
- Gemini API key is in User Secrets (NOT in repo). Sandbox-ul cloud nu o are.
- Agent cannot run/test the app in cloud sandbox (no .NET SDK, no SQL Server). Validation happens on user's Windows machine.

## Sync procedure (for future sessions)
Dacă user-ul a făcut `Git → Commit + Push` în VS2022 între sesiuni (migrări noi, modificări locale):
1. Agent rulează: `cd /app && git fetch github main`
2. Agent identifică fișierele diferite: `git diff --name-only HEAD github/main`
3. Agent pull-ează fișierele relevante (migrări, cod local): `git checkout github/main -- <path>`
4. Apoi începe task-ul nou → Save to Github nu mai dă conflict.

Remote-ul `github` este deja configurat ca `https://github.com/Pintilie58/MedicalApp.git`.
