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
- **Limite de coadă configurabile + widget admin** (iunie 2026):
  - `InterpretationQueueSettings` legat de secțiunea `InterpretationQueue` din `appsettings.json`
    (`MaxConcurrent: 3`, `MaxPerUser: 1`, valorile < 1 sunt corectate la 1). Se schimbă fără rebuild,
    doar cu restart. `InterpretationQueueWorker` citește `MaxConcurrent` o singură dată la pornire.
  - `GET /Admin/InterpretationQueueStatus` + widget în `Views/Admin/Index.cshtml`:
    „X în lucru / Y la rând / limite: 3 simultan / 1 per utilizator”, plus avertisment roșu când
    există rânduri `processing` fără job activ (orfane rămase după un crash). Poll 15s.
  - Document de arhitectură pentru scalare: **`/app/memory/AZURE_SCALING.md`** (Gemini NU e gâtuirea;
    gâtuirile reale: LOINC single-worker, coadă în memorie, PDF în RAM, SMTP sincron; plan de trecere
    la Service Bus + Blob + worker separat).
  - Regresie verificată: suita completă re-rulată → **48/48 PASS**, inclusiv teste noi care demonstrează
    că limitele din configurație schimbă real comportamentul (2 per user acceptă 2, refuză al 3-lea).
- **Audit I/O async + pregătire SQL Azure** (iunie 2026, pas 1 din planul de scalabilitate):
  - Audit: Gemini/HTTP, email (MailKit), upload PDF, coada de fundal — deja 100% async;
    zero `SaveChanges()` sincron, zero `async void`; `.Result` apare doar după `Task.WhenAll`
    (nu e sync-over-async). Endpoint-urile FastAPI sunt `def` intenționat (CPU-bound → threadpool).
  - Corectate 3 apeluri sincrone reale: `AccountController.Dashboard()` (era `IActionResult` cu
    `_db.Users.FirstOrDefault` — pagina cea mai vizitată), `CamBatchService.WriteSumar` → `WriteSumarAsync`,
    `CAM/DashboardController` `File.WriteAllBytes` → `WriteAllBytesAsync`.
  - `Program.cs`: `UseSqlServer` cu `EnableRetryOnFailure(5, 10s)` + `CommandTimeout(30)` — obligatoriu
    pe SQL Azure. Sigur pentru că aplicația nu folosește tranzacții explicite (`BeginTransaction`).
  - Limitări asumate: `File.Move/Delete`, `Directory.GetFiles` NU au variante async în .NET (rămân
    sincrone în serviciile de fundal). Problema reală a CAM pe Azure nu e async, ci folderele locale.
  - Regresie: suita re-rulată → **55/55 PASS** (inclusiv teste noi: command timeout, strategie de retry,
    construcția modelului EF fără conexiune, `Dashboard()` async cu și fără sesiune).

- **Colorare după status în comparația B2B (CAM)** (iunie 2026): `CamComparePdfGenerator` —
  denumirea analizei e colorată după statusul **celei mai recente** valori (coloanele sunt vechi→nou),
  iar fiecare valoare din tabel după statusul ei; paletă identică cu B2C (`#c62828` high, `#1565c0` low,
  `#f9a825` borderline, `#2e7d32` normal, gri neutru la status necunoscut). Glifele ↑↓≈✓ folosesc acum
  aceleași hex-uri. Adăugată linie de legendă `CamCompareLegendStatusColors` în toate 7 limbile.
  Verificat prin probă (`/app/memory/probes/CamCompareColorProbe.cs.txt`): PDF real generat + analiză
  de pixeli (toate 4 culorile prezente) + text extras.

