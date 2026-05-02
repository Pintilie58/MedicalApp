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
