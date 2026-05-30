# Audit Tehnic Complet — MedicalApp
**Data audit**: Feb 2026
**Auditor**: E1 (Sonnet 4.5)
**Scope**: codebase complet (`/app/MedicalApp` C# + `/app/loinc_service` Python)
**Versiune**: post-Faza 4.2

---

## Executive Summary

Proiectul e **funcțional, ambițios și matur ca features** (B2C + B2B CAM, multi-lingv, AI, PDF, email, LOINC). Calitatea codului e **medie-bună** — comentarii excelente, naming clar, separare logică între B2C și B2B prin Areas. Există însă **datorie tehnică acumulată** care încetinește velocitatea și crește riscul de bug-uri (ex. Densitate urinară).

**Verdictul scurt**: proiectul nu are bug-uri arhitecturale critice. Are însă fișiere prea mari, lipsă teste C#, autentificare fragilă (Session manual), un layer violation (Service → Controller), și o serie de pași P1 de igienă tehnică care l-ar pregăti pentru: scalare, refactoring fără frică, deploy production.

**Recomandare prioritizată**:
- **P0 (acum)**: 3 elemente
- **P1 (4-6 săptămâni)**: 6 elemente
- **P2 (când ai timp)**: 8 elemente
- **P3 (înainte de deploy production)**: 4 elemente

---

## Metrici cheie

| Metrică | Valoare | Observație |
|---|---|---|
| Linii C# (excl. Migrations) | ~14,435 | În creștere rapidă |
| Linii .cshtml | ~4,584 | OK |
| Cele mai mari 6 fișiere C# | ProfilesController (1184), GeminiService (1178), Loc (1020), InterpretationController (714), CamBatchService (659), AdminController (598) | TOATE peste 500 linii — semnal de „God class" |
| Migrații EF | 5 | OK |
| EF queries în controllers | 76 | |
| Queries cu `AsNoTracking` | 41 (54%) | **46% din read queries sunt tracked degeaba** |
| Try/catch blocks | 27 în CamBatchService și InterpretationController | OK pentru integrări AI |
| Test files C# | **0** | 🚨 zero unit tests |
| Test files Python | 1 (smoke) | OK |
| TODO/FIXME comentarii | 1 (Stripe/Netopia placeholder) | foarte curat |
| `[Authorize]` attribute folosit | **0** | toată auth e manuală via Session |
| `Session.GetString("UserEmail")` repetat | 22 locuri | DRY violation |

---

## 🔴 P0 — Probleme critice (rezolvă în următoarele zile)

### P0.1 — Bug Densitate urinară încă nerezolvat
**Severitate**: medie-mare (afectează acuratețea raportului — produs central)
**Status**: raportat ieri, încă nu funcționează după fix-ul anterior cu `BorderlineTolerancePct`
**Ipoteze**:
1. `StatusValidator` apelat pe path-ul CAM, dar `TryParseRange` eșuează pe formatul exact din PDF (ex. `"1.005–1.030"` cu em-dash, sau `"1,005-1,030"` cu virgulă decimală europeană).
2. Gemini emite range-ul într-un câmp diferit de `ReferenceRange`.
3. PDF-ul vizibil utilizatorului folosește datele DIN BAZA DE DATE (rândul deja persistat din interpretarea anterioară) — re-procesare nu re-pesistă.

**Plan de acțiune**: cere user-ului 1 PDF eșantion + range-ul EXACT cum apare în PDF + log-ul live al lotului. Adaug logging detaliat în `StatusValidator.Validate` care arată EXACT ce range și valoare a primit pentru fiecare parametru skip-uit.

### P0.2 — Layer violation: Service → Controller
**Severitate**: arhitecturală
**Location**: `MedicalApp/Services/CamComparePdfGenerator.cs` linia 69 apelează `ProfilesController.BuildComparison(...)` (metodă statică pe un controller).

**De ce e o problemă**:
- Imposibil de scris unit tests pentru `CamComparePdfGenerator` fără a instanția un controller.
- Schimbarea semnăturii `BuildComparison` necesită modificări încrucișate.
- Convenție inversă: în clean architecture controllers consumă services, nu invers.

**Fix sugerat**: mută `BuildComparison` într-un `ProfileCompareBuilder` service. Refactor de ~50 linii, low-risk.

### P0.3 — Connection string și API keys în appsettings.json (Source Control Risk)
**Severitate**: securitate
**Location**: `MedicalApp/appsettings.json`

Conține:
- `ConnectionStrings:DefaultConnection` cu numele exact al serverului `PINTILIE\SQLEXPRESS`
- `EmailSettings:Password` (gol acum, dar slot e acolo)
- `Gemini:ApiKey` (gol acum)
- `OpenAI:ApiKey` (gol)

**De ce e o problemă**: dacă vreodată completezi vreuna și faci `git push` din greșeală → secrete în GitHub forever (git history). Și e ușor să se întâmple.

**Fix sugerat**:
1. Adaugă `appsettings.Local.json` la `.gitignore` (probabil deja e — verifică).
2. Mută secretele acolo, păstrează `appsettings.json` cu stringuri goale + comment care zice "OVERRIDE IN appsettings.Local.json".
3. Sau folosește **dotnet user-secrets** pentru dev: `dotnet user-secrets set "Gemini:ApiKey" "..."` — niciodată în source control.

---

## 🟡 P1 — Important (rezolvă în următoarele 4-6 săptămâni)

### P1.1 — Fișiere prea mari (God classes)
**Toate aceste fișiere ar trebui descompuse**:

| Fișier | Linii | Sugestie split |
|---|---|---|
| `ProfilesController.cs` | 1184 | → `ProfilesController` (CRUD profile) + `ProfileHistoryController` (history+download) + `ProfileCompareController` (compare) + `ProfileEvolutionController` (evolution+export) |
| `GeminiMedicalInterpretationService.cs` | 1178 | → `GeminiClient` (HTTP layer) + `GeminiPromptBuilder` (system prompt + schema) + `GeminiResponseParser` (JSON → model) |
| `Loc.cs` | 1020 | → migrare la `.resx` files (vezi P2.1) |
| `InterpretationController.cs` | 714 | → `InterpretationController` (upload + processing) + `InterpretationDuplicateController` (duplicate detection flow) |
| `CamBatchService.cs` | 659 | → `CamBatchService` (orchestration) + `CamFileProcessor` (per-file logic) + `CamErrorHandler` (move/retry logic) |
| `AdminController.cs` | 598 | OK ca e mare (CRUD admin) dar separa user-mgmt vs promo-mgmt vs stats |

**Beneficiu**: cod mai testabil, navigare mai rapidă, code review mai ușor, refactoring sigur.

### P1.2 — Auth pattern fragil (Session.GetString repetat 22 locuri)
**Problemă**: fiecare action verifică manual `HttpContext.Session.GetString("UserEmail")`, uneori și `user.UserType == "Clinic"`. Ușor de uitat pe un nou action → bug de securitate.

**Fix sugerat**: action filter custom `[CurrentUser]` care:
- Citește Session
- Încarcă user din DB într-un cache scoped
- Setează `HttpContext.Items["CurrentUser"]`
- Returnează 401 dacă lipsește

Apoi în controller: `CurrentUser` extension method pe ControllerBase. Reduce auth-checking de la ~6 linii pe action la 0.

**Bonus**: definește `[RequireRole("Clinic")]`, `[RequireRole("Admin")]` ca attribute filters → înlocuiește toate string comparisons inline.

### P1.3 — Magic strings pentru roluri
**Problemă**: `"Clinic"`, `"Individual"`, `"Admin"` apar literal în ~15 locuri (`AccountController`, `CreditsController`, `DashboardController`, etc.).

**Fix sugerat**: clasă `Roles` cu constante:
```csharp
public static class Roles
{
    public const string Individual = "Individual";
    public const string Clinic = "Clinic";
    public const string Admin = "Admin";
}
```
Atunci când vrei să adaugi rol nou (ex. "Doctor", "Lab"), schimbi într-un singur loc.

### P1.4 — Lipsă unit tests C#
**Problemă**: ZERO teste pentru logică critică:
- `StatusValidator.ComputeStatus` (a 3-a oară când debugăm bug-ul de Densitate fără test care să prindă regresia)
- `SamplingDateParser.TryParse` (Romanian/EN/FR date formats)
- `BuildComparison` (LOINC drift, summary stats)
- `LoincSourceBadge`
- `StatusValidator.TryParseRange` (formats: `"X-Y"`, `"<X"`, `"≤X"`, `"1.005–1.03 / g/mL"`)

**Fix sugerat**: creează proiect `MedicalApp.Tests` (xUnit) cu **doar** aceste 5 module testate. Target: 30 test cases. Time effort: ~2h. ROI: enorm.

Test exemplu pentru bug-ul actual:
```csharp
[Theory]
[InlineData("1.024", "1.005 - 1.03", "normal")]
[InlineData("1.030", "1.005 - 1.03", "borderline")]
[InlineData("1.04",  "1.005 - 1.03", "high")]
public void Status_DensitateUrinara_CorrectClassification(...)
```

### P1.5 — `AsNoTracking()` lipsă pe 46% din read queries
**Problemă**: EF tracks entități pentru queries read-only → memory waste + slow.

**Fix sugerat**: pe toate `*Controller.Index()`, `History()`, `Compare()`, `Dashboard()` adaugă `.AsNoTracking()` pe queries care nu salvează ulterior. Audit existent: 35 locuri lipsă.

### P1.6 — Denormalizare `AnalysisResults` (deja în backlog)
**Problemă**: fiecare apel `Compare/Evolution/History` deserializează JSON-ul `RawJsonResult` (poate fi 30-50KB per analiză). Pentru un pacient cu 10 analize → 500KB parsing per request.

**Fix sugerat**: tabel separat `AnalysisResult` (PK=Id, FK=InterpretationHistoryId/ClinicAnalysisId, plus Parameter, Value, Unit, RefRange, Status, LoincCode, LoincClass). Populat în trigger la insert/update pe parent. Query-uri agregate devin 10x mai rapide.

**Avantaj bonus**: Search/filter pe parametri devine posibil (ex. „Arată-mi toate analizele cu hemoglobina < 10").

---

## 🟢 P2 — Datorie tehnică (rezolvă când ai timp)

### P2.1 — Migrare `Loc.cs` (1020 linii) la `.resx`
Multi-language în C# class e suboptimal:
- Greu de adăugat language nou (trebuie programator)
- Niciun translator nu poate edita fără IDE
- Nu se poate folosi format `.po`/Crowdin/Lokalise

**Sugestie**: migrează la ASP.NET resource files (`SharedResource.ro.resx`, `SharedResource.en.resx`, etc.). Auto-completion + UI editor în VS.

### P2.2 — Duplicate logic B2C ↔ CAM
Cod aproape identic în:
- `InterpretationController.Upload` ↔ `CamBatchService.ProcessOneFileAsync` (validare PDF, apel Gemini, StatusValidator, LOINC matcher, PDF gen)
- `History.cshtml` ↔ `CheckPdfs/Index.cshtml`
- `Compare.cshtml` ↔ `CamComparePdfGenerator`

**Sugestie**: extract `IInterpretationPipeline` service cu metoda `RunAsync(byte[] pdfBytes, ...)` returnând `InterpretationResult + audit`. Atât B2C cât și CAM o apelează. Reduce duplicare cu ~300 linii.

### P2.3 — Lipsește un Global Error Handler
**Problemă**: excepții necapturate → yellow screen of death (în dev) sau 500 generic în prod.
**Fix**: middleware `ExceptionHandlingMiddleware` care:
- Loghează cu correlation ID
- Returnează JSON pentru `/api/*`, redirect pentru pagini
- Setează `TempData["ErrorMessage"]` cu mesaj user-friendly

### P2.4 — Lipsă correlation IDs în logging
**Problemă**: când debug-ezi un lot CAM cu 50 fișiere și apar erori, nu poți să le grupezi.
**Fix**: middleware care setează `Activity.Current?.AddTag("BatchId", id)`. Apare automat în Serilog/Application Insights logs.

### P2.5 — GDPR — retention policy + export user data
**Problemă**: `ClinicAnalysis.RawJsonResult` conține date medicale sensibile. Niciun mecanism de:
- Ștergere automată după N ani
- Export self-service ("descarcă toate datele mele")
- Right-to-be-forgotten

**Sugestie**: un BackgroundService nightly care șterge analize > 5 ani. Plus un buton `/Account/ExportMyData` care livrează un .zip cu toate analizele.

### P2.6 — Task.Run pentru CAM batch e fragil
**Problemă**: dacă IIS face recycle în mijlocul unui lot de 50 fișiere → progresul se pierde. `ClinicBatchRun` rămâne în status `"Running"` pentru veci.

**Sugestie ușoară**: BackgroundService la pornire detectează `Status="Running"` mai vechi de X minute → marchează `Failed`.
**Sugestie completă**: queue persistent (Quartz.NET / Hangfire) cu retry built-in.

### P2.7 — Configurare LLM dublă (OpenAI + Gemini) cu OpenAI dead
`appsettings.json` are secțiune întreagă `OpenAI` neutilizată. Curăță sau document the intent (poate fallback dacă quota Gemini se termină).

### P2.8 — Lipsă `[ValidateAntiForgeryToken]` pe câteva POST-uri
Nu toate POST-urile au atributul. Risc minor în prod (CSRF pentru acțiuni autenticate).

### P2.9 — Magic numbers și hardcoded paths
- `MinScore = 0.55` LOINC matcher — explicat?
- `BorderlineTolerancePct = 0.05` — comentariu zice cum, dar nu și de ce 5%
- `FilesRoot = "C:\\MedicalApp_files"` în appsettings — Linux/macOS incompat dacă vreodată muți deploy

---

## 🔵 P3 — Înainte de production deploy

### P3.1 — Lipsă CI/CD
Push GitHub → nimic se întâmplă. Nu există build automat, teste automate, deploy automat.
**Sugestie**: GitHub Actions cu 2 jobs: `build` (dotnet build + test) + `deploy-azure` (opțional, condiționat de tag).

### P3.2 — Lipsă monitorizare production
Cum afli că un user a primit 500 ieri? Niciun mecanism.
**Sugestie**: Application Insights gratuit până la 5GB/lună. Plus Sentry pentru excepții.

### P3.3 — Pricing Gemini hardcoded
`GeminiPricing:Flash:InputPerMillionUsd: 0.30` — dacă Google schimbă prețul, app afișează cost greșit user-ilor. Sugestie: refresh periodic dintr-un endpoint sau update via admin UI.

### P3.4 — Lipsă rate limiting per user
Un user rău intenționat poate trimite 1000 PDF-uri pe minut → consumi quota Gemini.
**Sugestie**: middleware `AspNetCoreRateLimit` cu `100 requests/oră` per user.

---

## Highlights pozitive (ce e bine)

1. **Comentarii excelente** — cod bine documentat, mai ales pentru complexitatea logică (LOINC matcher, Gemini retries, drift detection). Lăudabil.
2. **BCrypt pentru parole** — corect, nu plaintext / MD5.
3. **ValidateAntiForgeryToken** prezent pe action-urile critice (Admin*).
4. **Areas** folosit corect pentru separarea CAM de B2C.
5. **Migrații EF** versionate și aplicate consistent.
6. **i18n** funcționează deși infrastructura e suboptimă.
7. **Python LOINC matcher** are smoke test — punct bun.
8. **Retry logic** cu fallback Flash→Pro pentru Gemini — robust.
9. **`StartupSeed.cs`** — clinică demo + admin demo idempotent. Bun pentru dev.
10. **`SamplingDateParser`** — design bun (regex + named months EN/RO/FR), exact gen de utility care merită extracted.

---

## Recomandare ordine atac

Dacă vrei să atacăm acum:

**Sprint 1 (1 săptămână, ~6h efort)**:
- ✅ Rezolvă P0.1 (Densitate — debug profund cu logging detaliat)
- ✅ Rezolvă P0.2 (Service → Controller layer fix, 50 linii)
- ✅ Rezolvă P0.3 (secrets out of appsettings)

**Sprint 2 (2 săptămâni, ~10h efort)**:
- ✅ P1.4 (proiect Tests cu 30 test cases pe StatusValidator + SamplingDateParser + BuildComparison)
- ✅ P1.2 + P1.3 (auth filter + role constants)
- ✅ P1.5 (AsNoTracking pe 35 queries)

**Sprint 3 (3 săptămâni, ~15h efort)**:
- ✅ P1.1 (split fișiere mari — ProfilesController + GeminiService)
- ✅ P1.6 (Denormalizare AnalysisResults — migration + populare backfill + refactor BuildComparison)

**Backlog**: P2.* și P3.*

---

## Estimare realistă

- **Datorie tehnică actuală**: ~30-40h dezvoltare ca să închizi P0+P1.
- **Cost-of-inaction**: bug-uri ca Densitate vor reveni la fiecare schimbare; refactorizările devin tot mai scumpe pe măsură ce fișierele cresc.
- **Recomandare**: **NU pause feature dev pentru audit complet acum**. Mai bine atacăm P0 (3 elemente) + P1.4 (tests) în paralel cu Faza 5 features. Restul intră în roadmap normal.

---

*Sfârșit raport audit.*
