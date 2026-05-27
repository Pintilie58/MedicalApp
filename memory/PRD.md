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
- 🔜 **Faza 3**: Batch Processing + Background Job + Sumar.txt.
- 🔜 **Faza 4**: Dashboard CAM cu statistici + export Sumar PDF.

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
