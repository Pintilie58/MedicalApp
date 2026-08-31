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

## Backlog
- **P1**: validare de către utilizator a pachetului anterior (JSON repair + batch encoding LOINC);
  revenire la `PipelineMode: "split"` după validare
- **P2**: „Verdict pe axe” (Axis Verdict) în Admin Dashboard
- **P2**: buton de re-probe LOINC din UI (fără restart aplicație)
- **P4**: Integrare Stripe / Netopia
- **P4**: Deploy cloud (Azure) — serviciu LOINC ca resursă separată
