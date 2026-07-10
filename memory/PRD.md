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
