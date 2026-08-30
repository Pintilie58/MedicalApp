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

- ✅ **2026-06 — Corecții după primul test real cu `split` (feedback user)**:
  - **Cauza câștigului mic (19s):** logul a arătat `404 models/gemini-3.1-pro is not found` — modelele 3.x nu sunt disponibile pe cheia userului, deci etapa C a căzut pe modelul de rezervă. Trecut pe **`gemini-2.5-flash` (A, B) și `gemini-2.5-pro` (C)**.
  - **Bug ascuns găsit și reparat:** Gemini 2.5 NU acceptă `thinkingLevel`. `BuildGenerationConfig` traduce acum nivelul în `thinkingBudget` pentru modelele non-3.x (minimal=0, low=1024, medium=4096, high=16384; la 2.5-pro minimal devine 128 pentru că acolo gândirea nu se poate opri).
  - **„Analize în afara intervalului” 8 din 12 → 12 din 12 GARANTAT:** `Services/AbnormalFindingsCompleter.cs`, rulat în controller DUPĂ `StatusValidator`. Orice analiză high/low/borderline omisă de model e adăugată determinist, cu explicația ei și severitate calculată din depășirea relativă a limitei (<20% mild, <50% moderate, restul severe; borderline mereu mild). Intrările scrise de model nu se modifică niciodată. Funcționează și pe fluxul monolitic.
  - **„83 din 84 analize” → etapa A2 de măturare:** un al doilea apel de citire care rulează în PARALEL cu B și C, primește lista deja extrasă și returnează DOAR ce lipsește; analizele recuperate primesc explicație într-un apel mic suplimentar și ridică `expected_count`. Protecții: duplicate ignorate, intrări fără valoare ignorate, maxim 15 recuperări, eșec silențios. Comutator: `Gemini:EnableCompletenessSweep`.
  - **Admin nu mai induce în eroare:** `ModelUsed` stochează modelele reale (`split: 2.5-flash|2.5-flash|2.5-pro`), fiecare rând are badge **split** / **monolitic**, iar tabelul A/B/C are coloana „Măturare (A2)” cu numărul de analize recuperate.
  - Validare: `/app/test_reports/iteration_15.json` — **46/46 PASS** (e2e cu Gemini fals local + teste unitare pe completer). Build: 0 erori / 0 warning-uri. Fără migrații EF.

- ✅ **2026-06 — Optimizare Gemini: pipeline pe 3 apeluri (A / B / C) în loc de un apel monolitic**:
  - **Etapa A — EXTRAGERE** (`gemini-3.5-flash`, thinkingLevel=low): doar tabelul, fără proză. Refolosește promptul de sistem INTEGRAL + un addendum care suprascrie doar contractul de ieșire, deci toate regulile de extragere (completitudine, primul/ultimul rând, `parameter_normalized_en`, panel/analyte raw, markeri de laborator, self-audit) rămân în vigoare.
  - **Etapa B — EXPLICAŢII** (`gemini-3.1-flash-lite`, thinkingLevel=minimal): loturi de 12 analize care pleacă în PARALEL; primește doar tabelul, nu PDF-ul. Adâncimea explicațiilor a rămas cea de azi (decizia userului).
  - **Etapa C — NARATIV** (`gemini-3.1-pro`, thinkingLevel=medium): summary, abnormal_findings, corelații, recomandări, risc, întrebări pentru medic. Rulează SIMULTAN cu B → timpul devine A + max(B, C).
  - **Cascade per etapă:** lot B picat → retry pe același model, apoi pe modelul narativ, iar dacă tot pică doar acele analize rămân fără explicație; etapa C → retry pe al doilea model; orice eșec nerecuperabil → **fallback automat la apelul monolitic de azi**, deci utilizatorul primește întotdeauna raport complet.
  - **Flag:** `Gemini:PipelineMode` = `monolithic` (implicit, comportament neschimbat) | `split`. Modelele, nivelurile de thinking și dimensiunea lotului sunt toate în `appsettings.json`.
  - **thinkingLevel vs thinkingBudget:** Gemini 3.x folosește `thinkingLevel` (minimal/low/medium/high), 2.5 rămâne pe `thinkingBudget`; se trimite exact unul, niciodată ambele.
  - **Admin → Performanță:** tabel nou „Pipeline pe 3 apeluri” cu durata, tokenii, tokenii de thinking și **costul estimat în USD pe fiecare etapă** + total (tarife separate pentru flash-lite / 3.x flash / pro).
  - **CAM/B2B rămâne pe apelul monolitic** (trimite mereu un model de tier explicit; split-ul se activează doar când nu e forțat un model).
  - **2026-06 (la cererea userului):** `appsettings.json` are acum `"PipelineMode": "split"` PORNIT pentru testare, plus chei `_README_*` cu instrucțiuni inline (cum se revine la `monolithic`, ce face fiecare model, ce e ExplainBatchSize, unde se văd rezultatele în /Admin/Performance). Cheile `_README_*` sunt ignorate de binder.
  - Validare: `/app/test_reports/iteration_14.json` — **34/34 PASS end-to-end** cu un server Gemini fals local (paralelism real B‖C verificat pe timestamps, mapare explicații pe index, retry de lot, fallback monolitic, mod monolitic neatins, tarife). Build: 0 erori / 0 warning-uri. Fără migrații EF.

- ✅ **2026-06 — Același cod LOINC cu unități diferite = rânduri separate (cazul Fibrinogen g/L vs mg/dL)**:
  - **Cauza (nu era unificatorul):** gruparea în Comparații / Dosar / Grafice se făcea DOAR pe codul LOINC. Ambele Fibrinogen au primit corect același cod `3255-7`, dar unul raportat în g/L (4.32, ref 2-4) și altul în mg/dL (516, ref 200-400) — deci cădeau pe același rând, cu un singur interval de referință și două scale incomparabile.
  - **`LoincUnifier.UnitScope`** (nou): numără unitățile normalizate per cod în toată arhiva; dacă un cod a fost raportat în >1 unitate, cheia de grupare primește sufixul `|u=<unitate>` → fiecare scală are rândul/cardul/seria proprie. Măsurătorile FĂRĂ unitate intră în bucketul majoritar (nu creează un al treilea rând). Codurile cu o singură unitate păstrează cheia identică → zero regresie.
  - Cablat în `BuildComparison`, `BuildDossier` și `BuildEvolutionAsync` (un cod „split” produce mai multe serii, cea mai frecventă prima; `CodesNotFound` recalculat pe cod).
  - Validare: `/app/test_reports/iteration_13.json` — 24/24 PASS, inclusiv regresia pe unificarea LOINC (colesterol, INR, feritină). Build: 0 erori / 0 warning-uri. Fără migrații EF.

