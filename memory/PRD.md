# MyMedicalApp — PRD

## Problem statement
Platformă SaaS medicală (B2C + B2B/CAM) care citește buletine de analize PDF, le interpretează
cu LLM (Google Gemini 2.5), mapează strict analizele pe standardul LOINC și oferă
dashboard-uri: „Dosar Medical”, grafice de evoluție, comparații între buletine, rapoarte PDF,
credite/plăți, panou admin cu performanță și costuri.

## Stack
- C# ASP.NET Core MVC (`/app/MedicalApp`) + EF Core + SQL Server (local: `PINTILIE\SQLEXPRESS`)
- Microserviciu Python FastAPI pentru matching LOINC (`/app/loinc_service`, uvicorn 127.0.0.1:8000)
- Gemini 2.5 Flash / Pro (cheie în appsettings), SMTP Brevo pentru email

## Note de mediu
Containerul Emergent NU are SQL Server, deci rularea end-to-end se face pe mașina
utilizatorului (VS2026). Aici se validează prin `dotnet build` (0 warnings) și probe izolate.
`dotnet` se află la `/root/.dotnet/dotnet` (nu e în PATH implicit).

## Implementat (istoric recent)
- **Unificare retroactivă LOINC** (`LoincUnifier.cs`) în Dosar Medical / Comparații / Grafice
- **Curățare markeri de laborator** (`LabMarkerSanitizer.cs`)
- **Pipeline Gemini split A/B/C** + fallback monolitic automat + reparator JSON invalid
- **`AbnormalFindingsCompleter.cs`** — completează analizele anormale omise de model
- **Progress UI live** pentru interpretări (`InterpretationProgressTracker.cs`)
- **Batch encoding** în `loinc_service/pipeline.py` (vectorizare în masă, 20s → <1s)
- **LoincHealthMonitor** — probe periodic `/ready`, cache in-memory, auto-start uvicorn pe Windows

### Iunie 2026
- `PipelineMode` comutat pe **`monolithic`** (cerere utilizator, până la validarea modului split)
- **Pre-Flight Check LOINC (P1)**: pe `/Interpretation/Upload` (GET) se citește snapshot-ul
  `ILoincHealthState` (0 ms) și se afișează banner de avertizare `data-testid="loinc-offline-warning"`
  ÎNAINTE ca utilizatorul să consume un credit. Chei noi în `Loc.cs`:
  `LoincOfflineWarningTitle`, `LoincOfflineWarningBody` (5 limbi: en/ro/fr/es/de).
  Nu se avertizează pentru statusurile `unknown` (fără probe încă) și `disabled`.
- Verificat: warning-ul CS0414 din `AdminController.cs` nu mai există — build cu 0 warnings.
- **Colorare după valoare în „VALORI ÎN AFARA NORMALULUI”**:
  - `AbnormalFindingsCompleter.FindKeyResult()` — helper nou care leagă un finding de
    rândul din `key_results` (potrivire pe nume normalizat).
  - `ReportScreenViewModel.LockableFinding` are acum `Status`, `Value`, `Unit`.
  - `Views/Profiles/ViewReport.cshtml`: denumirea + valoarea (valoare + unitate afișate
    lângă nume) colorate roșu (high) / albastru (low) / muștar (borderline); severitatea
    rămâne doar fallback pentru interpretări vechi nepotrivite.
  - `PdfReportGenerator`: aceeași colorare + „Nume — valoare unitate” în raportul PDF
    trimis pe email; în tabelul complet de analize denumirea analizei e colorată după status.
  - Probă: `/app/memory/probes/AbnormalFindingsColorProbe.cs.txt` (PDF generat + verificare
    pixeli roșu/albastru/muștar) — ALL PASS.