- **CAM pregătit pentru Azure Blob Storage** (iunie 2026, inactiv până la hostare):
  - `ICamFileStore` rescris din „căi de folder” în **operațiuni** (List/Read/Write/Move/Delete/
    Exists/Ensure/GetDisplayLocation). Fără asta, Blob-ul e imposibil (nu are foldere, nici rename).
  - `LocalDiskCamFileStore` (dev/Docker, comportament identic cu înainte) + `BlobCamFileStore` nou
    (Azure.Storage.Blobs + Azure.Identity, managed identity fără secrete, move = copiere server-side).
  - Comutare din `CamSettings:Storage` = `LocalDisk` | `Blob`; `Program.cs` alege implementarea.
  - Adaptați: CheckPdfs/Batch/CAM-Dashboard controllers, `CamBatchService` (lucrează cu NUME de
    fișiere, nu căi), `CamRetentionService` (măsurare + sweep async prin store),
    `CamBatchSumarWriter.Write` → `Build` (întoarce nume + text, nu mai scrie pe disc).
  - Testat: **50/50 PASS** — același contract de 25 verificări rulat pe disc local ȘI pe Azurite
    (emulator Azure real, pornit local), inclusiv izolarea între clinici, non-suprascriere,
    integritate la mutare, path traversal. Plus regresie: suita B2C 55/55 PASS.
  - Ghid de activare + Docker/Azurite: **`/app/memory/CAM_BLOB_STORAGE.md`**
    (atenție: Azurite are nevoie de `--skipApiVersionCheck` cu SDK-ul actual).

- **Poziția în coadă + timp estimat** (iunie 2026): `InterpretationJobQueue` ține ordinea joburilor
  care așteaptă (`_waiting`: HistoryId → număr de secvență); `GetPosition(historyId)` întoarce locul
  1-based (0 = deja în lucru), `MarkStarted` e apelat de worker când jobul iese din coadă.
  `JobStatus` returnează `position` + `etaSeconds` = `ceil(poziție / MaxConcurrent) × durata medie`
  (media ultimelor 20 de interpretări reușite din `DurationMs`, cache 5 min, implicit 180s).
  Afișat în pastila din dreapta sus („La rând: poziția 3 • ~6 min”) și în Istoric
  („La rând (poziția 2)” vs „În lucru”). Chei noi în 5 limbi.
  Notă: cu `MaxPerUser = 1`, poziția > 1 apare doar când alți utilizatori au joburi înaintea ta.
  Testat: **66/66 PASS** (avansarea pozițiilor la `MarkStarted`, job necunoscut → 0, formula ETA).

- **Stateless / multi-instance (pas 2 din planul de scalabilitate)** — iunie 2026, **inactiv** implicit
  (`ScaleOut:Enabled = false` ⇒ comportament local identic):
  1. Sesiune pe `AddDistributedSqlServerCache` (tabel `AppSessionCache`, creat automat la pornire) —
     fără asta, cu 2 instanțe utilizatorii se delogau aleatoriu (identitatea e `Session["UserEmail"]`).
  2. Chei Data Protection în Blob (`dataprotection/keys.xml`, `SetApplicationName`) — altfel
     „antiforgery token could not be decrypted”, inclusiv la fiecare repornire de container Docker.
  3. `PendingRegistrationStore` rescris pe `IDistributedCache` (activ mereu; local = memorie) —
     codurile de verificare la înregistrare nu mai sunt legate de o instanță.
  4. `SingletonLeaseService` (tabel `AppSingletonLease`, MERGE atomic, fail-open) — `DailySummaryService`
     și `BudgetAlertService` nu mai trimit emailuri duplicate de pe fiecare instanță.
  5. `FailOrphanedInterpretationsAsync` respectă `OrphanGraceMinutes` (30) când scale-out e activ —
     înainte, o instanță care repornea marca drept eșuate joburile ce rulau pe celelalte instanțe.
  - Testat: **22/22 PASS** (`/app/memory/probes/ScaleOutProbe.cs.txt`), inclusiv round-trip real de
    Data Protection între două instanțe pe Azurite și scenariul de orfani. Regresie: B2C 66/66 PASS.
  - Ghid de activare: **`/app/memory/SCALE_OUT.md`**.

