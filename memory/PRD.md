# MedicalApp – PRD

## Original problem statement
Build "MedicalApp", an ASP.NET Core MVC (.NET 9, VS2022) web app where users upload medical analysis PDFs. The app uses AI to interpret the data, generates a nicely formatted localized PDF report and emails it back to the user. Credit-based payment (1 credit per interpretation), user auth, email verification, password reset, 5 languages (EN, RO, FR, ES, DE).

Development workflow: bi-directional Git sync. The agent modifies files in the cloud workspace → user pushes via "Save to GitHub" → user does `Git Pull` in VS2022 → runs with local SQL Server Express (`LENOVO-YOGA2\SQLEXPRESS`).

## Core stack
- ASP.NET Core MVC .NET 9, EF Core + SQL Server
- BCrypt auth, MailKit (Gmail SMTP)
- OpenAI `gpt-4o-mini` (user-provided API key)
- UglyToad.PdfPig `1.7.0-custom-5` (PDF text extraction)
- QuestPDF (PDF report generation)

## Architecture
```
/app/MedicalApp/
├── Controllers/ (Account, Credits, Home, Interpretation)
├── Data/ (AppDbContext)
├── Models/ (User, InterpretationHistory, AuthViewModels, CheckoutViewModel, InterpretationResult)
├── Services/ (EmailService, Loc, MedicalInterpretationService, OpenAISettings, PasswordGenerator, PdfReportGenerator, PdfTextExtractor, PendingRegistrationStore)
├── Views/ (Account, Credits, Home, Interpretation, Shared)
├── wwwroot/ (css, js, images)
├── appsettings.json
└── Program.cs
```

## DB schema
- **Users**: Email (PK), Parola, Credite, DataC, CreditConsum, CreditRest, PasswordResetToken, PasswordResetTokenExpiry
- **InterpretationHistories**: Id (PK), UserEmail, OriginalFileName, UploadDate, CostInCredits, IsSuccess, ErrorMessage

## Implemented (changelog)
- ✅ Project scaffolding (.NET 9 MVC) + SQL Server via EF Core
- ✅ 5-language localization via `Loc.cs`
- ✅ BCrypt auth + EF Core
- ✅ Password reset via email link (MailKit)
- ✅ Registration with 4-digit email verification
- ✅ Credit system + simulated checkout
- ✅ PDF text extraction via PdfPig
- ✅ OpenAI interpretation (gpt-4o-mini)
- ✅ Localized PDF report generation (QuestPDF, A4)
- ✅ Interpretation integrated in dashboard (−1 credit per call)
- ✅ **[Feb 2026] Option A – Strict & Deterministic AI interpretation:**
  - `OpenAISettings.cs` defaults: Temperature=0.0, Seed=42, MaxOutputTokens=8000, TimeoutSeconds=120
  - `appsettings.json` aligned to the same values (ApiKey untouched)
  - `PdfTextExtractor.cs` uses `ContentOrderTextExtractor.GetText(page, true)` (with fallback) for layout-aware extraction (preserves table rows/columns of lab PDFs)
  - `MedicalInterpretationService.cs` switched from plain JSON mode to **OpenAI Structured Outputs** (`ChatResponseFormat.CreateJsonSchemaFormat(strict:true)`) with a full schema for `medical_interpretation`
  - `Seed` now passed in `ChatCompletionOptions` for deterministic runs
  - System prompt hardened with an "EXTRACTION COMPLETENESS" section forcing the model to emit EVERY measured parameter, compute status vs. reference range, and add any non-normal to `abnormal_findings`
  - Added `FinishReason==Length` warning in logs if response ever gets truncated

## Pending / Backlog
### P0 – validation
- 🧪 User to pull from GitHub and retest with the same PDF that previously gave inconsistent output. Expected: same output on repeated runs + every lab parameter present in `key_results`.

### P1
- History page for users to view their past interpretations (read from `InterpretationHistories`)

### P2
- Move secrets (`OpenAI:ApiKey`, Gmail App Password) from `appsettings.json` to User Secrets / Environment Variables
- Real payment gateway (Stripe / Netopia / PayPal) replacing the simulated checkout

### P3
- Deploy to Azure App Service + SQL Azure
- PWA (installable on mobile)

## Known constraints
- User's **local** `appsettings.json` must keep the OpenAI API key after Git Pull (the repo version has `ApiKey: ""`). If Git Pull overwrites the local value, user must re-paste the key or (preferred) migrate to User Secrets (P2).
- Agent cannot run/test the app in the cloud sandbox (no .NET SDK, no SQL Server); validation happens on the user's Windows machine.