- ✅ **2026-06 — Eliminarea markerilor de rutare ai laboratorului din denumirea analizei („LLIS”, „#LC”)**:
  - **Problema (foto user):** unele laboratoare tipăresc în marginea din stânga un cod intern (care aparat / partener a lucrat proba). În modul TEXT, PdfPig pune marginea și denumirea pe aceeași linie, deci apărea „LLIS Amilaza serica” / „LLIS Trigliceride” în raport și în numele trimis matcher-ului LOINC.
  - **`Services/LabMarkerSanitizer.cs`** (nou, ~140 linii, determinist, FĂRĂ listă de acronime ca mecanism): un token de 2-6 majuscule (opțional prefixat cu `#`/`*`) e eliminat DOAR dacă deschide ≥ 3 parametri DISTINCŢI din același buletin (markerii de margine se repetă; prefixele medicale reale nu) și dacă restul rămâne un nume valid (≥ 3 caractere, cu litere mici). Curăță și `parameter_normalized_en` (deci matcher-ul LOINC primește nume curate) și redenumeste `abnormal_findings` în sincron.
  - **Plasă anti-regresie:** allow-list de abrevieri care pot deschide legitim un nume (LDL, HDL, VLDL, AC, IgG/IgA/IgM, TSH, FT3/FT4, PSA, CEA, CA, INR, VSH, GGT, ALT/AST, LDH, MCV/MCH/MCHC, PH, PT/APTT, ...) — niciodată eliminate, indiferent de frecvență.
  - **Cablare:** `InterpretationController` (B2C) și `CamBatchService` (B2B/CAM), imediat după Gemini și îNAINTE de StatusValidator + matcher-ul LOINC, în try/catch (pas cosmetic, nu poate rupe fluxul). Plus **Layer 1**: regulă generică nouă în promptul Gemini („LAB ROUTING CODES ARE NOT PART OF THE NAME”) ca markerul să nu intre deloc.
  - **Decizie user:** se aplică doar interpretărilor NOI; arhiva existentă rămâne cu numele vechi. Fără loguri. Fără migrații EF.
  - Validare: `/app/test_reports/iteration_12.json` — 18/18 verificări PASS (inclusiv cazul real din buletin, `#LC`, prefixe medicale intacte, prag de frecvență, idempotență). Build: 0 erori / 0 warning-uri. Proba păstrată la `/app/memory/probes/LabMarkerSanitizerProbe.cs.txt`.

- ✅ **2026-06 — Unificare retroactivă a codurilor LOINC la afișare (Dosar medical, Comparații, Grafice)**:
  - **Problema:** matcher-ul semantic + variabilitatea de formulare a Gemini produc coduri LOINC diferite pentru ACEEAșI analiză în buletine diferite (ex. Colesterol total `2093-3` vs `14647-2`), deci istoricul se rupea în două rânduri/carduri/serii.
  - **`Services/LoincUnifier.cs`**: `Analyze(allResults)` construiește (a) `CodeMap` (cod vechi -> cod unificat) doar când **Nume + Unitate de măsură + Interval de referință** coincid după normalizare (lowercase, fără diacritice/punctuație; unități canonice µL/uL, 10^3; intervale comparate NUMERIC) și (b) `MissingAxisByName` ("unit"/"range"/"both") pentru cazurile pe care am REFUZAT să le unificăm. Cod câștigător: ancoră (verified) > scor semantic > număr apariții > cod stabil.
  - **`ProfilesController`**: `BuildComparison`, `BuildDossier` și `BuildEvolutionAsync` aplică harta înainte de grupare; în Evoluție se unifică și codurile lipite de utilizator (un cod vechi continuă să deseneze seria). Drift warning-ul `⚠` din Compare se calculează acum pe codurile POST-unificare, deci nu mai alarmează pentru ce am rezolvat.
  - **Regula de prudență (cerută de user):** dacă pe un buletin lipsește UM sau intervalul, NU se unifică; rândul/cardul rămâne dublu și primește un **`!` gri discret** cu tooltip localizat în 7 limbi (`LoincUnifyMissingUnitTip` / `...RangeTip` / `...BothTip`).
  - **Bug prins de teste:** `NormalizeRange` interpreta `-` din "0-200" ca semn minus (rezulta `[0, -200]` vs `[0, 200]` pentru "0 - 200"), deci NIMIC nu se unifica niciodată. Cratimele/dash-urile sunt acum separatori.
  - **Zero migrații EF** — schimbare exclusiv la nivel de afișare, schema DB neatinsă. Build: 0 erori / 0 warning-uri (inclusiv CS0414 din `AdminController` nu mai apare).
  - **Corecție 2026-06 (feedback user, cazul INR):** axa lipsă blochează unificarea DOAR când e **inconsistentă** (prezentă pe un buletin, absentă pe altul). Analitele adimensionale (INR, Raport A/G, indici) nu au UM pe NICIUN buletin — asta nu e incertitudine, deci se unifică normal și nu mai afișează `!`. Validat: 46/46 verificări PASS.
  - Validare: `/app/test_reports/iteration_11.json` — 40/40 verificări PASS (probe .NET console pe codul real: LoincUnifier + BuildComparison + BuildDossier prin reflecție + paritatea celor 21 de chei Loc).
- ✅ **2026-02 — Migrare SMTP: Gmail → Brevo relay (contact@mymedicalapp.net)**:
  - **Context:** user a cumpărat domeniul `mymedicalapp.net` (GoDaddy), a creat contul Microsoft 365 `contact@mymedicalapp.net`, l-a înregistrat pe Brevo (SPF+DKIM configurate în DNS-ul GoDaddy). Toate emailurile tranzacționale merg acum prin Brevo relay pentru deliverability profesional.
  - **`appsettings.json` — EmailSettings**: `SmtpServer=smtp-relay.brevo.com`, `SmtpPort=587`, `SenderEmail=contact@mymedicalapp.net` (afișat în „From"), `SenderName=MyMedicalApp.NET`, `Username=b1b34c001@smtp-brevo.com` (credential relay Brevo), `Password=<Brevo SMTP key>`. Codul din `EmailService.cs` deja distingea `SenderEmail` de `Username` — zero cod C# modificat.
  - **`appsettings.json` — AdminSettings**: adăugat `contact@mymedicalapp.net` ca prim destinatar al alertelor admin (Budget alert Gemini, Daily summary, Purchase notifications) alături de `vasilepintilie2003@gmail.com` ca backup.
  - **`Services/StartupSeed.cs` linia 157**: `operatorRedirectEmail` (unde ajung emailurile pacienților fictivi din contul demo B2B) actualizat la `contact@mymedicalapp.net` pentru consistență.
  - **Cont Admin `contact@mymedicalapp.net`**: userul îl creează manual prin flow-ul normal Register + VerifyEmail, apoi `UPDATE Users SET IsAdmin=1 WHERE Email='contact@mymedicalapp.net';` — zero cod nou (evită hardcodare parolă în seed).
  - **⚠️ Recomandare securitate**: parola SMTP Brevo e acum în `appsettings.json`. Repo-ul e privat, dar best practice: migrare la `dotnet user-secrets init && dotnet user-secrets set "EmailSettings:Password" "..."` (parola trăiește local pe PC, niciodată în git). P1 din backlog împreună cu cheile OpenAI/Gemini.


- 🐛 **2026-02 — Bug fix: Landing header depășea ecranul după rebrand**:
  - **Simptom:** după rebrand `MedicalApp+` → `MyMedicalApp.NET+`, header-ul Landing (brand + 5 link-uri meniu + limbă + Sign In + Get Started) rămase pe un singur rând flex → depășea viewport-ul pe ≤1280px.
  - **Fix (CSS/HTML-only):** restructurat `<nav class="land-nav">` în **2 rânduri distincte**:
    - **Rând 1** — `.land-nav-brand-row`: doar brand-ul, centrat orizontal cu `justify-content: center`, font-size ridicat la `1.5rem` pentru vizibilitate solo.
    - **Rând 2** — `.land-nav-inner` (existent, doar padding-top eliminat): nav links stânga + language dropdown + Sign In + Get Started dreapta, cu `justify-content: space-between` păstrat.
  - **Mobile (≤768px):** brand row cu padding + font-size reduse (`1.25rem`), action row cu `justify-content: center` + `gap: 0.5rem` pentru echilibru vizual (nav links deja ascunse pe mobil).
  - **Zero atingeri** pe restul Landing-ului: Hero, pillars, pricing, footer neschimbate. Toate `data-testid` păstrate (`land-nav`, `land-brand`, `nav-how/compare/features/clinics/pricing`, `land-signin`, `land-getstarted`, `land-lang-btn`).
  - Validare statică (`/app/test_reports/iteration_10.json`): 9/9 acceptance criteria PASS, brace balance 81/81 Razor + 325/325 CSS, zero regresii, zero action items.


- ✅ **2026-02 — Rebranding: MedicalApp → MyMedicalApp.NET (domeniu nou www.mymedicalapp.net)**:
  - **Regula:** brand vizibil = `MyMedicalApp.NET` (cu extensie peste tot), URL vizibil = `www.mymedicalapp.net` (lowercase, standard web), email contact = `contact@mymedicalapp.net`.
  - **Fișiere modificate (16 total):**
    - `Services/Loc.cs` — 199 apariții `MyMedicalApp.NET` în 7 limbi, toate URL-urile `www.MedicalApp.com` → `www.mymedicalapp.net`; paritate 1010 chei/limbă păstrată; `AppTitle` = "MyMedicalApp.NET" în toate 7 limbi.
    - `Services/EmailSettings.cs` — `SenderName` default `"MyMedicalApp.NET"` (display name pentru „From" în toate e-mailurile SMTP).
    - `Services/PdfReportGenerator.cs` — header/footer PDF cu URL nou.
    - `Services/BudgetAlertService.cs`, `Services/DailySummaryService.cs`, `Services/CamBatchService.cs` (LabelFor: engine tiers), `Services/CamPatientEmailBuilder.cs` (Powered-by în emailuri pacient).
    - `Controllers/AccountController.cs` (register/verify/reset emailuri), `Controllers/AdminController.cs` (reset user password + admin notif), `Controllers/CreditsController.cs` (subject `[MyMedicalApp.NET] Achizitie noua ...`), `Controllers/InterpretationController.cs` (result email HTML), `Controllers/ProfilesController.cs` (compare/email report HTML).
    - `Views/Home/Landing.cshtml` (`<title>` browser tab, brand pill span-uri, mailto: footer → `contact@mymedicalapp.net`), `Views/Home/Index.cshtml` (placeholder image alt), `Views/Admin/SendEmail.cshtml` (preview header), `Areas/CAM/Views/Dashboard/Index.cshtml` (Powered-by link).
  - **PROTEJAT (deliberat neschimbat):**
    - **C# namespace `MedicalApp.Services`** — ~200 fișiere depind de el, rename = spargere garantată. Rename separat, cu Visual Studio Rename tool, e o operație distinctă viitoare.
    - **`[MedicalApp]` marker de protocol CAM** în `Services/CamPdfMetadataExtractor.cs` regex + badge în `Areas/CAM/Views/CheckPdfs/Index.cshtml` — schimbarea ar sparge identificarea pacienților din PDF-urile deja emise de clinici. Necesită tranziție cu recunoaștere duală + retraining operatori.
    - **Prompt-uri sistem LLM** (`GeminiMedicalInterpretationService.cs:774`, `MedicalInterpretationService.cs:127`) — schimbarea ar putea influența calitatea răspunsurilor Gemini/OpenAI (LLM ar putea începe să includă `.NET` în interpretări). Rămâne `MedicalApp` intern.
    - **`SenderEmail` din `appsettings.json`** = `vasilepintilie2003@gmail.com` — user actualizează manual când provisionează `contact@mymedicalapp.net` + App Password Gmail/M365.
    - **Comentarii XML doc** (`Models/InterpretationResult.cs`, `Services/CamBatchService.cs`, `SupportedLanguagesConfig.cs`) — non-vizibil, curățare viitoare opțională.
  - Validare statică (`/app/test_reports/iteration_9.json`): 16 fișiere modificate, 199 rewrites Loc.cs, brace balance = 0 în toate, paritate 7 limbi păstrată, protejate toate identificatoarele critice. Zero URL-uri bare `mymedicalapp.net` fără `www.`.


- ✅ **2026-02 — Feature: melodie „finale" mai lungă (2.5s) la sfârșitul interpretării B2C**:
  - **Cerință:** sunetul de terminare al mascotei doctor să dureze 2-3 secunde la finalul unei interpretări B2C (înainte nu se emitea niciun sunet — B2C făcea doar redirect fără feedback audio).
  - **`wwwroot/js/doctor-mascot.js`**: adăugat `playInterpretationFinale(instance)` — 6 note ascendente C major (C5→G6, 0.16s per notă) urmate de un acord susținut C major (C6+E6+G6 în sine wave) ~1s. Total ~2.5 secunde. Melody folosește triangle wave (cald), chord final folosește sine (blând). Respectă `soundMuted` din localStorage + `ctx.resume()` pentru cazul când AudioContext e suspendat (Chrome autoplay policy).
  - **`window.DoctorMascot.playInterpretationFinishSound()`** (nou, expus global): găsește prima instanță existentă `.doc-mascot` pe pagină și reutilizează AudioContext-ul ei (pentru a păstra preferința „silențios"); fallback pe context temporar dacă nu există mascotă.
  - **`Controllers/InterpretationController.cs` linia 786**: la finalul reușit al interpretării B2C (înainte de `RedirectToAction("Dashboard", "Account")`), setează `TempData["PlayInterpretationSuccessSound"] = "1"` — flag single-shot.
  - **`Views/Account/Dashboard.cshtml`**: block Razor `@if (TempData["PlayInterpretationSuccessSound"] == "1")` emite un mic script inline cu `setTimeout(fire, 200)` care apelează `window.DoctorMascot.playInterpretationFinishSound()`. 200 ms lasă `autoInit` al mascotei să ruleze primul.
  - **Scope:** afectează DOAR B2C (redirect după interpretare). CAM Batch continuă să folosească `playFanfare` (~1s) — user a cerut modificare doar la B2C.
  - Testing: lint JS PASS, brace balance OK. Modificare izolată, self-test suficient — user testează local pe VS2026.


- ✅ **2026-02 — Feature: 2 butoane pe pagina „Analiză deja interpretată" (Download + Email)**:
  - **Cerință:** butonul unic „Deschide raportul existent" înlocuit cu două butoane distincte — „Descarcă interpretarea" (forțează descărcarea PDF, fără deschidere Acrobat) și „Trimite-o pe email" (regenerează PDF-ul și îl trimite ca atașament la emailul userului).
  - **`Controllers/ProfilesController.cs`**: extras helperul `TryRegenerateReportPdfAsync(int id)` din `DownloadReport` — face DB lookup + JsonSerializer.Deserialize + `_pdfGenerator.Generate` cu freemium gating, returnează `(byte[]? pdf, string? fileName, IActionResult? errorResult)`. `DownloadReport` devine ~14 linii (guard + call + `File(..., attachment)`). Nouă acțiune `EmailReport(int id, int? profileId)` POST cu `[ValidateAntiForgeryToken]` folosește același helper, apoi construiește HTML body cu Loc.T (culture capturat up-front) și trimite via `_emailService.SendEmailWithAttachmentAsync`. Pe eroare: TempData ErrorMessage + redirect la History/Upload; pe succes: TempData SuccessMessage cu emailul userului + același redirect.
  - **`Views/Interpretation/DuplicateDetected.cshtml`**: în coloana stângă, blocul cu un singur `<a>` (`btn-open-existing-report`) devine `.d-grid.gap-2` cu (a) `<a>` download primary (`btn-download-existing-report`) și (b) `<form>` POST spre EmailReport cu AntiForgery + hidden `id`+`profileId` + button outline-primary (`btn-email-existing-report`). Coloana dreaptă (Re-interpretează) neatinsă.
  - **`Services/Loc.cs`**: 5 chei noi × 7 limbi: `DupDownloadTitle`, `DupSendByEmailTitle`, `DupEmailSentFmt` (cu `{0}` pentru email), plus `ErrReportCannotBeReconstructed` și `ErrPdfGenerationFailed` (elimină vechile stringuri hardcodate în română din DownloadReport). Total: 1010 chei/limbă cu paritate completă.
  - **PDF force-download**: `File(bytes, \"application/pdf\", fileName)` (3-arg overload) setează `Content-Disposition: attachment; filename=...` — browser-ul salvează fișierul în Downloads fără să încerce să-l deschidă în Acrobat/Reader. Aceasta e comportamentul de care userii fără PDF viewer instalat aveau nevoie.
  - Validare statică (`/app/test_reports/iteration_8.json`): 8/8 verificări trecute, zero regresii, placeholder-uri `{0}` verificate în toate 7 limbi.


- 🐛 **2026-02 — Bug fix: auto-login după VerifyEmail (redirect greșit spre Landing în loc de Interpretare)**:
  - **Simptom:** utilizator dă click pe „Interpretare gratuită" → se înregistrează Persoană fizică → primește codul de 4 cifre pe email → introduce codul → aterizează pe pagina **Landing** (marketing) în loc de `/Interpretation/Upload`, chiar dacă userul este creat corect în DB.
  - **Cauză root:** `AccountController.VerifyEmail` POST (linia 374) făcea `RedirectToAction("Index", "Home")`. `HomeController.Index` (linia 13-19) renderează `Landing` pentru orice vizitator FĂRĂ session cookie. Codul crea userul în DB dar nu seta niciodată `HttpContext.Session["UserEmail"]`, așa că nu era „logat" — deci Home/Index îl trimitea la Landing. Bug regresie introdus atunci când pagina Landing a înlocuit vechiul default `/Home/Index=login form`.
  - **Fix:** înainte de redirect, setăm `Session["UserEmail"] = user.Email` + `Session["JustLoggedIn"] = "1"` (aceleași chei ca Login normal), apoi:
    - **B2C**: `RedirectToAction("Upload", "Interpretation")` — direct la formularul de upload PDF, cu creditul freemium activ (BonusCredits=1).
    - **B2B/Clinic**: cache `ClinicId` prin `AsNoTracking()` (aceeași optimizare ca Login pentru polling la 3s), apoi `RedirectToAction("Index", "Dashboard", new { area = "CAM" })`.
  - Linia veche `TempData["ActiveTab"] = "login"; return RedirectToAction("Index", "Home");` eliminată (inutilă — Landing nu citea ActiveTab).
  - Rutele error-path (cod expirat, prea multe încercări) rămân neatinse — acolo redirectul spre Home/Index e corect (userul e neautentificat legitim).
  - Validare statică (`/app/test_reports/iteration_7.json`): 7/7 verificări trecute, brace balance OK, zero regresii.


- ✅ **2026-02 — Blocare tab „Clinică" pe fluxul „Interpretare gratuită"**:
  - **Cerință:** când vizitatorul dă click pe un CTA care promite „interpretare gratuită" (Hero, PillarInd, Compare, Pricing), formularul de Înregistrare trebuie să afișeze DOAR opțiunea „Persoană fizică" — B2B/Clinic ascuns complet. Alte CTA-uri (header signin, PillarLab, PillarCln, B2B strip) rămân neafectate.
  - **`Views/Home/Landing.cshtml`**: adăugat helper `AuthUrlFree(string tab)` care generează `/Home/Auth?tab=register&flow=free`. Migrate exact 4 CTA-uri (hero-cta-primary, pillar-ind-cta, compare-cta, pricing-cta). Cele 5 CTA-uri de tip B2B/navigație (land-signin, land-getstarted, pillar-lab-cta, pillar-cln-cta, b2b-cta) rămân pe `AuthUrl` clasic.
  - **`Controllers/HomeController.cs`**: `Auth(tab, flow)` acceptă noul param `flow`; dacă `flow=="free"` (case-insensitive) setează `ViewData["Flow"] = "free"`.
  - **`Controllers/AccountController.cs`**: `Register(model)` citește `Request.Form["flow"]` la începutul acțiunii; dacă e „free" coerce `UserType="Individual"` și golește Clinic-fields (Name/City/Address) ÎNAINTE de validarea Clinic-required — apărare defense-in-depth împotriva unui POST modificat manual. Setează ViewData["Flow"] pentru a supraviețui re-randării la eroare de validare.
  - **`Views/Home/Index.cshtml`**: nou `isFreeFlow` boolean; când e true: (a) UserType e forțat „Individual", (b) hidden `<input name="flow" value="free">` adăugat în form pentru propagare pe POST, (c) radio-ul `userTypeClinic` + label-ul lui înconjurate de `@if (!isFreeFlow)` — NU se randează deloc în DOM, (d) radio-ul Individual force-checked. JS existent de toggle `#clinicFields` are deja guard `if (!rClinic) return;` — bail-out safe când Clinic nu e randat.
  - Validare statică (`/app/test_reports/iteration_6.json`): 9/9 verificări trecute; zero regresii pe Login, VerifyEmail, header nav sau alte CTA-uri.


- 🐛 **2026-02 — Bug fix: profil implicit „Eu" lipsă pentru useri noi înregistrați după boot**:
  - **Simptom:** un user B2C nou-înregistrat cu parolă puternică (după activarea politicii de complexitate) intră pe `/Interpretation` și vede dropdown gol pentru profil („-- Selectează profilul --", nici o opțiune).
  - **Cauză root:** `Services/StartupSeed.EnsureDefaultProfilesAsync` creează profilul „Eu" doar la **pornirea aplicației** pentru userii existenți care nu au încă profil. NU rulează pentru userii care se înregistrează **după** ce aplicația e deja pornită. `AccountController.VerifyEmail` (unde se creează efectiv userul în DB) nu avea niciun cod care să adauge profilul implicit.
  - **Fix:** în `Controllers/AccountController.cs`, imediat după `_db.Users.Add(user);` (linia 280), adăugat `_db.Profiles.Add(new Profile { UserEmail = user.Email, Name = Loc.T("DefaultProfileNameSelf"), Relationship = "self", IsDefault = true, CreatedAt = DateTime.UtcNow });`. Ambele INSERT-uri intră în aceeași tranzacție cu `SaveChangesAsync`; EF Core ordonează automat User→Profile (FK constraint).
  - **Localizare:** cheie nouă `DefaultProfileNameSelf` × 7 limbi în `Services/Loc.cs` (en=„Me", ro=„Eu", fr=„Moi", es=„Yo", de=„Ich", it=„Io", pt=„Eu"). Total 1005 chei/limbă, paritate perfectă.
  - **Discriminator:** `IsDefault=true` este cheia (nu numele) — `InterpretationController.Index` line 97 selectează profilul cu `IsDefault=true` ca default în dropdown; nu a fost nevoie de modificări în controller-ul de interpretare.
  - **Fără regresii:** `StartupSeed.EnsureDefaultProfilesAsync` rămâne intact — continuă să facă backfill pentru userii legacy la boot. Const-ul `DefaultProfileName = "Eu"` din StartupSeed rămâne hardcodat (existenții au deja „Eu" în DB — consistență). B2B/CAM nu e afectat (userii clinici folosesc `/CAM` nu `/Interpretation`).
  - Validare statică (`/app/test_reports/iteration_5.json`): 8/8 verificări au trecut, zero issues, zero regresii.


- ✅ **2026-02 — Politică de complexitate parolă (Register + ResetPassword, B2C+B2B)**:
  - **`Models/LocalizedAttributes.cs`**: adăugat atributul `LocalizedPasswordComplexityAttribute` (ValidationAttribute + IClientModelValidator) care impune 5 reguli: min 8 caractere, ≥1 majusculă, ≥1 minusculă, ≥1 cifră, ≥1 caracter special din setul explicit `!?@#$%^&*` (const `SpecialChars`). Empty/null returnează `true` — delegăm către `LocalizedRequired` pentru evita eroarea dublă „Field required" + „Rules not met". Emite atributele `data-val-pwdcomplex-*` (header/min/upper/lower/digit/special/specialset) pentru adaptorul JS.
  - **`Models/AuthViewModels.cs`**: `RegisterViewModel.Parola` și `ResetPasswordViewModel.Parola` folosesc acum `[LocalizedPasswordComplexity] + [StringLength(100)]` (înlocuiește `LocalizedStringLength(100, "PasswordMinLength", MinimumLength=6)`). `LoginViewModel.Parola` **NU** are politica — userii existenți cu parole vechi (6 caractere) continuă să se logheze fără impediment.
  - **`Services/Loc.cs`**: adăugate 6 chei noi × 7 limbi = 42 traduceri (PasswordRulesTitle, PasswordRuleMinLength, PasswordRuleUpper, PasswordRuleLower, PasswordRuleDigit, PasswordRuleSpecial) — total 1004 chei/limbă. Toate traducerile listează explicit setul de caractere speciale `!?@#$%^&*` pentru claritate.
  - **`wwwroot/js/password-complexity.js`** (nou, ~5 KB): (1) înregistrează metoda jQuery Validate `pwdcomplex` + adaptor unobtrusive cu 7 parametri; (2) scanează la `DOMContentLoaded` toate input-urile `[type=password][data-val-pwdcomplex-min]` și le împachetează într-un container `.pwd-complex-wrap`; (3) adaugă buton info „(i)" în colțul dreapta-sus al input-ului cu Bootstrap 5 Popover `trigger:'click'` care afișează lista celor 5 reguli în limba userului; (4) adaugă panou de live-feedback sub input care schimbă între ✓ (verde) și ✗ (roșu) per regulă, în timp real, pe fiecare `input`/`focus` event. Toate textele vin din atributele `data-*` — zero string-uri hardcodate în JS.
  - **`wwwroot/css/password-complexity.css`** (nou, ~1.8 KB): stilizează wrap-ul, butonul (i), popover-ul și panoul de feedback. Include `white-space: pre-line; display: block;` pe `.field-validation-error` și `.text-danger` pentru ca lista multi-linie a regulilor să se afișeze corect la submit fail.
  - **`Views/Home/Index.cshtml`** (unifică Register B2C + B2B/CAM prin radio-button UserType) și **`Views/Account/ResetPassword.cshtml`**: adăugat `<link>` CSS în top + `<script>` JS în secțiunea Scripts. Nu s-au modificat structurile view-ului; feature-ul se aplică automat prin data-atribute.
  - Validare statică (`/app/test_reports/iteration_4.json`): 9/9 verificări au trecut. Zero bug-uri; comentariile de code-review au fost aplicate (display:block în CSS).


- ✅ **2026-02 — Adăugare limba portugheză (PT), a 7-a limbă suportată**:
  - **`Services/SupportedLanguagesConfig.cs`**: adăugat al 7-lea `LangDef` (Code `pt`, CultureCode `pt-PT`, LangName `Portuguese (Português)`, NativeName `Português`, FlagEmoji 🇵🇹, 12 luni long + 12 luni short). Datorită refactor-ului din Faza 1-3 (centralizare config), această modificare unică propagă automat PT în 8 locuri: `Program.cs`, `_Layout.cshtml` (JS auto-detect), `Home/Landing.cshtml`, `Home/Index.cshtml`, `GeminiMedicalInterpretationService`, `MedicalInterpretationService`, `CamBatchService`, `SamplingDateParser.cultures[]`.
  - **`Services/Loc.cs`**: adăugat blocul `["pt"] = new() { ... }` cu **998 chei** traduse ES→PT prin Gemini 3.5 Flash (25 chunks × 40 chei, retry pe erori). Ordinea și setul de chei sunt identice cu EN (verificat: 998/998, zero missing/extra). Escape-uri C# preservate (`\n`, `\"`, `\uXXXX`). Convenții portugheza europeană (ficheiro/utilizador/ecrã, nu arquivo/usuário/tela). Cele 9 apariții de „arquivo" mapează pe cheile de arhivă (`EvolutionPageBtnBackArchive`, `HistoryPageTitleFmt`, etc.) — utilizare corectă în sensul de „arhivă/depozit", nu de „fișier".
  - **`Services/SamplingDateParser.cs`**: adăugate 15 tokeni portughezi în `MonthLookup` (janeiro, fev, fevereiro, março, marco, abr, abril, maio, junho, julho, setembro, out, outubro, dez, dezembro). Tokenul `set` a fost omis intenționat (deja mapat de italiană pe 9, ambele valide — evită excepția de duplicate key la init). Fallback-ul `cultures[]` folosește `SupportedLanguagesConfig.CultureCodes` deci auto-detectează `pt-PT`.
  - Validare statică (`/app/test_reports/iteration_3.json`): 16/16 verificări structurale au trecut — echilibru braces (`{}=0`), număr chei per limbă (998 × 7 = 6986 keys), zero placeholders lipsă/în plus, HTML tags păstrate, ghilimele native portugheze `«»` (`\u00AB`/`\u00BB`) în loc de germane `„"` (`\u201E`/`\u201D`) — corect cultural.
  - **Metodă adăugare limbă:** cu refactor-ul centralizat, o limbă nouă se adaugă acum în 3 pași (2 automați + 1 manual): (1) tuplu în `SupportedLanguagesConfig.cs`, (2) dicționar PT în `Loc.cs`, (3) hardcodat: luni în `SamplingDateParser.MonthLookup`. Ghidul `Docs/Adding_New_Language.md` reflectă vechiul flow — necesită mini-update pentru a menționa că pașii 2-8 sunt acum automați.


- ✅ **2026-02 — Phase 7 traduceri: GDPR clinică + emailuri share Compare/Evolution**:
  - **`Views/Home/Index.cshtml`** (card register-clinic): notele GDPR + Windows-only acum folosesc `Loc.T`. Pentru fraza cu emfaze („**Important:** ... **numai cu Windows**.") am folosit o cheie unică cu markup HTML inline (`Html.Raw`) — soluție pragmatică, fiecare limbă alege ce să bold-uiască.
  - **`Controllers/ProfilesController.cs`** (2 emailuri):
    - Email Compare (linia 470): subject + body cu greeting/intro/cod-uri/goodbye + mesaj de eroare. Toate folosesc `Loc.T(key, lang)` cu lang capturat la entry-ul acțiunii (same pattern ca InterpretationController, anti-thread-pool drift).
    - Email Evolution (linia 970): idem + key dedicată pentru lista de coduri LOINC.
  - **`Loc.cs`**: +9 chei × 5 limbi = **+45 traduceri**. Total: **616 chei × 5 limbi = 3080 traduceri**.
- ✅ **2026-02 — Translation Coverage Dashboard** (`/Admin/TranslationCoverage`) — vede în timp real ce limbă are missing keys / extra keys / top 10 cele mai lungi traduceri.
- ✅ **2026-02 — Phase 6 traduceri: History (arhivă profil)** (+36 chei × 5 limbi).
- ✅ **2026-02 — FIX bug critic: email body în limba greșită** (Loc.T overload cu languageCode explicit).
- ✅ **2026-02 — Phase 5 traduceri: DuplicateDetected + email "for profile"**.
  - **`Views/Profiles/History.cshtml`**: rescrisă complet cu `Loc.T(...)`. Inclus: titlu, heading, badge-uri singular/plural, banner-ul premium (gratuit / plătit cu format dynamic placeholders), tabel (Data / Fișier original / Data recoltării / Parametri / Anormalități / Acțiuni), modalul de evoluție (intro lung cu LOINC, label, placeholder, help, buton Generează grafic), modal ștergere (date/fișier/notă + butoane), tooltip-uri pentru disabled/delete/unavailable, link „Înapoi la profile", link „Încarcă prima analiză".
  - **`Loc.cs`**: +36 chei × 5 limbi = **+180 traduceri noi**. Total: **607 chei × 5 limbi = 3035 traduceri**.
  - Banner-ul premium folosește `Html.Raw + string.Format` cu `HtmlEncode` pe șablon (anti-XSS) și `<strong>{0}</strong>` injectat pentru data / count.
- ✅ **2026-02 — FIX bug critic: email body în limba greșită** (Loc.T overload cu languageCode explicit).
- ✅ **2026-02 — Phase 5 traduceri: DuplicateDetected + email "for profile"**.
- ✅ **2026-02 — Buton „Evoluție grafică" + „Compară selectate" disabled cu tooltip**.
- ✅ **2026-02 — B2C: fallback automat TEXT → VISION** când extracția PdfPig nu vede analize.
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

## Recently completed (Feb 2026)

- ✅ **[Feb 2026 — B2B/CAM Phase 2 Translations]** Final B2B/CAM module strings
  translated into all 5 languages (RO/EN/FR/ES/DE) via the central `Loc.cs`
  dictionary. Scope of this batch:
    * `Areas/CAM/Views/Dashboard/Index.cshtml` — full rewrite using
      `@Loc.T("CamDash...")` keys (KPI cards, batches stats per year/month,
      quick actions, disk usage + cleanup confirm, batch history table, folders
      panel). Month names follow `CultureInfo.CurrentUICulture` instead of
      hard-coded `ro-RO`.
    * `Areas/CAM/Views/CheckPdfs/Index.cshtml` — full rewrite using
      `@Loc.T("CamCheck...")` keys (upload form, method 1/2 help, summary
      badges, table columns + email validity badges, source badges, edit
      modal, delete-confirm with localized JS template via
      `System.Text.Json.JsonSerializer.Serialize`). `[MedicalApp]` example
      block now sourced from `Loc.T("CamCheckBlockExample")` so the label
      (`Patient:` / `Pacient:` / `Nom:` / `Nombre:` / `Name:`) matches the UI
      language while staying compatible with the extractor regex.
    * `Services/CamPdfMetadataExtractor.cs` — added `ReasonKey` property on
      `CamPdfMetadata` populated alongside the English `Reason` (gold path
      vs. unreadable / empty text / not-medical / blacklisted / no email /
      no name). DB still stores the English text for stable traceability.
    * `Services/CamBatchService.cs` — pre-filter non-medical PDFs now logs
      the live message via `Loc.T(probe.ReasonKey, lang)` so the operator
      sees the message in their selected UI language while `RecordErrorAsync`
      keeps the English `Reason` in the DB.
    * `ClassifyEmailFailure()` translated to **English-only** (per user's
      explicit choice: "doar EN" — technical/log message, not UI).
    * Total: ~128 new translation keys × 5 languages = 640 dictionary
      entries inserted right after the existing `CamBatchLog*` block.


- ✅ **[Feb 2026 — B2B/CAM Bug Fix: Language not propagated to Interpretation]**
  Multiple bugs were preventing the operator's UI language from reaching the
  actual batch outputs (Gemini, interpretation PDF labels, compare PDF). Fixes:
    * `CamBatchService.RunAsync`: language was hard-coded `"ro"` on the
      Gemini call (`gemini.InterpretPdfAsync(ms, fileName, "ro", ...)`) —
      so Gemini always returned the interpretation in Romanian regardless
      of the operator's chosen language. Now passes `lang` correctly.
    * `CamBatchService.RunAsync`: the background batch thread never had
      `CultureInfo.CurrentUICulture` set, so `LocalizedLabels.ForCurrentUi()`
      (used by `PdfReportGenerator`) and any other `Loc.T(key)` call without
      an explicit language fell back to the OS culture (English/system) rather
      than the operator's choice. Fix: at the start of `RunAsync`, set both
      `CurrentUICulture` and `CurrentCulture` to a culture derived from
      `lang` (e.g. `ro-RO`/`en-US`/`fr-FR`/`es-ES`/`de-DE`). `CurrentUICulture`
      flows through awaits inside the same async state machine, so a single
      assignment covers the whole batch lifetime.
    * `CamComparePdfGenerator`: ~20 labels and footer-legend lines were
      hard-coded in Romanian. Replaced all with `Loc.T("CamCompare*")` keys
      added across 5 languages (header title, "Interpretations" badge,
      "Clinic" label, subtitle, per-card "Interpretation N / Patient /
      Sampling / Interpreted / X parameters · Y abnormalities", summary
      badges (Risen / Fallen / Unchanged / Partial only), table headers
      "Parameter" / "Reference", legend lines 1+2, verified/auto/drift
      LOINC-source markers, footer "Automatically generated by MedicalApp+").
    * `_Layout.cshtml` navbar: `Mod personal` / `Mod clinică` buttons and
      tooltips were hard-coded Romanian. Replaced with `Loc.T("NavMode*")`
      keys (Personal mode / Clinic mode in 5 languages).
  After these fixes, the entire B2B/CAM interpretation pipeline (Gemini
  output + interpretation PDF + compare PDF + navbar UI) follows the
  operator's chosen language end-to-end.



- ✅ **[Feb 2026 — B2C Bug Fix: patient_info.age shows current age instead of PDF age]**
  When a B2C user interprets an old lab PDF tied to a profile with a known
  BirthYear, the generated interpretation PDF was printing the patient's
  CURRENT age (computed today from BirthYear, e.g. 82) instead of the age
  the lab actually printed at sampling time (e.g. "Varsta: 56 ani" written
  by Regina Maria in 2014).
  - Root cause: `InterpretationController:285-287` builds a `PatientContext`
    with `AgeYears = Now.Year - BirthYear`. The original prompt then injected
    that as `"- Age: 82 years\n"` into the patient-context block, with no
    distinction from the lab-printed age. Gemini, seeing an explicit numeric
    hint marked "Age", filled `patient_info.age` with that value, overriding
    the PDF's literal `Varsta: 56 ani`.
  - Fix: in `GeminiMedicalInterpretationService.BuildUserPrompt` the line is
    rewritten to `"- Current age (today, derived from declared birth year):
    {N} years"` with explicit instructions that this is the **current** age,
    is to be used **only** for age-bracketed reference ranges (PSA by age,
    pediatric vs. adult hemoglobin), and that `patient_info.age` MUST be set
    from the PDF — or `null` if the PDF doesn't print one. The same rule is
    reinforced at the top of the OUTPUT FORMAT section of the system prompt
    so the model sees it both before the JSON schema and inside the patient
    context block. Pure prompt-engineering change; no schema/code path
    modified, no DB migration needed.



## Pending / Backlog

- ✅ **[Feb 2026 — B2B/CAM Patient Email translated]** Final hardcoded
  Romanian asset in the CAM flow: `CamPatientEmailBuilder.cs` (the HTML
  email body + subject sent to patients on behalf of the clinic) was
  entirely in Romanian. Refactored to use `Loc.T()` for every visible
  string:
    * Subject — `"Rezultate analize - {Clinic}"` → `Loc.T("CamEmailSubject")`
    * Header eyebrow, greeting, intro paragraph, the 3 attachment-line
      variants (1 / 2 / 3 documents), important-note label + body, auto
      footer disclaimer, footer "Powered by" + tagline.
    * 12 new `CamEmail*` keys × 5 languages = 60 dictionary entries added
      right after the `CamCompare*` block in `Loc.cs`.
  No new public API on `CamPatientEmailBuilder` and no change at the call
  site in `CamBatchService` — the previous fix that sets
  `CultureInfo.CurrentUICulture` once at the start of `RunAsync` means
  plain `Loc.T(key)` inside the builder automatically resolves to the
  clinic operator's chosen language. The interpretation PDF, the
  comparison PDF and the patient email now all follow the SAME language
  end-to-end within a single batch.
  `Loc.cs` total: 881 keys per language (all 5 languages perfectly
  aligned). XSS posture preserved — clinic name / patient name / file
  name are still HTML-encoded before format interpolation.



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

- ✅ **[Feb 2026 — B2C/CAM Bug Fix: First-row-of-table dropped by Gemini Vision]**
  Observed across 4 successive re-interpretations of the same PDF, the LAST
  one silently dropped the FIRST data row ("Numar total de Leucocite",
  LOINC 6690-2). Pattern: the lab printed the first analyte in ALL UPPERCASE
  right under the section title "HEMATOLOGIE", and Gemini Vision absorbed it
  into the header. Other "NUMAR TOTAL DE..." rows that appeared later in the
  page were extracted correctly — confirming it is a positional / boundary
  failure mode, not a structural blind spot.

  Two-layer defense applied (no other code touched):

  **Layer 1 — Prompt prevention (surgical, generic).**
  Added one new bullet inside the existing "EXTRACTION COMPLETENESS — MOST
  IMPORTANT RULE" section of `BuildSystemPrompt()` in
  `GeminiMedicalInterpretationService.cs`. The rule is **completely
  generic**: no specific analyte, no specific lab, no specific language. It
  tells Gemini explicitly that the first row under a section title or
  column-header line is ALWAYS a normal analyte row (never part of the
  header), warns about the uppercase / bold visual confusion, and forces a
  final re-read of the first row under each section before finalizing.
  ~110 tokens — minimal — no impact on attention for the other rules.

  **Layer 2 — Independent completeness audit (telemetry-only).**
  New file `Services/InterpretationCompletenessAuditor.cs` (~100 lines,
  isolated): heuristically counts analyte-like rows in the PDF text layer
  (extracted via existing `PdfTextExtractor` / PdfPig) and compares against
  `result.KeyResults.Count`. When the diff is ≥ 2 rows AND ≥ 10% relative,
  logs a `LogWarning` with the divergence details. **Never modifies the
  result.** Wired into `CallGeminiAsync` after the existing self-audit, with
  a try/catch wrapper that swallows any auditor failure (observational
  layer must NEVER break interpretation).

  Feature flag `Gemini:CompletenessAuditEnabled` in `GeminiSettings`
  (default `true` — safe because Layer 2 only logs). Set to `false` in
  `appsettings.json` for instant rollback of the audit; Layer 1 (prompt) is
  always on as it's a string change.

  Diff stat: +68 lines across 2 existing files, +104 lines in 1 new file.
  Zero deletions. Zero schema changes. Zero DB migrations.


- ✅ **[Feb 2026 — Boundary-row prompt rules round 2: LAST row + post-long-comment]**
  Validation testing surfaced a 2nd boundary-confusion failure mode that
  was distinct from the first: at the BOTTOM of a section, the LAST analyte
  ("Lipide totale", 540.75 mg/dL) was dropped while everything before AND
  after was extracted correctly. Mechanism: the previous row ("Colesterol
  non-HDL") had an extremely long multi-tier reference-range comment
  (pediatric thresholds, CV-risk-very-high / high / moderate targets), and
  immediately after "Lipide totale" came a section divider for "Rata
  filtrarii glomerulare (eGFR)". Gemini Vision absorbed "Lipide totale"
  into either the previous long comment or the next section header.

  Same surgical approach as round 1 (no other code touched):
  - **Rule B — LAST DATA ROW IN A SECTION** (mirror of the existing
    FIRST DATA ROW rule). Forces explicit re-read of the row immediately
    before each section change.
  - **Rule C — ROWS AFTER LONG REFERENCE-RANGE COMMENTS**. Attacks the
    attention-dilution mode where a multi-paragraph comment block visually
    dominates the page and Gemini stops scanning past it.

  Both rules are completely generic — zero specific analyte / lab / language
  mentions. They describe the failure MECHANISM, not the symptom. Added
  17 lines to the existing EXTRACTION COMPLETENESS section. No new files,
  no new flags, no schema/DB changes, zero deletions. Auditor from round 1
  catches this case automatically too (it counts rows regardless of where
  they are positionally). Total prompt overhead with all 3 boundary rules
  (A+B+C) is ~290 tokens — still well within attention budget.



- ✅ **[Feb 2026 — Translation sweep Phase 1: Controller TempData messages]**
  Exhaustive scan across all `.cshtml`/`.cs` files identified 124 hardcoded
  Romanian strings in 25 files. Categorized into 4 phases. **Phase 1
  complete**: TempData / flash / inline error messages from 7 controllers.

  Files touched:
  - `ProfilesController.cs` — 21 strings → 21 `Loc.T(...)` calls
  - `Areas/CAM/Controllers/CheckPdfsController.cs` — 14 strings
  - `Areas/CAM/Controllers/DashboardController.cs` — 8 strings
  - `Areas/CAM/Controllers/BatchController.cs` — 3 strings
  - `Areas/CAM/Controllers/PatientsController.cs` — 2 strings
  - `AdminController.cs` — 1 string
  - `InterpretationController.cs` — 1 string

  Added **43 new keys × 5 languages = 215 entries** in `Loc.cs` with
  semantic prefixes (`Err*`, `Ok*`, `Cam*`). Loc.cs now has **924 keys per
  language**, all 5 languages perfectly aligned.

  Pattern used: `string.Format(Loc.T("Key"), args...)` for parameterized
  strings; `Loc.T("Key")` for plain strings. No language parameter passed
  explicitly — relies on `CultureInfo.CurrentUICulture` already set by
  `RequestLocalizationMiddleware` (B2C path) or by `CamBatchService.RunAsync`
  (CAM batch path, fix from a previous session).

  Diff stat: +291 / -62 lines across 8 files. Zero schema changes, zero new
  files, paranthesis-balanced in all touched files (verified with brace
  parity check). Categories B (intentional RO matching dictionaries in
  `CamPdfMetadataExtractor` + `SamplingDateParser`) deliberately left
  untouched per user's confirmation — those are literal tokens used in
  regex/Contains() to recognize Romanian medical PDFs and translating them
  would break detection.

  Remaining phases:
  - Phase 2: PDF generators (~17 strings)
  - Phase 3: Display services + remaining views (~16 strings)
  - Phase 4: Final cleanup scan



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

## CHANGELOG

### 2026-02 — Codebase translations sweep, Phase 2 & 3 (services + mascot + admin health widget)
- `LoincClassDisplay.cs`: 28 hardcoded RO labels → `Loc.T()` (Compare-view group headers now follow UI culture).
- `CamBatchSumarPdfGenerator.cs`: Full PDF localized (title, KPI cards, error table, footer) — 19 keys × 5 langs.
- `CamBatchSumarWriter.cs`: The `.txt` sibling localized symmetrically (5 extra keys for stats/notSends/status/tries).
- `EmailDeliverabilityChecker.cs`: All 6 user-facing FriendlyMessage strings now via `Loc.T()`.
- `_DoctorMascot.cshtml`: Sound toggle `title` + `aria-label` localized.
- `Views/Admin/Index.cshtml`: Daily-summary button tooltip + the entire LOINC health widget (badge labels, refresh tooltip, status states, "checked" timestamp, "LOINC codes" unit) localized — inline JS reads a `<script type="application/json">` blob.
- `CamBatchService.cs`: Hardcoded ".reasons.txt" header ("Acest fișier a eșuat de 3 ori…") moved to `Loc.T("CamBatchFailedThreeTimesHeader")`.
- Total: 73 new keys added to all 5 languages (EN/RO/FR/ES/DE) = **365 new translation entries**.

### 2026-02 — Polish: localized HTML5 file-required popup + smart language auto-detect
- **`UploadFilePleaseSelect`** key added in 5 langs. Wired via `setCustomValidity()` in both upload forms (`Views/Interpretation/Upload.cshtml` B2C single-file + `Areas/CAM/Views/CheckPdfs/Index.cshtml` B2B multi-file). The native English "Please select a file." popup is now replaced with the user's language.
- **Smart language auto-detect** added to `Views/Shared/_Layout.cshtml`. On the very first request the browser does, ASP.NET Core's existing `AcceptLanguageHeaderRequestCultureProvider` already picks the visitor's language. The new helper:
    1. Reads the `.AspNetCore.Culture` cookie. If missing AND `navigator.language` matches a different supported lang than the one rendered → writes the cookie + reloads (handles the edge case where `Accept-Language` is suppressed by privacy extensions).
    2. If missing but the rendered lang already matches → just persists the cookie (makes the choice sticky for future visits & the dropdown reflects the active choice).
    3. Uses `sessionStorage.langAutoChecked` as a one-shot guard against reload loops.
    4. Wrapped in `try/catch` — never breaks the page on a localization helper.
- Tested by: User in VS2026 (not yet — pending local pull & rebuild).
- Status: Phase 2 & 3 + Account pages + i18n polish = ✅ COMPLETE.

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

### 2026-06 — Plan de Afaceri POCIDIF rescris integral GREENFIELD (doar PARTEA A II-A)
- User a cerut reconstrucția completă: proiect prezentat 100% de la zero, DOAR Partea a II-a (Descrierea proiectului) conform Anexei 4, cu subcapitole noi și C.4 cu TOATE etapele/fazele (inclusiv landing page, înregistrare, autentificare etc.), datele din draftul anterior (FIXMEDICAL S.R.L., 800.000 EUR, 18 luni, POCIDIF), inovația evidențiată.
- Generatorul a fost modularizat: `/app/bp/` (helpers, cover, c1, c2, c3, c4_arhitectura, c4_etape, c4_specificatii, c4_ecrane, c4_dictionar, c5, c6, c7, c8, c9, glosar) + `/app/generate_business_plan.py` (asamblor).
- Structură: C.1 (5 subcap) · C.2 (6 subcap, inovație) · C.3 (6 subcap, personas) · C.4 (12 subcap; C.4.4 = 11 etape × ~30 faze cu livrabile+criterii de acceptanță) · C.5–C.9 + Glosar. 30 tabele, ~98.500 caractere, estimare 40–48 pagini.
- Narativă verificată: zero referințe la prototip existent/refactoring/migrare; totul la timp viitor.
- Output: `/app/Plan_Afaceri_MyMedicalApp_FIXMEDICAL.docx`, copiat în `/app/frontend/public/` (accesibil la [PREVIEW_URL]/Plan_Afaceri_MyMedicalApp_FIXMEDICAL.docx — verificat 200 OK).
- ATENȚIE fork nou: scriptul vechi monolitic a fost șters de `git reset --hard github/main` (commit-uri locale nepush-ate); a fost recuperat din reflog (a2909c5) apoi înlocuit cu varianta modulară. Regula git fetch+reset rămâne, dar verifică întâi reflog dacă lipsesc fișiere.
- Pending user: feedback pe conținut; posibile extinderi spre 60+ pagini (mai mult detaliu per fază, context de piață, capturi descrise).

### 2026-06 — Fix P1: robustețea matcher-ului LOINC (cazul Hemoglobina 718-7 vs 16931-8)
- RCA confirmat matematic (scor simulat 0.831 ≈ 82% afișat): Gemini a emis sufixul stocastic "by Automated count" → ancora exact-match a ratat → semantic (MiniLM favoriza sufixul comun) + regula de metodă (impedanta/citometrie penaliza 718-7 fără metodă) au ales 16931-8 (Hct/Hgb Ratio).
- Implementat în `loinc_service/`:
  1. `canonical_anchors.py`: `strip_method_suffix()` + `lookup_anchor_stripped()` — ancoră second-chance după tăierea sufixului " by <method>" (doar după ratarea exact-match).
  2. `loinc_store.py`: `name_index` + `get_by_name()` — lookup determinist pe LONG_COMMON_NAME exact.
  3. `pipeline.py`: `_deterministic_lookup()` cu 3 straturi (ancoră exactă → nume LOINC exact → ancoră cu sufix tăiat), fiecare validat de garduri: `_method_contradicts` (doar metode EXPLICITE contradictorii resping; candidații fără metodă nu sunt respinși), `_raw_name_contradicts` (anti-halucinație asimetric: respinge doar cu dovadă pozitivă că numele brut aparține ALTUI analit ancorat, sim≥0.85 + gap≥0.20; "VSH" românesc nu declanșează), `_unit_contradicts_property` (g/dL nu poate fi Ratio/Fraction).
  4. `_apply_rules`: credit parțial 0.5 pentru candidați fără metodă (Fix 3); penalizare ×0.25 în bucla semantică pentru contradicție unitate↔Ratio (Fix 2).
- `test_pipeline_smoke.py` extins: suită GOLDEN cu 12 cazuri istorice (Hgb+sufix, Hct Estimated/Automated, FT3 pmol/L swap, MCH/MCHC, LDL by calculation, VSH raw, anti-halucinație) + 19 legacy. Rezultat: 31/31 PASS, zero regresii.
- Validare finală pe mașina userului (dicționar complet 97k + SQL Server): PENDING.

### 2026-06 — Etapa 1 „coduri corecte și unitare": canonicalizare ancore + garduri semantice
- RCA pe emisii reale Gemini (din JSON-ul debug atașat pe email): prepoziția „in/of" + parafraze („antigen" vs „Ag", „.total", fără specimen/metodă) ocoleau toate straturile deterministe → semantic alegea coduri diferite per interpretare (Hct 48703-3 vs 4544-3, MPV 28542-9 vs 32623-1, Fibrinogen 48664-7 vs 3255-7, PSA 83112-3 vs 2857-1, HbA1c 71875-9/4546-8 vs 4548-4).
- **BUG CONFIRMAT în ancore (suspiciunea userului)**: „Urea [Mass/volume]..." → 22664-7 (cod MOLES!) — corectat la 3091-6; adăugată cheia [Moles/volume] → 22664-7.
- Implementat în `loinc_service/`:
  1. `canonical_anchors.py`: `canon_key()` (lowercase + „ of "≡„ in " + antigen→ag/antibody→ab), `_build_lookup()` cu detecție de coliziuni (raise la startup) + aliasuri automate de bază fără sufix „by <method>" (aliasurile ambigue se anulează; cheile explicite câștigă — ESR rămâne 30341-2 pe bază). 162 chei totale.
  2. Ancore noi: Fibrinogen→3255-7 (2 variante), PSA.total→2857-1, MPV fără specimen→32623-1, HbA1c→4548-4 (2 variante drift).
  3. `loinc_store.py`: `name_index` construit cu `canon_key` (layer 2 tolerant la prepoziții).
  4. `pipeline.py`: penalizare ×0.5 în stratul semantic pentru metodă EXPLICIT contradictorie cu keywords din PDF (methodless neafectați); `_method_contradicts` cu param `quiet`; seturile „coagulometrie/coagulometric/coagulometria" extinse cu token „coagulation" (3255-7 „Coagulation assay" nu mai era considerat contradictoriu).
- `test_pipeline_smoke.py`: +12 cazuri golden din emisiile reale (total 43 teste: 19 legacy + 24 golden) → 43/43 PASS.
- C#: `CamBatchService` atașează JSON-ul debug (`<fisier>_Gemini_LOINC.json`) la emailul de interpretare sub flag `CamBatch:AttachDebugJson` (true doar în Development).
- Etapa 2 (RELMA-like axis parsing + SHORTNAME în fuzzy) și Etapa 3 (echivalență la Comparare + sticky mapping per pacient în C#) — APROBATE de user, de implementat.
- Validare pe mașina userului (dicționar 97k): PENDING (re-interpretare fișiere + Comparație).

### 2026-06 — BUG CRITIC rezolvat: axele LOINC lipseau din seed → UNIT-SWAP mort pe mașina userului
- Simptom: Colesterol HDL în mmol/L primea 2085-9 (Mass/volume) în loc de 14646-4 (Moles/volume); Compararea amesteca 1.27 mmol/L cu 44.3 mg/dL pe același rând.
- RCA: tabela SQL `LoincDictionary` (model C# `LoincEntry`) NU are coloanele Component/Property/System/Method → `seed_embeddings.py` (tolerant) a produs metadata cu axe None pentru toate cele 97.314 intrări → `_property_family(None)`=None → UNIT-SWAP, peer-search și garda anti-halucinație erau dezactivate silențios pe mașina userului (funcționau doar în sandbox, unde eșantionul avea axe).
- Fix elegant (fără migrații SQL, fără re-seed): `parse_loinc_axes()` în `loinc_store.py` — derivă axele din gramatica numelui lung (`Component [Property] in|of System by Method`), aplicat ca enrichment la `STORE.load()` doar pe câmpurile lipsă.
- Alte fix-uri în aceeași sesiune: LoincHealthMonitor — ProbeTimeoutMs 800→3000, FailuresBeforeRestart 2→3, gate #4 „second opinion" (probe de confirmare 8s înainte de spawn; elimină duplicatele care mureau cu Errno 10048 când serviciul era ocupat de batch), titlu fereastră „LOINC Service (auto-started)", log explicit când Enabled=false.
- Test: `test_pipeline_smoke.py` rulează acum în DUAL MODE (seed complet + seed sărac simulat) — 45 cazuri × 2 = 90/90 PASS, incl. HDL mmol/L→14646-4 și mg/dL→2085-9.
- Pending: validare pe mașina userului; sugestie viitoare: conversie de unități în Comparare (mmol/L→mg/dL) pentru a alinia istoric coloanele HDL vechi; pre-flight check LOINC la start interpretare/lot cu avertisment (aprobat de user ca idee, neimplementat).

### 2026-06 — Etapa 2 RELMA implementată: potrivire axă-cu-axă
- `pipeline.py`: emisia Gemini e descompusă cu `parse_loinc_axes()` (același parser ca enrichment-ul dicționarului) în Component/Property/System/Method; scor pe axe: 0.50·comp (fuzzy, incl. SHORTNAME, dot-normalizat) + 0.20·prop (familii: MCnc/SCnc/VFr/MFr/EntVol/NCnc/CCnc/ACnc/Presence/Ratio...) + 0.15·system (canonicalizare + grupuri coarse: Ser≈Ser/Plas≈PPP 0.75) + 0.15·method (grupuri de echivalență: automated≈impedance, coagulation≈clauss, IA≈chemiluminescence...; absența = 0.5 neutru).
- Blend: `final = AXIS_WEIGHT·axis + (1-AXIS_WEIGHT)·(0.60·sem+0.25·fuzzy+0.15·rules)`; gate: doar dacă emisia e parsabilă (component + încă o axă). Toate gardurile/penalizările existente rămân.
- KILL SWITCH: `LOINC_AXIS_WEIGHT=0` (env var, config.py, default 0.45) — dezactivare instant fără rollback.
- Teste: 90/90 PASS (45 × seed complet + 45 × seed sărac). Demo: deriva inedită „Hemoglobin level [Mass/volume]..." → 718-7 cu scor 0.84 (vs 0.76 fără axe); erorile inter-axe (16931-8) imposibile structural (prop Ratio ≠ Mass = 0 pe axa property).
- Validare pe mașina userului: PENDING.

### 2026-06 — „Verdict pe axe" implementat (explicabilitatea deciziei LOINC)
- `pipeline.py`: `MatchResult.axis_verdict` (dict de stringuri) construit de `_build_axis_verdict()` pe toate cele 3 drumuri: determinist (cu eticheta stratului: „ancoră exactă" / „nume LOINC exact" / „ancoră după tăierea sufixului"), semantic (cu ponderea axelor) și unit-swap (notă explicită „X [MCnc] → Y [SCnc] — unitatea cere...").
- Format per axă: „query ↔ candidat = similaritate" + axis_score agregat.
- `main.py`: `LoincResponse.axis_verdict`; C#: `LoincMatcherClient.MatcherResponse.AxisVerdict` → `KeyResult.LoincAxisVerdict` (`loinc_axis_verdict` în JSON) → ajunge automat în RawJsonResult și în atașamentul debug de pe email.
- Teste: 90/90 PASS (dual-mode); demo verificat pe cele 3 drumuri.
- PENDING de la user: „mai sunt câteva analize de același fel care nu au LOINC identic" — de diagnosticat cu verdictul pe axe când userul trimite JSON-urile noi.

### 2026-06 — Anomalii VSH + Procente de protrombină rezolvate (diagnoză via Verdict pe axe)
- VSH: emisiile „...in Blood" / „...by Microphotometric method" se unifică pe 30341-2 (umbrelă generică, politica RO; Westergren explicit rămâne 4537-7). Ancoră nouă + base-alias.
- Procente de protrombină: 3 emisii diferite (Units/volume în Coagulation plasma / PPP, Ratio) unificate pe 3289-6 (confirmat loinc.org: Prothrombin activity actual/normal, %, PPP, Coag). 4 ancore noi.
- **BUG component descoperit din verdict**: „Prothrombin ↔ Prothrombin Ab = 1.00" (token_set subset!) → guard Ab/Ag: `_ab_ag_mismatch()` token-based (folosind canon_key: antibody→ab, antigen→ag) aplicat în `_hard_reject_penalty` (×0.30) și în `_axis_component_sim` (cap 0.30). Testele Ab genuine (Thyroglobulin Ab, Prothrombin Ab exact-name) rămân neafectate.
- Axa metodei cu context-fallback: când Gemini omite metoda dar PDF-ul o confirmă (`_METHOD_KEYWORDS` în source context), candidatul cu metoda potrivită urcă la 1.0 (DOAR upgrade, methodless rămâne 0.5 neutru — nu recreează bugul 718-7).
- Chei noi: „fotometric/photometric" în _METHOD_KEYWORDS + grup axis („spectrofotometrie" NU conține substring-ul „fotometric" — verificat); sisteme noi: „Coagulation plasma"/„citrated plasma" → grup PPP.
- Teste: 53 cazuri × 2 moduri = 106/106 PASS. Legacy ESR actualizat la 30341-2 (4537-7 era artefact de eșantion).
- Clarificare pt user: unitățile NU erau cauza (mm/h, % compatibile cu ambele coduri) — cauzele: fragmentare pe metode + subset-match pe component.

### 2026-06 — INR rezolvat prin 2 corecții GENERALE (cerința userului: fără fix-uri particulare)
- Diagnoză (via Verdict pe axe): emisia expandată „International normalized ratio (INR)..." scotea 6301-6 din top-25 semantic (MiniLM nu echivalează abrevierea) → câștiga 3200-3 (factor VII, comp 0.44).
- Fix general #1 — `_PHRASE_SYNONYMS` + `apply_phrase_synonyms()` în canonical_anchors: echivalențe abreviere↔formă expandată (INR, aPTT, TSH→thyrotropin) + dedup „x (x)"→„x"; aplicate în canon_key (ancore + name_index + query determinist), pe query-ul fuzzy și pe componenta din axis layer.
- Fix general #2 — injecție lexicală în bazinul de candidați: `rf_process.extract` (token_set_ratio, limit 10, cutoff 70) peste `STORE.names_norm` (listă nouă în load()); union cu top-K semantic — codul corect primește mereu „un loc la masă" chiar când embedding-ul ratează.
- Teste: 56 cazuri × 2 moduri = 112/112 PASS (3 cazuri noi INR; ancora existentă 6301-6 acum accesibilă via base-alias + sinonime).
- URMEAZĂ (aprobat de user): Sticky Mapping (C#: tabelă LoincMappingCache per clinică, amprentă = nume brut + UM + interval referință; supapă: ancorele corectează cache-ul semantic).

### 2026-06 — Marketing în PDF-ul Demo: CTA „deblocare GRATUITĂ" (cerere user)
- `PdfReportGenerator.cs`: sub bannerul cu lacătul, linie nouă „ATENȚIE! (roșu) - Cumpără orice pachet de credite pentru a debloca GRATUIT (roșu) raportul complet."
- CTA final rescris: „Vrei raportul complet?" + „Chiar și cel mai mic pachet deblochează GRATUIT (roșu) toate secțiunile acestui raport!"
- 3 butoane portocalii CLICABILE (hyperlink → https://www.mymedicalapp.net/Credits/Buy): sus (după banner), mijloc (după tabelul de rezultate), jos (în blocul CTA). Doar în modul freemium.
- Helper nou `AppendHighlighted()` colorează în roșu-bold orice apariție a cuvântului „gratuit" localizat; `UnlockButton()` randează butonul.
- Chei noi Loc.cs × 7 limbi: PdfFreemiumAttentionWord, PdfFreemiumAttentionLine, PdfFreemiumFreeWord, PdfFreemiumUnlockButton (+ PdfFreemiumCtaBody rescris).
- Validare: `dotnet build` 0 erori / 0 warning-uri + PDF real generat și inspectat vizual pe 3 pagini; 3 hyperlink-uri confirmate în binarul PDF.
- OBSERVAȚIE de raportat userului: emoji-ul 🔒 din titlul bannerului se randează ca pătrat gol (font fallback lipsă) — de decis dacă îl înlocuim cu text.
- RĂMÂNE OPȚIONAL (varianta b discutată): pagină nouă în app care afișează raportul Demo pe ecran, cu butoane CTA. Momentan raportul se livrează DOAR pe email.
- NEXT (aprobat anterior): Sticky Mapping (LoincMappingCache).
- UPDATE link CTA: URL-ul butoanelor NU mai e hardcodat. `appsettings.json` → `"PdfCta": { "BuyCreditsUrl": "https://localhost:5001/Account/Dashboard" }`, citit în Program.cs în `PdfReportGenerator.BuyCreditsUrl`. La hosting se schimbă doar această linie din appsettings (fără rebuild de cod). Verificat: 3 hyperlink-uri = localhost:5001/Account/Dashboard.

### 2026-06 — Deblocare automată + anunțare user la PRIMA cumpărare (A + B, aprobat de user)
- Link CTA din PDF setat pe `https://localhost:5001/Credits/Buy` (appsettings.json → PdfCta:BuyCreditsUrl).
- CONSTATARE: deblocarea era DEJA automată — `ProfilesController.TryRegenerateReportPdfAsync` folosește `isFreemium = (user.Credite == 0)`, deci după cumpărare orice raport re-descărcat din Arhivă iese complet. Nu s-a scris logică de „deblocare", doar de LIVRARE + ANUNȚARE.
- (A) `CreditsController.Checkout` POST: `isFirstPurchase` = `!Purchases.Any(p => p.UserEmail == ...)` evaluat ÎNAINTE de inserarea Purchase. Dacă e prima cumpărare ȘI userul NU e Clinic → `TrySendUnlockedDemoReportAsync(user)`: ia cea mai recentă interpretare (Status=success, RawJsonResult≠null), regenerează PDF-ul cu `isFreemium:false` în limba raportului (swap temporar de CurrentUICulture, ca în CamBatchService) și îl trimite pe email ca atașament. Dacă există mai multe rapoarte demo → doar cel recent atașat + nota „ai încă {0} rapoarte — vezi Arhiva" cu URL absolut (Url.Action + Request.Scheme). Try/catch total: eșecul emailului NU afectează plata.
- (B) Banner verde pe `Views/Account/Dashboard.cshtml` (data-testid: demo-unlocked-banner / demo-unlocked-download-btn) cu buton „Descarcă raportul complet" → Profiles/DownloadReport?id=... TempData transmis ca STRING (numerele nu sunt type-stable prin serializatorul TempData).
- CAM/Clinic: exclus explicit (nu există interpretare Demo la CAM).
- Chei noi Loc.cs × 7 limbi: CreditsDemoUnlockedEmailSubject/EmailIntro/EmailArchiveFmt/BannerTitle/BannerBody/BannerOthersFmt/BannerButton.
- Validare: `dotnet build` 0 erori / 0 warning-uri. E2E NEtestat local (necesită SQL Server + SMTP de pe mașina userului) → de testat de user: cumpărare primul pachet cu un cont care are cel puțin un raport Demo.

### 2026-06 — Ecran „Raportul tău" în aplicație (funnel in-app) — A+B+sticky, aprobat de user
- NOU `Views/Profiles/ViewReport.cshtml` + acțiune `ProfilesController.ViewReport(int id)` + `Models/ReportScreenViewModel.cs`.
- SECURITATE: redactarea se face SERVER-SIDE (textul blocat nu ajunge deloc în HTML) — fără CSS blur, deci nu poate fi citit cu F12/View Source. Pattern-ul de redactare e unul singur pentru PDF și ecran: `PdfReportGenerator.IsRedactedAt(index)` (expus public din BlurAt, `index % 5 is 1 or 2 or 4` = ~60%).
- 4 CTA: sus (sub bannerul ATENȚIE), mijloc (după tabelul de rezultate), jos (blocul verde) + BARĂ STICKY permanentă jos cu „{N} elemente din acest raport sunt încă blocate" → toate spre `/Credits/Buy`.
- Redirect după interpretare: freemium (`user.Credite == 0`) → `Profiles/ViewReport`; plătitor → Dashboard (neschimbat). Jingle-ul mascotei mutat să funcționeze pe ambele destinații. `SaveHistory` returnează acum `int` (Id) pentru redirect.
- Arhivă (`Views/Profiles/History.cshtml`): buton nou „Vezi pe ecran" afișat DOAR pentru freemium (`ProfileHistoryViewModel.IsFreemium`). Plătitorii merg direct la PDF (și `ViewReport` îi redirectează la `DownloadReport` dacă au credite).
- Chei noi Loc.cs × 7 limbi: ScreenReportTitle, ScreenReportEmailedNote, ScreenReportLockedSectionsFmt, ScreenReportBackToHistory, ScreenReportViewOnScreenBtn (restul refolosite din PdfFreemium*).
- Validare: `dotnet build` 0 erori/0 warning-uri + pagina randată REAL într-un host de probă (/tmp/webprobe, ProjectReference la MedicalApp + reflection pe BuildReportScreen) și inspectată pe 3 screenshot-uri; corectate 2 probleme de layout (diacritice la titluri, marker-ul de listă la întrebările blocate).
- data-testid: report-screen-heading, report-screen-demo-badge, report-cta-top/middle/bottom/sticky, report-screen-results-table, report-screen-row-locked, report-sticky-bar, btn-view-report-{id}.

### 2026-06 — Ecranul Demo „bibilit" (7 cerințe user, toate livrate)
1. Paritate cu raportul de interpretare: ecranul refolosește paleta, săgețile și structura PDF-ului (StatusArrow → #c62828 high / #1565c0 low / #f9a825 borderline / #2e7d32 normal).
2. Header de brand identic PDF-ului: „MyMedicalApp.NET" + Loc.T("BrandSubtitle") + www.mymedicalapp.net + linie albastră.
3. Valoarea separată de intervalul de referință: `.col-value{padding-right:2.25rem}` + `.col-ref{padding-left:2.25rem}` (0.9rem pe mobil).
4. Valoarea colorată: roșu peste interval, albastru sub, galben borderline, verde normal.
5. `AnalyteLineRaw` (line-context: „-Ser - Metoda fotometrica (Cobas c501)") afișat italic sub numele analitei.
6. Linie LOINC: cod + Long Common Name + punct colorat (anchor=verde/semantic=albastru, tooltip din LoincSourceBadge) + procent pentru semantic; legendă sub tabel.
7. Extra „ochios": pagina arată ca un document (card alb cu umbră pe fundal gradient), watermark DEMO diagonal în CSS, card de progres „Vezi 3 din 6 rezultate" + bară verde, hover pe rânduri, antete de panel multi-nivel (split pe " | "), culori pe severitate la findings, footer cu tagline + URL.
- Cheie nouă Loc.cs × 7 limbi: ScreenReportVisibleResultsFmt. VM extins: AnalyteLine, LoincCode/LongName/Source/Score, VisibleResultsCount, TotalResultsCount, HasLoincCodes.
- Validare: aplicația a rulat în host-ul de probă (/tmp/webprobe) și pagina a fost inspectată pe 3 screenshot-uri cu date LOINC reale (718-7, 13457-7, 2498-4).
- ATENȚIE fork viitor: SDK-ul .NET nu persistă în container (se șterge /opt și /tmp la restart) → reinstalare cu `curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 9.0 --install-dir /opt/dotnet` (~2 min) înainte de build/probe.
- FIX layout tabel (feedback user, foto): `table-layout: fixed` + `<colgroup>` 54% / 14% / 19% / 13% → descrierea analizei ocupă tot spațiul orizontal, iar Valoare/Interval/Status nu mai pot fi strivite sau împinse în afara ecranului. `vertical-align: top` pe celule (valoarea stă pe aceeași linie cu numele analitei, nu centrată vertical într-o celulă înaltă). Status redesenat ca `.status-chip` — cerc colorat 1.9rem cu ✓ verde / ↑ roșu / ↓ albastru / ≈ galben. Pe mobil tabelul are `min-width:34rem` + scroll orizontal.
- FIX 2 (feedback user): intervalul de referință poate fi descriptiv („100-125: Glicemie bazala modificata") → s-a scos `white-space:nowrap` de pe `.col-ref` + `overflow-wrap:anywhere`, lățimi noi 50/13/24/13. Numele analizei mărit (1.05rem, bold) și colorat după status (roșu peste / albastru sub / galben limită / verde normal) prin `.param-name`.
- SCHIMBARE DE DECIZIE (user): „Vezi pe ecran" apare acum la TOATE fișierele din arhivă, pentru orice user. `ViewReport` NU mai redirectează plătitorii la PDF — randează raportul COMPLET pe ecran: `isFreemium = (Credite == 0)` intră în `BuildReportScreen(..., isFreemium)` și toate redactările sunt `isFreemium && IsRedactedAt(i)`. Pentru plătitori sunt ascunse: badge DEMO, watermark, banner ATENȚIE, cardul de progres, cele 3 CTA și bara sticky; apare butonul „Descarcă PDF" (există pentru ambele moduri, sus lângă „Înapoi la arhivă").
- Validare: ambele moduri randate real și inspectate (DEMO + ?full=true): 0 CTA și 0 bară sticky în modul plătit, verificat prin selectori.

### 2026-06 — MODUL NOU: „Dosar medical" (B2C) — livrat
Concept (idee user): din toate buletinele din arhiva unui profil se extrag TOATE analizele în afara intervalului (high/low/borderline) + rezultatele POZITIVE, grupate pe domeniu medical, fiecare analiză cu propriul istoric în timp. Este foaia pe care pacientul o duce la medic.
- Buton nou „📋 Dosar medical" în `Views/Profiles/History.cshtml`, lângă Grafice și Combinații (activ chiar și cu o singură interpretare).
- `ProfilesController.Dossier(profileId)` + `DossierExport` (download/email, ca la Compare) + `BuildDossierAsync` → `static BuildDossier(profile, histories)` (pur, testabil fără DB).
- Structură: header brand → Informații pacient (nume/vârstă din BirthYear/sex) → Istoric medical (din `Profile.Notes`) → sumar („7 analize în afara intervalului, din 4 buletine, între ... și ...") → grupuri pe clasă LOINC (`LoincClassDisplay`, prioritate Hematologie→Coagulare→Biochimie→Endocrino→Serologie→...) → card per analiză (nume + LOINC + Long Common Name) → tabel istoric (Data recoltării / Laborator / Valoare / Interval / Status / Evoluție).
- Detalii implementate: dedup pe (analit|zi|valoare) pentru buletine încărcate de 2 ori; data recoltării cu fallback pe data interpretării (marcat vizual); trend ↑/↓/= între intrări consecutive (ParseNumeric); `ClassifyAbnormal` prinde și textul pozitiv (pozitiv/reactiv/detectabil/prezent) DAR verifică negațiile ÎNTÂI (nereactiv/negativ/nedetectabil); `InferClassFromPanel` plasează analizele fără cod LOINC în domeniul lor citind PanelHeaderRaw (ex. „Indice HOMA" → Biochimie).
- `Services/MedicalDossierPdfGenerator.cs` (A4 portret, QuestPDF) + înregistrat în Program.cs. Export download + email (nu consumă credit suplimentar).
- Billing: `_archiveAccess.TryConsume(user, "dossier")` — identic Grafice/Combinații.
- Empty state: mesaj verde „Felicitări! Toate analizele tale sunt în intervalul normal".
- 24 chei noi Loc.cs × 7 limbi (Dossier*).
- Validare: build 0 erori/0 warning-uri; logica de agregare rulată REAL prin reflection pe `BuildDossier` cu 4 buletine sintetice (inclusiv un duplicat, un „Negativ" și un „POZITIV") → 7 analize / 4 grupuri corecte, dedup OK, trend OK; pagina inspectată pe 3 screenshot-uri; PDF generat (144 KB, 1 pagină) și inspectat vizual.

### 2026-08-24 23:48 — PUNCT DE ÎNTOARCERE declarat de user
Userul a cerut explicit să reținem acest moment ca checkpoint de rollback înainte de lucrările de performanță. Tot ce e livrat până aici (Demo PDF cu CTA, deblocare la prima cumpărare, ecran „Raportul tău", Dosar medical) este testat și acceptat de user.

### 2026-08 — AUDIT PERFORMANȚĂ + Pasul 1 și 2 (instrumentare + LOINC batch)
CONTEXT: user raporta interpretări de 2-4 minute + coduri LOINC diferite la aceeași analiză. A propus Med-Gemini; am verificat prin web search și am arătat că NU e o soluție (Med-Gemini nu are API; MedLM oprit în sept 2025; MedGemma e model open care cere GPU self-hosted, slab pe multilingv, mai LENT). User a acceptat.
AUDIT (cauze găsite în cod):
- Prompt de sistem = 44.730 caractere ≈ 11.000 tokeni, trimis la fiecare apel, amestecând 5 sarcini diferite.
- `MaxOutputTokens: 65000` + instrucțiunea „DETAILED (not a short one)" → 4.000-9.000 tokeni de ieșire = 60-120 s (latența LLM vine din generare, nu din citire).
- Retry-uri: transient 5/15/30/60 s; audit/JSON eșuat → RE-interpretare completă (până la 3 apeluri) → cazurile de 4 minute.
- `LoincMatcherClient.MatchAllAsync`: buclă SECVENȚIALĂ, 1 HTTP POST per analiză → 30-40 round-trips = 15-120 s.
- Zero instrumentare (niciun Stopwatch) → nimeni nu știa unde se duce timpul.
LIVRAT ACUM:
1. `Services/StageTimer.cs` — cronometru per etapă (acumulează la retry), serializat JSON.
2. `InterpretationHistory`: coloane noi `DurationMs` (int?) + `StageTimingsJson` (string, 1000). Migrație EF: `20260824205604_AddInterpretationStageTimings` (atinge DOAR aceste 2 coloane). **USERUL TREBUIE SĂ RULEZE Update-Database.**
3. `InterpretationController`: cronometrare pe pdf_extract, ai_calls (+ai_attempts), loinc_match, pdf_report, email; log „Interpretation TIMING ..." + salvare pe rândul de istoric.
4. `loinc_service/main.py`: endpoint NOU `POST /loinc/match-batch` — tot buletinul într-un singur apel, ThreadPoolExecutor (max 8 workers), rezultate aliniate pozițional, `null` pentru nepotriviri. Testat cu FastAPI TestClient (3 in → 3 out, 200 OK).
5. `LoincMatcherClient`: calea rapidă folosește batch-ul; helper `ApplyMatch` partajat; FALLBACK automat pe bucla veche dacă endpoint-ul lipsește (404/405) → versiune veche de Python = doar mai lent, nu rupt. **USERUL TREBUIE SĂ REPORNEASCĂ serviciul Python** ca să încarce endpoint-ul nou.
6. Prompt Gemini rescris (decizia userului): explicații de 1-2 propoziții la analizele NORMALE, minimum 3 propoziții + cele 4 puncte doar la high/low/borderline/pozitiv. Estimare: -40% tokeni de ieșire.
7. Admin: acțiune nouă `AdminController.Performance(take=20)` + `Views/Admin/Performance.cshtml` + `Models/InterpretationPerformanceViewModel.cs` + buton „Performanță" în Admin Index. Tabel cu timpi per etapă, medii, heat-map (roșu >20s, galben >5s), badge „N apeluri" când au fost retry-uri AI.
VALIDARE: build 0 erori/0 warning-uri; endpoint batch testat real; panoul Admin randat real și inspectat (screenshot).
URMEAZĂ (aprobat conceptual): Pasul 3 Sticky Mapping (cache determinist LOINC per profil) + Pasul 4 împărțirea apelului Gemini în EXTRACTOR (temperatură 0, doar tabel) + INTERPRETOR (proză), cu LOINC matching rulat în PARALEL cu proza, totul în spatele unui feature flag.

### 2026-08-25 — Soluția 1: thinkingConfig ca buton reglabil (nu decizie irevocabilă)
DATE REALE de la user (4 interpretări, după pasul 1+2): total mediu 110,9 s din care Gemini 102,4 s (92%), LOINC 5,7 s (5% — reparat, era estimat 15-120 s), extracție 955 ms, PDF 292 ms, email 1,5 s. Tokeni de ieșire 10.220 / 14.599 / 18.168 / 19.972 → timp 81,7 / 88,7 / 107,5 / 131,9 s. RELAȚIE LINIARĂ: ~150 tokeni/secundă. Zero retry-uri. Concluzie: latența = volum de ieșire, nimic altceva.
CAUZĂ NOUĂ GĂSITĂ: codul NU trimitea `thinkingConfig`, deci gemini-2.5-flash rula cu thinking dinamic implicit, iar tokenii de gândire se generează ca output = timp.
LIVRAT:
- `GeminiSettings.ThinkingBudget` (int, default -1). `-1` = cheia NU se trimite (comportament identic celui anterior, regresie zero); `0` = thinking oprit; `256..24576` = plafonat.
- `BuildRequestBody` trimite `thinkingConfig` doar când budget >= 0. VERIFICAT prin probe cu reflection: -1 → fără cheie, 0 → `"thinkingConfig":{"thinkingBudget":0}`, 1024 → idem cu 1024.
- Se citește `usageMetadata.thoughtsTokenCount` → log dedicat + salvat în StageTimingsJson ca `ai_thinking_tokens` → coloană nouă „din care thinking" în Admin → Performanță (roșu peste 2000).
- `appsettings.json`: `"ThinkingBudget": 0` (userul poate reveni instant punând -1, doar restart, fără rebuild de cod).
- Fix: `OtherMs` din panoul Admin exclude contoarele (`ai_attempts`, `ai_thinking_tokens`) ca să nu le scadă ca milisecunde.
ATENȚIE calitate (comunicat userului): thinking-ul contează la CORELAȚII (raționament între analize) și la completitudinea JSON pe buletine lungi. Protocol de test dat: același PDF cu -1 vs 0, comparat pe timp + număr de analize extrase + calitatea secțiunii „Corelații posibile". Dacă scade calitatea → 1024 sau 2048 ca variantă de mijloc.
NU necesită migrație (nicio schimbare de schemă).
URMEAZĂ: Soluția 2 (limite dure pe lungimea explicațiilor — user vrea pas cu pas, după validarea Soluției 1), apoi Soluția 3 (împărțire în apeluri paralele: extractor + loturi de explicații + narativ), apoi plafon MaxOutputTokens 24.000 + procesare în fundal (risc de timeout la 100-120 s pe proxy în producție).

### 2026-08-25 — CORECȚIE DE PLAN (observație justă a userului)
Userul a semnalat că thinking-ul este necesar la: 1. Factori de risc, 2. Corelații posibile, 3. Recomandări, 4. Întrebări pentru medic, 5. Completitudine. CORECT — iar `thinkingBudget` se aplică PE APEL, nu pe secțiune. Deci într-un apel monolitic nu poți avea 0 pentru tabel și buget generos pentru raționament.
DECIZII:
- `appsettings.json` → `"ThinkingBudget": -1` (comportament neschimbat, ZERO risc de calitate). Măsurarea `thoughtsTokenCount` funcționează și pe -1, deci aflăm cât thinking se consumă fără a sacrifica nimic.
- Ordinea soluțiilor se INVERSEAZĂ: Soluția 2 (limite dure de lungime DOAR pe explicațiile per analiză) devine următorul pas, fiind singura care taie timp fără să atingă niciuna din cele 5 secțiuni de raționament.
- Soluția 3 (împărțirea apelului) capătă o cerință suplimentară de arhitectură: apelul EXTRACTOR + apelurile de EXPLICAȚII rulează cu `thinkingBudget: 0`, iar apelul NARATIV (factori de risc, corelații, recomandări, întrebări) + auditul de completitudine rulează cu buget GENEROS (4096+), eventual pe un model mai puternic. Astfel calitatea crește exact unde contează, iar viteza vine din restul.
