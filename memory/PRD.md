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
- ✅ **[Feb 2026 — LOINC Faza C]** **Anchored LOINC mappings in Gemini system prompt**
  (`GeminiMedicalInterpretationService.cs`): hardcoded official codes for 8 frequently
  hallucinated Romanian-lab analytes — LDH (14804-9), eGFR / DFG (62238-1, CKD-EPI legacy
  which is the code present in the local seeded LOINC subset; 98979-8 noted as alternative
  for CKD-EPI 2021 race-free), Densitate urinară (2965-2), Non-HDL cholesterol (43396-1),
  Procent protrombină / Quick% (5894-1, NOT INR 6301-6), Celule epiteliale plate (5787-7
  general; initial wrong anchor 5787-2 corrected after first test showed the LOINC
  validator overriding it via check-digit recovery), Anti-tiroglobulină (8098-6),
  Calcitonină (1992-7; initial wrong anchor 8000-2 corrected after web-verification
  against loinc.org). New Strict Rule #9 forbids LOINC fabrication globally and instructs
  `null` over guessing. Companion STRICT block disallows digit-swap, check-digit
  ""correction"" or similar-looking substitutions for the eight anchored codes.

## Pending / Backlog

### P1 – Family profiles (multi-session focus)
- 🔜 **P1.6**: Denormalize parameters into `AnalysisResults` table on each interpretation (ParameterCode, Value, Unit, Status, SamplingDate, per profile)
- 🔜 **P1.7**: Canonical dictionary mapping raw parameter names (e.g. "VS 1ère heure", "Vitesse de sédimentation") → canonical code (e.g. "ESR") for cross-lab tracking
- 🔜 **P1.8**: Parameter evolution view (Chart.js line chart per parameter, per profile)
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
