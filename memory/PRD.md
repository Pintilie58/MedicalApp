# MedicalApp – PRD

## Original problem statement
Build "MedicalApp", an ASP.NET Core MVC (.NET 9, VS2022) web app where users upload medical analysis PDFs. The app uses AI to interpret the data, generates a nicely formatted localized PDF report and emails it back to the user. Credit-based payment (1 credit per interpretation), user auth, email verification, password reset, 5 languages (EN, RO, FR, ES, DE).

Development workflow: bi-directional Git sync. The agent modifies files in the cloud workspace → user pushes via "Save to GitHub" → user does `Git Pull` in VS2022 → runs with local SQL Server Express (`LENOVO-YOGA2\SQLEXPRESS`).

## Core stack
- ASP.NET Core MVC .NET 9, EF Core + SQL Server
- BCrypt auth, MailKit (Gmail SMTP)
- OpenAI `gpt-4o-mini` (user-provided API key in **User Secrets**)
- UglyToad.PdfPig `1.7.0-custom-5` (PDF text extraction)
- QuestPDF (PDF report generation)
- Chart.js (admin revenue chart)

## Architecture
```
/app/MedicalApp/
├── Attributes/ (AdminAuthorizeAttribute)
├── Controllers/ (Account, Admin, Credits, Home, Interpretation)
├── Data/ (AppDbContext)
├── Models/ (User, Purchase, PromoCode, AdminViewModels, InterpretationHistory, AuthViewModels, CheckoutViewModel, InterpretationResult)
├── Services/ (AdminSettings, EmailService, Loc, MedicalInterpretationService, OpenAISettings, PasswordGenerator, PdfReportGenerator, PdfTextExtractor, PendingRegistrationStore)
├── Views/ (Account, Admin, Credits, Home, Interpretation, Shared)
├── wwwroot/
├── appsettings.json
└── Program.cs
```

## DB schema
- **Users**: Email (PK), Parola, Credite, DataC, CreditConsum, CreditRest, PasswordResetToken, PasswordResetTokenExpiry, **TotalPaid**, **LastLoginAt**, **IsBlocked**, **IsAdmin**
- **InterpretationHistories**: Id, UserEmail, OriginalFileName, Language, Status, ErrorMessage, CreditsConsumed, InputTokens, OutputTokens, CreatedAt
- **Purchases** (new): Id, UserEmail, PurchasedAt, AmountEur, CreditsAdded, PaymentMethod, PackageKey, PromoCode
- **PromoCodes** (new): Id, Code (UQ), CreditsToAdd, ValidFrom, ValidUntil, TimesUsed, MaxUses, IsActive, CreatedAt

## Implemented (changelog)
- ✅ Project scaffolding (.NET 9 MVC) + SQL Server via EF Core
- ✅ 5-language localization via `Loc.cs`
- ✅ BCrypt auth + EF Core
- ✅ Password reset via email link (MailKit)
- ✅ Registration with 4-digit email verification
- ✅ Credit system + simulated checkout
- ✅ PDF text extraction (layout-aware Y/X sort using PdfPig 1.7.0-custom-5)
- ✅ OpenAI interpretation with strict JSON Schema + Seed + MaxOutputTokens=16000 + mandatory 5-step algorithm prompt (Imunochimie etc.)
- ✅ Localized PDF report generation (QuestPDF, A4)
- ✅ Debug email attachments: extracted text + raw GPT JSON
- ✅ OpenAI ApiKey migrated to User Secrets (not in repo)
- ✅ Loading overlay "Așteptați câteva secunde!" (5 languages) on interpretation upload
- ✅ Stronger correlations + recommendations prompts (min 3-5 / 4-6 sentences)
- ✅ **[Feb 2026] Admin Dashboard** (new):
  - `AdminController` + `[AdminAuthorize]` attribute (session + IsAdmin flag + not blocked)
  - `AdminSettings` in appsettings → list of admin emails auto-promoted at register/login
  - Dashboard: 12 stat cards, Top-10 spenders, 30-day revenue chart (Chart.js)
  - Users list with search + pagination
  - User detail page: profile, purchase history, interpretation history, actions (give credits, reset password, block/unblock)
  - Bulk email with filters (all / paying / with_credits / last 30 days / blocked)
  - Promo codes CRUD (e.g. `Med3` → +3 credits for new registrations, with ValidFrom/ValidUntil, MaxUses)
  - Register form accepts optional promo code
  - Purchase row written on every successful credit purchase + TotalPaid incremented on User
  - LastLoginAt tracked on every login
  - Block check on login

## Pending / Backlog
### P0 – validation
- 🧪 User to apply admin dashboard changes via `Save to GitHub → Create Branch & Push`, merge PR on GitHub, then `Git Pull` locally, run EF migration, test all flows.

### P2
- Real payment gateway (Stripe / Netopia / PayPal) replacing the simulated checkout
- Interpretation history page for users (their own)

### P3
- Deploy to Azure App Service + SQL Azure
- PWA (installable on mobile)

## Known constraints
- Cheia OpenAI e în User Secrets (NU în repo). Sandbox-ul cloud nu o are.
- Agent cannot run/test the app in cloud sandbox (no .NET SDK, no SQL Server). Validation happens on user's Windows machine.