- **Fix alarmă falsă „Serviciul LOINC nu este disponibil”** (iunie 2026):
  1. `appsettings.json` → `LoincAutoStart.ProbeTimeoutMs` 800 ms → **3000 ms** (800 ms era prea puțin;
     serviciul e single-worker și nu răspunde cât timp face un batch match).
  2. `LoincMatcher.BaseUrl` `http://localhost:8000` → **`http://127.0.0.1:8000`** (pe Windows
     `localhost` se resolvă întâi pe IPv6 `::1`, iar uvicorn ascultă doar pe IPv4 127.0.0.1 →
     penalizare de conexiune care depășea timeout-ul).
  3. `LoincHealthMonitor`: o singură ratare NU mai marchează serviciul picat — se păstrează
     snapshot-ul anterior până la **2 ratări consecutive**; citește și `entries` din `/ready`
     (serviciul Python nu trimite `loinc_count`).
  4. `LoincMatcherClient.IsReadyAsync()` nou; ecranul de upload **confirmă live** înainte de a
     afișa banner-ul și reîmprospătează cache-ul dacă serviciul e de fapt viu.
  - Notă: banner-ul NU e limitat la admin, apare pentru orice utilizator pe pagina de upload.
  - Probe: `/app/memory/probes/LoincHealthFalseAlarmProbe.cs.txt` + `fake_loinc_service.py` — ALL PASS.

- **B2C interpretation runs in the BACKGROUND** (iunie 2026, decizii utilizator: coadă în proces,
  rezervare credit la lansare, rămâne pe pagină dar poate pleca, 3 simultane / 1 per user):
  - `InterpretationJobQueue` (singleton, `Channel` + gate per user), `InterpretationQueueWorker`
    (`BackgroundService`, `SemaphoreSlim(3)`), `B2cInterpretationRunner` (scoped) — tot pipeline-ul
    mutat din `InterpretationController` (controllerul a scăzut de la 1179 la ~460 linii).
  - `CreditLedger.ReserveOne/RefundOne` — creditul se rezervă la lansare și se restituie automat
    la eșec, la respingere (PDF non-medical) și la repornirea aplicației
    (`StartupSeed.FailOrphanedInterpretationsAsync`).
  - Rând `InterpretationHistories.Status = "processing"` creat la lansare → apare în Istoric ca
    „În lucru” (auto-refresh 10s) și devine „success” când jobul se termină.
  - `/Interpretation/Progress` returnează acum și `redirectUrl` + `historyId`; overlay-ul navighează
    singur la final, afișează eroarea la eșec și are butonul „Lasă să ruleze în fundal”.
  - Cancelarea nu mai vine din browser (`HttpContext.RequestAborted`) — închiderea tabului nu mai
    omoară apelul Gemini.
  - Probă integrată: `/app/memory/probes/bg_interpretation/` (EF InMemory + AI/email fals,
    30 verificări: coadă, credit, happy path, eșec, respingere, orfan, freemium, worker) — ALL PASS.
- **Indicator global de job (dreapta sus)** — `Views/Shared/_JobIndicator.cshtml`, randat din `_Layout`
  pentru utilizatorii logați, plus endpoint `GET /Interpretation/JobStatus` (citește din
  `InterpretationHistories`, nu din memorie, deci supraviețuiește și restartului de browser):
  - pastilă galbenă cu spinner „Fișier PDF în lucru! Așteptați…” cât timp există rând `processing`;
  - pastilă verde cu clopoțel „Gata! Vezi raportul” (+ jingle) când jobul urmărit s-a finalizat,
    memorată în `localStorage` până e închisă → apare și dacă userul a fost pe alt site;
  - pastilă roșie „Interpretarea nu s-a finalizat. Creditul a fost restituit.” la eșec;
  - polling la 6s, se oprește automat la 401 (delogare).
  - Verificat prin probă (endpoint: running / lastDone / lastFailed / 401) + screenshot pe cele 3 stări.
  - **Corecții după feedback (iunie 2026)**: `JobStatus` face acum **o singură** interogare (ultimul rând
    al userului = jobul urmărit) în loc de trei; polling 6s cât urmărim un job / 30s în repaus, cu pauză
    când tabul e ascuns; pastila sincronizează și bannerul „o interpretare rulează în fundal” din pagină
    (înainte rămânea afișat din randarea server-side și contrazicea „Gata!”). Zgomotul din Output a fost
    tăiat prin `Microsoft.EntityFrameworkCore.Database.Command` și `System.Net.Http.HttpClient` = `Warning`
    în appsettings(.Development).json.

## Backlog- **P1**: validare de către utilizator a pachetului anterior (JSON repair + batch encoding LOINC);
  revenire la `PipelineMode: "split"` după validare
- **P2**: „Verdict pe axe” (Axis Verdict) în Admin Dashboard
- **P2**: buton de re-probe LOINC din UI (fără restart aplicație)
- **P4**: Integrare Stripe / Netopia
- **P4**: Deploy cloud (Azure) — serviciu LOINC ca resursă separată