- **Fix avertisment EF „First/FirstOrDefault without OrderBy”** (iunie 2026): cele două agregate
  `GroupBy(_ => 1).Select(...).FirstOrDefaultAsync()` din `Areas/CAM/Controllers/DashboardController.cs`
  (`PopulateStatsAsync`, `ComputeBatchPeriodRangeAsync`) au primit `.OrderBy(...)` înainte de
  `FirstOrDefaultAsync`. Avertismentul era o euristică EF — agregatul întoarce cel mult un rând, deci
  `First` era deja determinist și NU s-au afișat niciodată date greșite. Fix de igienă a logului.
  Reprodus și verificat cu `/app/memory/probes/EfFirstWithoutOrderByProbe.cs.txt` (forma veche = 1
  avertisment, forma nouă = 0, valori agregate identice). Confirmat de testing agent:
  `/app/test_reports/iteration_20.json` — 0 probleme, regresie B2C 66/66 PASS.

- **Fix duplicare rânduri la același analit cu intervale de referință „proză”** (iunie 2026):
  cauza — `LoincUnifier.NormalizeRange` compara TOATE numerele din câmpul „interval de referință”,
  deci HbA1c cu „…normal: 4.8-5.6% … >=6.5%” vs același text + „Ținta terapeutică ≤7%” avea
  semnături diferite ⇒ codurile `4548-4` și `41995-2` nu se uneau (2 rânduri, aceeași UM `%`).
  Trei straturi noi în `LoincUnifier`:
  1. **Interval operativ** (`OperativeRange`): se extrage PRIMUL interval real din text
     (`4.8-5.6`, `< 130`, `≤ 7`, `până la 200`, `13.5-17.5` din intervale pe sexe) și se ignoră
     proza interpretativă. Fallback conservator: fără interval operativ ⇒ comportamentul vechi
     (toate numerele / text normalizat) ⇒ zero regresie.
  2. **Compatibilitate în loc de identitate**: gruparea se face acum pe nume → unitate →
     *clustere de intervale compatibile* (`ClusterByRange`). Compatibil = același interval
     operativ, sau text identic, sau o listă de numere e prefixul celeilalte (un lab a scris mai
     mult). Intervale operative diferite = contradicție reală ⇒ NU se unifică, iar dacă și codurile
     diferă rândurile primesc semnul „!” (`MissingAxis = "range"`).
  3. **Veto fail-open pe dicționarul LOINC**: fuziunea e blocată doar dacă AMBELE coduri au
     `LoincLongName` oficial și denumirile nu au nici un cuvânt semnificativ comun
     (`OfficialNamesConflict`). Lipsa denumirii nu blochează niciodată.
  - Bonus: `NormalizeUnit` recunoaște acum și `mii/µL` (`miiul`, `mii/L`, `mii/mmc`) ca `10e3/ul`.
  - Se aplică retroactiv (display-only) în Dosar Medical, Comparații, Grafice și PDF-uri — nu e
    nevoie de reprocesarea buletinelor.
  - Testat: probă nouă `/app/memory/probes/LoincRangeUnificationProbe.cs.txt` — **28/28 PASS**
    (HbA1c cu proză diferită unește; Limfocite %/mii-µL NU; Fibrinogen g/L vs mg/dL separate prin
    `UnitScope`; INR fără unitate unește; unitate/interval lipsă pe un buletin ⇒ „!”; intervale
    contradictorii NU unesc; `<10` vs `>10` NU unesc; „negativ” vs „absent” NU unesc; prefix de
    numere unește; veto LOINC blochează CRP vs timp de protrombină, dar e fail-open fără denumire).
    Regresie: `LoincUnifierProbe` **24/24 PASS**, suita B2C **66/66 PASS**, build 0 warning-uri.

- **Serviciul LOINC: studiu de scalare + cache pe două niveluri** (iunie 2026,
  document complet în **`/app/memory/LOINC_SCALING.md`**):
  - Măsurători reale (`/app/memory/probes/loinc_capacity_probe.py`): encoding 14,5 ms/nume în
    batch (207 ms individual), scanare 45 ms/analiză pe 97k rânduri (142 MB citiți de fiecare
    analiză), **855 MB RSS per worker**, cold start 8-10 s. Concluzii: scalarea se face pe
    replici mici (nu `--workers N`), iar gâtuirea se simte ca latență + alarme false `/ready`.
  - **Faza 0 (Python)**: cache LRU în proces în `find_loinc` (cheie = nume + unitate + nume brut +
    panel header + linia analitului, `LOINC_CACHE_SIZE=20000`), golit automat la `STORE.load()`;
    `match-batch` vectorizează doar necunoscutele și dedupează întrebările identice;
    `max_workers = min(cpu_count, n)`; `/health` și `/ready` devenite `async` (fix de fond pentru
    alarma falsă „Serviciul LOINC nu e disponibil”); endpoint nou `/loinc/cache` cu hit rate.
  - **Faza 1 (C#)**: tabel `LoincMatchCache` + `LoincMatchCacheStore` + integrare în
    `LoincMatcherClient` — cache **global** (decizia utilizatorului), cheie SHA-256 care include
    versiunea pipeline-ului (`LoincMatcher:Cache:PipelineVersion`). Python e întrebat numai despre
    nume noi; un buletin complet cunoscut nu generează nici un apel HTTP și **se codifică chiar și
    când serviciul Python e picat**. Kill switch: `LoincMatcher:Cache:Enabled = false`.
  - Migrare EF nouă: **`AddLoincMatchCache`** (necesită `Update-Database`).
  - Testat: probă Python **20/20 PASS** (`loinc_cache_probe.py`), probă C# **33/33 PASS**
    (`LoincMatchCacheProbe.cs.txt` — inclusiv „serviciu picat + analize cunoscute ⇒ tot se
    codifică”, invalidare la bump de versiune, cache oprit ⇒ comportament identic cu înainte),
    suita de aur LOINC **56/56 PASS** (neschimbată față de baseline), regresie B2C **66/66 PASS**,
    build 0 warning-uri.

- **Cheie de cache LOINC stabilă + multilingvism** (iunie 2026, detalii în
  `/app/memory/LOINC_SCALING.md` §5): prima versiune a cheii includea normalizarea engleză a lui
  Gemini, care se schimbă la fiecare rulare ⇒ hit rate **0%** și 122 de rânduri pentru 61 de
  analize. Cheia nouă = versiune + **numele tipărit în buletin** (limba nativă) + **unitatea
  canonizată** + **markerii decisivi de specimen/metodă** din contextul PDF. Vocabularul acestor
  markeri e servit de Python (`GET /loinc/context-keywords`, 160 de fraze, ~30 de limbi), luat o
  dată per proces și **persistat în tabelul `LoincVocabulary`** ca să funcționeze și când Python e
  oprit. Coloană nouă `LoincMatchCache.KeyMaterial` (diagnostic dintr-o interogare).
  Migrare: `AddLoincCacheKeyMaterialAndVocabulary`; `PipelineVersion` → `v2`.
  Testat: probă C# **55/55**, B2C **66/66**, unificator **24/24**, suita de aur LOINC **56/56**,
  cache Python **21/21**, build 0 warning-uri.

## Backlog- **P1**: validare de către utilizator a pachetului anterior (JSON repair + batch encoding LOINC);
  revenire la `PipelineMode: "split"` după validare
- **P2**: „Verdict pe axe” (Axis Verdict) în Admin Dashboard
- **P2**: buton de re-probe LOINC din UI (fără restart aplicație)
- **P4**: Integrare Stripe / Netopia
- **P4**: Deploy cloud (Azure) — serviciu LOINC ca resursă separată
