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
- **Fix „0 - 0.2” citit ca „0-2” (bazofile %)** — `Services/ReferenceRangeVerifier.cs` (nou):
  compară `reference_range` din JSON-ul Gemini cu rândul literal din textul PDF (PdfPig).
  Înlocuiește DOAR când: intervalul modelului e `X - Y`, un singur rând conține nume + valoare
  (token întreg), intervalul modelului NU e tipărit pe rând și există exact UN candidat tipărit
  „înrudit” (fiecare limită egală sau cu cifrele subșir: `2`⊂`0.2`, `75`⊂`7.5`, `2`⊂`-2`).
  Rulează înainte de `StatusValidator` în B2C (`B2cInterpretationRunner`) și B2B/CAM
  (`CamBatchService`, text extras local din bytes; log operator `CamBatchLogRangeVerifier`, 7 limbi).
  Prompt Gemini întărit: „REFERENCE RANGES ARE COPIED, NEVER RECALLED”.
  Probă: `/app/memory/probes/ReferenceRangeVerifierProbe.cs.txt` (16 cazuri incl. regresii:
  două intervale/rând, praguri `<200`, date, B-12, virgulă decimală, rând ambiguu) — ALL PASS.
- **Dedupe B2B devine PER PACIENT** (cerere utilizator): `CamBatchService.FindDuplicateAsync` face
  join cu `ClinicPatients` (NameKey + Email); același PDF byte-cu-byte pentru un pacient diferit
  (ex. override manual „CIRIP3000”) se procesează normal. Prefetch-ul paralel rezolvă pacientul
  (override sau bloc [MedicalApp]) și cheia in-batch `SeenHashes` = hash|nameKey|email.
  Probă `CamParallelPrefetchProbe` scenariu nou 8bis (8i–8l) — ALL CHECKS PASSED, build 0 warnings.
- **Raport B2C responsiv „hibrid”** (`Views/Profiles/ViewReport.cshtml`, doar CSS + `data-label` pe
  celulele Valoare/Interval): <992px tipografie fluidă `clamp()` + unitate sub valoare (fără nowrap);
  <576px fiecare analiză devine card (nume + status pe primul rând, apoi benzi „VALOARE” / „INTERVAL
  NORMAL”, etichete localizate din Loc). Eliminat `min-width: 34rem` (nu mai există scroll orizontal).
- **Fix „numele analizei + valoarea + intervalul apar de două ori”** — `Services/AnalyteLineDisplay.cs`
  (nou): `RawLineReconstructor` umple `analyte_line_raw` cu ÎNTREGUL rând literal din PDF (necesar
  matcher-ului LOINC); ecranul raport (`ProfilesController` → `AnalyteLine`) și PDF-ul
  (`PdfReportGenerator`) afișau linia brută. Acum se scot numele (case/diacritice-insensitive),
  valoarea, unitatea, intervalul/pragul și numărul de ordine; dacă nu rămân ≥3 litere descriptive,
  legenda se ascunde. Datele din DB NU se modifică. Probă `AnalyteLineDisplayProbe` — ALL PASS.
- **Comparații + Dosar Medical responsive** (doar CSS + clase/`data-label`): Compare.cshtml — <576px
  card per analiză cu un „chip” datat per interpretare (păstrează culorile risen/fallen/absent) și
  intervalul dedesubt; Dossier.cshtml — tabelul `.tl` devine card per măsurătoare (dată + status +
  tendință, laborator, benzi VALOARE / INTERVAL). Verificat mock 390/768 — 0 overflow.
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

- **Cotă Gemini gestionată + coadă durabilă** (iunie 2026, document complet în
  `/app/memory/QUOTA_AND_DURABLE_QUEUE.md`) — pașii 1 și 2 pentru 50 de utilizatori
  simultani:
  - `GeminiRateLimiter`: fereastră glisantă pe minut + plafon de apeluri simultane +
    pauză comună la 429/503 cu respectarea `Retry-After` (transportat acum prin
    `GeminiTransientException` și folosit de backoff-ul din runner). Config:
    `Gemini:RateLimit` (implicit 60 rpm / 6 simultane; `Enabled=false` = comportamentul
    vechi). **Înainte de a urca `InterpretationQueue:MaxConcurrent` peste 3, setează aici
    cota reală a contului Google.**
  - Coadă durabilă: tabel `InterpretationJobs` + `InterpretationJobStore` +
    `InterpretationJobRecoveryWorker` (scanare la 60 s). Lucrările supraviețuiesc
    restartului/deploy-ului, o instanță moartă e preluată după expirarea lease-ului de 20
    min, revendicare optimistă prin `RowVersion`, buget per instanță ca să nu monopolizeze
    coada, abandon după 3 încercări cu restituirea creditului.
    `StartupSeed.FailOrphanedInterpretationsAsync` nu mai eșuează rândurile recuperabile.
  - Migrare: `AddDurableInterpretationQueue`.
  - Testat: probă nouă **39/39**, B2C **66/66**, DI **1/1**, cache LOINC **55/55**,
    build 0 warning-uri.
  - **Corecție (după testul real)**: lease-ul a coborât de la 20 min la **2 min** cu
    heartbeat la 30 s, plus reluare imediată când `ScaleOut:Enabled = false` (o singură
    instanță) — reluarea după restart a scăzut de la **12 minute la ~10 secunde**. Reparat
    și `LoincVocabulary`: rândul nu-și mai impune `Id` (eroarea `IDENTITY_INSERT is OFF`
    împiedica persistarea vocabularului).
  - **Corecție 2 (după al doilea test real)**: `DbUpdateConcurrencyException` la finalul
    interpretării — heartbeat-ul prelungea lease-ul exact când rândul era șters. Acum
    heartbeat-ul e oprit ÎNAINTE de ștergere, iar `RenewLeaseAsync` și `RemoveAsync`
    tolerează cursa (reîncercare o dată la ștergere, ieșire silențioasă la reînnoire).
    Probă **42/42**.
  - **Rămas pentru hostare (pasul 3)**: 2+ instanțe de aplicație și 2 replici LOINC —
    doar configurație Azure, codul e pregătit.

- **Panou diagnostic infrastructură în Admin** (iunie 2026) — `GET /Admin/Diagnostics`
  (`AdminController.Diagnostics`, `Models/InfrastructureDiagnosticsViewModel.cs`,
  `Views/Admin/Diagnostics.cshtml`, link din `Views/Admin/Index.cshtml`):
  - **Cotă Gemini** (memoria instanței, prin `GeminiRateLimiter.Stats()`): apeluri în ultimul minut
    / rpm configurat, apeluri simultane permise, câte apeluri au așteptat din total, refuzuri
    Google 429/503, timp total de așteptare, badge „în pauză” la cooldown.
  - **Coadă durabilă**: la rând / în lucru (din `MaxConcurrent` sloturi) / reluate după pană /
    vechimea celei mai vechi lucrări + tabel cu ultimele 50 de lucrări (istoric, user, stare,
    încercări, instanță) și alertă pentru lease-urile expirate care vor fi recuperate.
  - **Cache LOINC**: mapări învățate pe versiunea activă a pipeline-ului, refolosiri însumate
    (analize necalculate), rânduri rămase din versiuni vechi, ultima mapare nouă, top 15 mapări
    refolosite, plus cache-ul in-process al serviciului Python (`GET /loinc/cache`: size/capacity/
    hit rate) și starea vocabularului salvat. Serviciul Python oprit ⇒ mesaj clar, pagina NU cade.
  - Reîmprospătare **manuală** (buton `diag-refresh-btn`, decizia utilizatorului — fără polling).
  - Testat: probă nouă `/app/memory/probes/DiagnosticsPanelProbe.cs.txt` (proiect `/app/probe_diag`)
    — **13/13 PASS** (cotă reală, refuz Google, coadă cu lease expirat, filtrare pe versiunea de
    pipeline, top mapări, vocabular, serviciu Python picat). Build `MedicalApp`: **0 warning-uri**
    (CS0414 nu mai apare).

- **Audit indexuri SQL (pregătire hostare, pasul 4)** — iunie 2026, migrare `AddScaleOutIndexes`,
  document complet în **`/app/memory/SQL_INDEXES.md`**:
  - `InterpretationHistories`: `CreatedAt DESC` adăugat în cheia `(UserEmail, ProfileId, Status)`
    (arhiva/graficele/comparațiile nu mai sortează tot profilul); index acoperitor nou
    `(UserEmail, Id DESC) INCLUDE (Status)` pentru pastila de job (cea mai frecventă interogare);
    `(Status, Id DESC) INCLUDE (DurationMs)` pentru scanarea „processing” + ETA;
    `(ProfileId, Status)` pentru numărătoarea per profil; **șters** indexul redundant pe `UserEmail`.
  - `AiUsageLogs`: un singur `(CreatedAt, Status) INCLUDE (Source, ModelUsed, InputTokens, OutputTokens)`;
    **șterse** cele trei indexuri pe o coloană (nefolosite ca primă coloană, costau o scriere după
    fiecare apel Gemini).
  - `Purchases`: `PurchasedAt INCLUDE (AmountEur)` ⇒ cifrele de venit devin index-only.
  - `ClinicAnalyses`: nou `(ClinicId, ProcessedAt) INCLUDE (PatientId)` pentru CAM Dashboard;
    **șters** indexul redundant pe `ClinicId`.
  - Testat: probă nouă `/app/memory/probes/SqlIndexAuditProbe.cs.txt` (proiect `/app/probe_indexes`)
    — **17/17 PASS** (potrivire exactă cheie + direcție + coloane incluse pentru fiecare interogare
    fierbinte, dispariția indexurilor redundante, nicio cheie pe `nvarchar(max)`, limita de 1700 B);
    `dotnet ef migrations has-pending-model-changes` ⇒ „No changes”; build 0 warning-uri.
    Script T-SQL de deploy: `/app/memory/probes/AddScaleOutIndexes.sql`.

- **Buton „Înregistrare/Autentificare” pe landing** (iunie 2026): butonul din dreapta sus
  (`data-testid="land-signin"`, cheia `NavSignIn`) deschidea deja panoul cu AMBELE taburi
  (Autentificare + Înregistrare), dar se numea doar „Autentificare”. Redenumit în toate cele
  **7 limbi** (`Sign up / Sign in`, `Înregistrare/Autentificare`, `Inscription / Connexion`,
  `Registro / Iniciar sesión`, `Registrieren / Anmelden`, `Registrati / Accedi`,
  `Registo / Iniciar sessão`). Fiind `white-space: nowrap`, eticheta mai lungă risca să împingă
  navbarul în scroll orizontal pe telefon ⇒ în `landing.css`, sub 768 px pastilele de acțiune au
  font/padding reduse și rândul are `flex-wrap: wrap`. Verificat cu screenshot pe 390 px și 1920 px:
  **zero overflow orizontal** (`scrollWidth == 390`).

- **Plafon de 20 de profile per cont B2C** (iunie 2026, cerere utilizator — siguranță în
  funcționare): `ProfileGateService.MaxProfilesPerUser = 20` + `IsAtProfileLimit()`;
  `CanCreateAdditionalProfile()` refuză la plafon **chiar și cu credite plătite**.
  - Profilele existente NU sunt atinse: un cont care are deja peste 20 le păstrează pe toate,
    doar crearea unuia nou e refuzată (clauză de „grandfathering”).
  - Refuzul e aplicat server-side în `ProfilesController.Create` (GET **și** POST), deci nu se
    poate ocoli cu un POST direct; mesajul diferă după motiv: plafon atins vs. lipsa creditelor
    plătite (regula veche, neschimbată sub plafon).
  - UI: `ViewBag.ProfileLimitReached` duce în `Views/Profiles/Index.cshtml` (banner de avertizare
    + buton blocat `btn-create-profile-maxed`) și în `Views/Interpretation/Upload.cshtml`
    (`interpret-add-profile-btn-maxed`). La plafon butonul NU mai trimite la /Credits — plata nu
    schimbă nimic.
  - Chei noi în **7 limbi**: `ProfileLimitReached`, `ProfileLimitTooltip` (ambele cu `{0}` = 20).
  - Clinicile (CAM/B2B) nu sunt afectate.
  - Testat: probă nouă `/app/memory/probes/ProfileLimitProbe.cs.txt` (proiect
    `/app/probe_profile_limit`) — **18/18 PASS** (regula, POST-ul real refuzat fără scriere în DB,
    creare permisă la 19, contul „grandfathered” intact, mesajul corect pe fiecare motiv,
    ViewBag-urile, cele 7 traduceri cu placeholder). Build 0 warning-uri.

- **Contor „X profile create — max 20”** (iunie 2026): partial nou
  `Views/Shared/_ProfileQuotaBadge.cshtml`, randat pe pagina Profile (lângă titlu) și pe ecranul
  de upload (sub selectorul de profil). Afișat **mereu**, dar numai pentru conturile cărora li se
  aplică plafonul (`ProfileGateService.IsCapped` ⇒ doar „Individual”), ca un cont de Clinic/Admin
  să nu vadă „28 din 20”. Culori: neutru sub 18, galben la 18-19, roșu la 20+. Cheie nouă
  `ProfileQuotaBadge` în **7 limbi** (două placeholdere: folosite/maxim).
  Clarificare: plafonul rămâne, prin decizia utilizatorului, **doar pe B2C („Individual”)** — de
  aceea un cont de tip Clinic (inclusiv adminul) poate depăși 20 de profile.
  Testat: probă extinsă `/app/memory/probes/ProfileLimitProbe.cs.txt` — **21/21 PASS**.

- **Prețuri B2C recalibrate** (iunie 2026, `Services/CreditPackages.cs`): Super **50 → 39 EUR**
  (18 credite ⇒ 2,17 €/credit), Premium **100 → 89 EUR cu 45 credite** în loc de 38
  (⇒ 1,98 €/credit). Normal (6 €/2) și Standard (11 €/4) neschimbate; pachetele CAM neatinse.
  `Views/Credits/Buy.cshtml` calculează prețul/credit din pachet, deci nu a fost nevoie de
  modificări în view sau traduceri. Achizițiile vechi rămân corecte (Purchases păstrează sumele
  la momentul cumpărării).

- **Afișare progresivă a interpretării B2C** (iunie 2026):
  - `InterpretationProgressTracker` publică acum secțiuni, nu doar etape: `SetPatient()`,
    `SetNarrative()` (rezumat + recomandări + valori anormale), `UpdateTable()` (reîmprospătează
    tabelul fără să dea etapa în urmă). `Get()` citește local, iar dacă tokenul e al altei
    instanțe, din `IDistributedCache`.
  - **Scale-out**: fiecare mutație se scrie write-through în `IDistributedCache` (în producție
    tabelul de cache SQL configurat de ScaleOut; local `AddDistributedMemoryCache`), altfel un
    poll trimis de load balancer altei instanțe ar arăta ecranul înghețat. **Zero modificări de
    schemă, zero migrări, zero coloane noi.**
  - `GeminiMedicalInterpretationService`: hook nou `OnPartialResult`, invocat cu „extract”
    (date pacient + tabel, la finalul etapei A) și „narrative” (rezumat + recomandări, în clipa
    în care răspunde etapa C — fără să aștepte etapa B).
  - `B2cInterpretationRunner` leagă hook-urile și republică secțiunile după pasajele locale
    (StatusValidator / AbnormalFindingsCompleter), astfel încât valorile preliminare să nu
    contrazică raportul final. Pe calea monolitică publică tot imediat după răspunsul AI.
  - `GET /Interpretation/Progress` întoarce `patient`, `summary`, `recommendations`, `findings`.
  - Overlay-ul din `Views/Interpretation/Upload.cshtml` are 4 secțiuni care se completează pe rând
    (date pacient / analize citite / valori în afara normalului / rezumat + recomandări), fiecare
    randată doar când apare și repictată doar când se schimbă. Chei noi în **7 limbi**:
    `ProgressSectionPatient`, `ProgressSectionAnalytes`, `ProgressSectionOutOfRange`,
    `ProgressSectionSummary`, `ProgressSectionRecommendations`, `ProgressSectionPending`.
  - Testat: `GeminiSplitPipelineProbe` extinsă (49 checks, inclusiv ordinea extract → narrative →
    final și partajarea între instanțe) și suita B2C `bg_interpretation` extinsă (**80/80**, cu
    pipeline-ul blocat pe pasul de email pentru a inspecta exact ce vede utilizatorul la 2/3 din
    drum). Regresie verde: DI 10/10 controllere, plafon profile 21/21, indexuri 17/17, panou
    diagnostic 13/13, cache LOINC, unificator LOINC. ScaleOutProbe: 2 eșecuri strict de mediu
    (Azurite nu rulează în container). Layout verificat prin screenshot la 390 px și 1920 px.

- **Tip de cont nou „Cabinet Medical” (CM) — etapa 1** (iunie 2026, plan în `/app/plan/plan.md`):
  - `Services/AccountTypes.cs` (nou): cele trei tipuri într-un singur loc — `Individual`,
    `Clinic`, `Cabinet`; `Normalize()` duce orice valoare necunoscută la `Individual`.
  - **Înregistrare**: a treia opțiune „Cabinet Medical”, cu un singur câmp suplimentar
    obligatoriu — **Numele cabinetului** (max 150). Fluxul „interpretare gratuită” (`flow=free`)
    continuă să forțeze `Individual`, deci nu se poate crea un cabinet pe acolo.
  - **După confirmarea emailului**: 1 credit bonus (ca la B2C) și aterizare direct pe ecranul de
    interpretare. Nu se creează rând în `Clinics` și nu se folosesc ecranele CAM.
  - **Preț**: pachet propriu, separat de B2C — `cabinet_premium`, **89 € = 45 credite**. Cabinetul
    nu vede pachetele B2C sau CAM. Nou: `Checkout` (GET și POST) refuză un pachet care nu aparține
    tipului de cont, deci nimeni nu mai poate cumpăra alt pachet scriind URL-ul.
  - **Plafon**: 2000 de pacienți (`MaxProfilesPerCabinet`), B2C rămâne la 20, clinicile
    neplafonate. Regula „profile suplimentare doar cu credite plătite” se aplică și cabinetului
    (decizia utilizatorului). Contorul devine „X profile create — max 2000”.
  - **Blurare**: neschimbată — raportul pe creditul bonus e blurat până la prima achiziție, iar la
    prima achiziție se debluează retroactiv și se trimite pe email (mecanismul B2C existent, care
    exclude doar clinicile).
  - **Admin**: badge verde „Cabinet” cu numele cabinetului + filtru nou „Cabinets” în lista de
    utilizatori. NU s-a adăugat migrarea manuală a conturilor existente (decizia utilizatorului).
  - Chei noi în **7 limbi**: `UserTypeCabinet`, `CabinetNameLabel`, `CabinetNamePlaceholder`,
    `CabinetNameRequired`, `RegisterCabinetNote`, `PackageCabinetPremium`.
  - **DB**: migrare `AddCabinetAccountType` — o singură coloană nouă, `Users.CabinetName`
    `nvarchar(150) NULL`. Aditivă, reversibilă, fără atingerea datelor existente.
  - Testat: probă nouă `/app/memory/probes/CabinetAccountProbe.cs.txt` (proiect
    `/app/probe_cabinet`) — **42/42 PASS** (înregistrare completă până la cont creat, validarea
    numelui, fluxul free forțat pe Individual, pachetul unic, refuzul cumpărării unui pachet
    străin, achiziția reală cu deblurare retroactivă, plafonul 1999/2000/2500, regula creditelor,
    ViewBag-urile pentru contor, cele 7 limbi, filtrele din admin, plus regresii B2C și CAM).
    Regresie verde: B2C 80/80, split 49/49, plafon profile 21/21, DI, indexuri 17/17, diagnostic
    13/13; `has-pending-model-changes` ⇒ „No changes”; build 0 warning-uri; UI verificat la 390 px
    și 1920 px.
  - **Etapa 2 rămasă** (cerută de utilizator): căutare + paginare pe pagina de profile și selector
    de pacient cu căutare în ecranul de interpretare, pentru volume de ordinul a 2000 de pacienți.

- **Cabinet Medical — etapa 2: căutare + paginare pacienți** (iunie 2026):
  - `/Profiles` pentru conturile Cabinet: căutare **pe server** (nume SAU notițe, case-insensitive)
    și paginare **24 pacienți/pagină** (`ProfilesController.Index(q, page)`, `PatientsPerPage`).
    Pagina cerută e limitată în interval, deci `?page=99` aterizează pe ultima pagină, nu pe un
    ecran gol. Contorul „X profile create — max 2000”, plafonul și gate-ul creditelor folosesc
    **totalul deținut**, niciodată rândurile paginii curente.
  - **B2C rămâne identic**: `IsPaged = false`, o singură interogare, filtrul instant din browser
    exact ca înainte (verificat prin test de regresie cu 30 de profile).
  - Ecranul de interpretare pentru Cabinet: **selector de pacient cu căutare**
    (`GET /Interpretation/SearchProfiles?q=` → maxim 20 potriviri, doar pacienții contului),
    debounce 250 ms, navigare cu ↑/↓/Enter/Escape, închidere la click în afară. Lista nu se mai
    încarcă cu 2000 de nume: se face seed cu 24, iar la revenirea din eroare de validare pacientul
    ales e păstrat în listă chiar dacă nu e în primele 24. B2C păstrează `<select>`-ul clasic.
  - Chei noi în **7 limbi**: `ProfilesSearchPlaceholderCabinet`, `ProfilesSearchSubmit`,
    `ProfilesSearchResults`, `ProfilesSearchEmpty`, `ProfilesPagerInfo`, `ProfilesPagerPrev`,
    `ProfilesPagerNext`, `InterpretPatientSearchPlaceholder`, `InterpretPatientSearchNoResults`.
  - **Zero modificări în baza de date.** (Indexul `(UserEmail)` pe `Profiles` acoperă deja
    filtrarea; căutarea în notițe rămâne un scan pe cele ≤2000 de rânduri ale contului.)
  - Testat: `CabinetAccountProbe` extinsă — **57/57 PASS** (paginare 24/24/12, fără duplicate
    între pagini, page clamp, căutare pe nume și pe notiță „fisa 12345”, case-insensitive, zero
    rezultate, B2C nepaginat, endpointul de căutare: limita de 20, căutare în notițe, refuz pentru
    anonimi, izolare între conturi). Regresie verde: B2C 80/80, split 49/49, plafon 21/21, DI,
    indexuri; build 0 warning-uri; UI verificat la 390 px și 1920 px, fără overflow.

- **Perioada gratuită pentru funcțiile avansate: 1 an → 3 ani** (iunie 2026, cerere utilizator):
  `ArchiveAccessService.FreeYears = 3` + `FreeUntilFrom(dataÎnscrierii)` (folosește `AddYears`,
  deci cade pe aceeași zi calendaristică, inclusiv la 29 februarie). Înlocuiește
  `FreePeriod = 365 zile` în toate locurile (înregistrare, seed, ecranul de istoric).
  - **Conturile existente sunt extinse automat** la pornirea aplicației
    (`StartupSeed.EnsureFreeArchiveUntilAsync`): orice cont cu `FreeArchiveUntil` mai mic decât
    `DataC + 3 ani` primește data nouă, calculată **de la data înscrierii**. Nimeni nu e scurtat
    (o perioadă de curtoazie mai lungă rămâne intactă), iar rularea repetată nu schimbă nimic.
  - După expirare rămâne neschimbată regula 3 utilizări/credit.
  - Mesajul din ecranul de istoric actualizat în **7 limbi** („3 ani de la înregistrare”).
  - **Zero modificări de schemă**: coloana `Users.FreeArchiveUntil` exista deja; se schimbă doar
    valorile, la primul start al aplicației.
  - Testat: probă nouă `/app/memory/probes/FreePeriodProbe.cs.txt` (proiect `/app/probe_freeperiod`)
    — **16/16 PASS** (perioada, 29 februarie, un cont de 2 ani care înainte plătea și acum are
    gratuit, expirarea la 3 ani + 1 zi, regula 3/credit după expirare, extinderea conturilor vechi
    și a rândurilor fără dată, interzicerea scurtării, idempotența seed-ului, cele 7 limbi).
    Regresie verde: B2C 80/80, cabinet 57/57. Build 0 warning-uri.

- **Acoperire traduceri 100% pe toate cele 7 limbi** (iunie 2026): IT și PT aveau 1093/1111 chei
  (98,4%). Cele 18 chei lipsă — rămase din lucrările la coada de fundal, indicatorul de job și
  avertismentul LOINC offline (`AdminQueue*`, `JobIndicator*`, `HistoryStatus*`,
  `Interpretation*Background`, `LoincOfflineWarning*`, `LeaveInBackgroundBtn`) — au fost traduse în
  italiană și portugheză. Acum toate cele 6 limbi non-EN au **1111/1111**.
  Testat: probă nouă `/app/memory/probes/TranslationCoverageProbe.cs.txt` (proiect `/app/probe_i18n`)
  — **30/30 PASS**: zero chei lipsă, zero chei „drift” pe care EN nu le are, zero traduceri goale
  și **placeholderele `{0}`/`{1}` identice cu EN** în fiecare limbă (o cheie cu placeholder greșit
  ar arunca excepție la `string.Format` în producție). Build 0 warning-uri.

- **Pregătire găzduire Azure: procesarea grea scoasă din request-uri** (iunie 2026, analiză +
  implementare; document complet în **`memory/AZURE_HOSTING.md`**):
  - **CAM: loturile nu mai pornesc cu `Task.Run` din request.** Butonul „Start” scrie rândul cu
    `Status="Queued"` + limba operatorului; `CamBatchQueueWorker` (orice instanță) revendică cu
    lease de 3 min, heartbeat 30 s, publică un snapshot de progres în `IDistributedCache` la 3 s,
    iar **Cancel** merge prin rând (`CancelRequested`), deci funcționează și de pe altă instanță.
    Garda „un lot per clinică” e acum în baza de date. `CamBatchQueueStore` + `CamBatchQueueWorker`.
  - **Bug multi-instanță reparat**: `StartupSeed.FailOrphanedBatchesAsync` marca TOATE loturile
    `Running` ca `Failed` la pornire — pe Azure, a doua instanță omora lotul care rula pe prima.
    Acum decide după lease; rândurile `Queued` nu se ating. Decizia de business (fără auto-resume
    pentru loturi întrerupte, ca să nu se retrimită emailuri) rămâne.
  - **O interpretare per utilizator, verificată în baza de date**
    (`InterpretationJobStore.HasActiveForUserAsync`): înainte, verificarea era doar în memoria
    instanței, deci un user putea porni două interpretări simultan de pe două instanțe.
  - **Cota Gemini împărțită pe instanțe**: `Gemini:RateLimit:InstanceCount` ⇒ fiecare instanță
    folosește `RequestsPerMinute / InstanceCount` și `MaxConcurrentCalls / InstanceCount`
    (minim 1). Panoul de diagnostic arată cota instanței + cota totală.
  - **Emailul în masă din Admin mutat în fundal**: bucla era în request și depășea limita Azure de
    230 s la câteva sute de destinatari. Acum: tabel nou `BulkEmailJobs` (queued → running → done),
    `BulkEmailWorker` cu lease, `NextIndex` (reluare exact de unde a rămas, fără duplicate),
    contoare `Sent`/`Failed`/`LastError` și tabel de progres în ecranul Admin.
  - **`GET /healthz`** (text `ok`, fără atingere de SQL/Gemini/Blob) pentru Health check-ul Azure.
  - **DB**: migrare `AddAzureBackgroundQueues` — tabel nou `BulkEmailJobs` + 6 coloane pe
    `ClinicBatchRuns` (`LanguageCode`, `OwnerInstance`, `LeaseUntil`, `Attempts`,
    `CancelRequested`, `RowVersion`). Strict aditivă.
  - Verificat și ce NU trebuia schimbat: nicio blocare sync-over-async pe calea unui request
    (`.Result` apare doar pe task-uri deja finalizate după `Task.WhenAll`).
  - Testat: probă nouă `/app/memory/probes/AzureHostingProbe.cs.txt` (proiect `/app/probe_azure`)
    — **40/40 PASS** (lease/claim/renew/release CAM, lotul instanței vii neatins, lotul instanței
    moarte închis, cancel prin rând, sweep-ul multi-instanță, garda per utilizator, împărțirea
    cotei Gemini + limitarea reală la 1 apel, emailul în masă: coadă, trimitere, filtre, reluare
    din `NextIndex`, owner viu neatins, destinatar cu eroare, audiență goală refuzată).
    Regresie verde: B2C 80/80, cabinet 57/57, DI 10/10, scale-out (mai puțin 2 eșecuri de mediu —
    Azurite nu rulează în container). `has-pending-model-changes` ⇒ „No changes”; build 0 warning-uri.

## Backlog
- **Configurație de deploy pregătită** (13 iunie 2026): `MedicalApp/appsettings.Azure.json` +
  ghidul `memory/AZURE_APP_SETTINGS.md` (copie în `MedicalApp/Docs/`).
  - Fișierul este **suprapunere**, nu înlocuire: se citește doar când
    `ASPNETCORE_ENVIRONMENT = Azure`, peste `appsettings.json`, iar Application settings din
    portal bat ambele. Local (`Development`) nu e citit niciodată.
  - Conține: `Storage = Blob` pentru CAM, `LoincAutoStart = false`, `AttachDebugJson = false`,
    timeout LOINC 10 s, loguri pe `Warning` (aplicația pe `Information`), plafoanele Gemini și
    cozile — cu **toate secretele goale**, de completat în portal.
  - Testat: probă nouă `/app/memory/probes/AzureAppSettingsProbe.cs.txt` (proiect
    `/app/probe_cfg`) — **17/17 PASS**; `dotnet publish` confirmă că fișierul ajunge în pachet.
- **P0 raportat de utilizator și REPARAT** (13 iunie 2026): `DbUpdateConcurrencyException` la
  primul fișier al unui lot CAM.
  - Cauză reală: lease-ul cozii stătea pe rândul `ClinicBatchRuns` împreună cu un
    `[Timestamp] RowVersion`. `CamBatchService` ține rândul lotului atașat în DbContext-ul lui
    pe toată durata rulării și îi salvează contoarele după fiecare fișier, în timp ce
    `CamBatchQueueWorker.KeepAliveAsync` reînnoia lease-ul **din alt DbContext** → prima
    reînnoire schimba `RowVersion`, deci salvarea runner-ului nu mai găsea rândul.
  - Fix: lock-ul s-a mutat în tabel propriu **`ClinicBatchClaims`** (`BatchRunId` = PK →
    INSERT-ul *este* lock-ul, atomic între instanțe); `ClinicBatchRun` **nu mai are** niciun
    token de concurență (`RowVersion` + `LeaseUntil` eliminate).
    `RenewLeaseAsync`/`ReleaseAsync`/`FailAbandonedAsync` și
    `StartupSeed.FailOrphanedBatchesAsync` lucrează exclusiv pe claim-uri.
  - Reparat și ce lăsase sesiunea precedentă necompilabil: `ClinicBatchClaim.cs` trunchiat
    (CS1022), `CamBatchQueueStore.RenewInterval` lipsă, `StartupSeed` pe `batch.LeaseUntil`.
  - **DB**: migrare **`AddCamBatchClaim`** (creează `ClinicBatchClaims` + index pe `LeaseUntil`,
    șterge `LeaseUntil` și `RowVersion` din `ClinicBatchRuns`). Pentru aplicare manuală în SSMS:
    `memory/probes/AddCamBatchClaim.sql` (idempotent, copie și în `MedicalApp/Docs/`).
    **Necesită `Update-Database` local înainte de a relansa un lot.**
  - Testat: `probe_azure` extins cu checkurile 6c-6i (claim atomic — a doua instanță pierde și nu
    atinge rândul; zero token de concurență pe `ClinicBatchRun`; 4 salvări de contoare intercalate
    cu heartbeat din alt DbContext, fără excepție; Release nu mai trece un lot `Completed` pe
    `Failed`) — **ALL CHECKS PASSED**; build 0 erori / 0 warning-uri; verificat independent de
    testing agent (`/app/test_reports/iteration_21.json`, backend 100%).
- **P0 Paralelizare lot CAM — IMPLEMENTAT** (14 iunie 2026), aprobat de utilizator după analiza
  tabelului Admin/Performance (Gemini = 89% din timp; thinking = 70-80% din tokenii out;
  Tier 1 = 1M TPM ⇒ cota Google NU e gâtuirea, ci procesarea secvențială din aplicație:
  25 fișiere × 95 s ≈ 40 min).
  - Setare nouă **`CamSettings:MaxParallelFiles`** (default cod **1** = comportament identic cu
    înainte; appsettings.json local = **4** după validarea utilizatorului: 10 fișiere 16 min → 3,35 min; Azure json: 4). Doar apelul Gemini al următoarelor N-1 fișiere pornește în avans
    (`GeminiPrefetch` în `CamBatchService`); bucla rămâne strict în ordine, cu același DbContext:
    contoare, credite, upsert pacient, PDF comparație și email — semantica secvențială păstrată.
  - Fiecare prefetch are DI scope propriu (DbContext + provider Gemini nu sunt thread-safe);
    eligibilitate identică pasului 1 (override sau bloc [MedicalApp]); plafon = creditele de la
    startul lotului (niciodată mai multe apeluri AI decât credite); eșecul definitiv al
    prefetch-ului NU se reîncearcă inline (7 încercări o singură dată); orice excepție a
    prefetch-ului ⇒ fallback transparent pe apelul inline (comportamentul vechi).
  - Chei Loc noi (7 limbi): `CamBatchLogParallelMode`, `CamBatchLogPrefetchStarted`.
  - `appsettings.Azure.json`: `Gemini.RateLimit` 300 RPM / 20 apeluri concurente (calibrat
    Tier 1), `CamSettings.MaxParallelFiles = 4`; documentat în `AZURE_APP_SETTINGS.md`.
  - Testat: probă nouă `/app/probe_cam_parallel` (copie `memory/probes/CamParallelPrefetchProbe.cs.txt`):
    7 scenarii / 30 checkuri — secvențial identic (max 1 în aer), paralel 4 (max 4 în aer,
    emailuri în ordine, 8 apeluri exact, 2,7× mai rapid), plafon credite (3 credite ⇒ 3 apeluri),
    eșec Gemini pe un fișier (1 singur apel, NotSends=1), fișier neeligibil (0 apeluri AI),
    același pacient în 2 fișiere (1 PDF comparație, fără pacient duplicat), anulare în timpul
    prefetch-ului (contoare consistente) — **ALL CHECKS PASSED**; build 0 erori / 0 warning-uri.
  - **Utilizatorul validează local** (VS2026): întâi cu `MaxParallelFiles: 1`, apoi 4.
- **Stripe Checkout — IMPLEMENTAT** (15 iunie 2026; decizii utilizator: plată per pachet, EUR, promo-uri ale
  noastre, chitanță Stripe automată). Detalii complete în `memory/STRIPE_PAYMENTS.md`. Pe scurt: `Stripe.net 52.4.2`;
  `PaymentSettings` (`Payments:Provider` Stripe|Simulated); `StripePaymentService` (sesiune, verificare, marcare
  idempotentă cu RowVersion, parsare webhook); `CreditsController`: `StartStripe` / `StripeSuccess` / `StripeCancel` /
  `StripeWebhook`, iar creditarea comună extrasă în `FulfillPurchaseAsync` (folosită și de providerul Simulated —
  comportament identic). Tabel nou `PaymentTransactions` + coloană `Purchases.ProviderReference` — migrare
  **`AddPaymentTransactions`** (utilizatorul rulează `Update-Database`). Checkout.cshtml: buton Stripe când
  `UseStripe`, formularul vechi doar pentru Simulated. Chei Loc `PaymentStripe*` (7 limbi). Sandbox Stripe RO
  provizionat (Flow A, claimable); cheile de test merg în User Secrets local. Probă 26 checkuri ALL PASSED.
  **Utilizatorul validează local cu cardul 4242.** Urmează (backlog): abonamente, facturare fiscală RO.
- **Retușuri PDF comparație + Note profil** (15 iunie 2026): antetul PDF-urilor de comparație afișează mai mare (12pt,
  bold albastru) proprietarul: CAM „Clinică: <nume>”; B2C „Cont: <email> · Profil: <nume>”; CM „Cabinet: <CabinetName> ·
  Profil: <nume>” (`ProfileComparePdfGenerator.Generate(profile, vm, owner)`, chei Loc `ProfileCompareAccountLabel /
  CabinetLabel / ProfileLabel`), plus `WWW.MyMedicalApp.NET` în antet și în footer (constanta `PdfBranding.Website`,
  înlocuiește „medicalapp.ro” și în sumarul CAM). `Profile.Notes` 500 → **4000** caractere (`Profile.MaxNotesLength`,
  textarea 8 rânduri + contor), migrare **`WidenProfileNotes`**. Probă `memory/probes/ComparePdfHeaderProbe.cs.txt` 9/9 PASS.
- **Evidență pe limbi la înregistrare** (15 iunie 2026): `Users.RegistrationLanguage` (nvarchar(5), null pentru conturile
  vechi — rămân „necunoscută”, decizia utilizatorului), setat la `AccountController.Register` din
  `CultureInfo.CurrentUICulture` (cookie de limbă → altfel Accept-Language al browserului → altfel „en”).
  Admin → Users: coloană **Limba** (badge RO/EN/…) după Type. Admin → buton **Struct Limbi** (`/Admin/LanguageStats`):
  total / cunoscute / necunoscute / limbi distincte + tabel limbă, nr., % din cunoscuți, % din total, bară.
  Migrare **`AddUserRegistrationLanguage`**. Analiza pe **țară** (GeoLite2 IP + țara cardului Stripe) — propusă, amânată.
- **P1**: poziție în coadă + ETA în UI-ul CAM (aprobat în principiu, amânat de utilizator).
- **Pagina CAM „Fișierele mele” — IMPLEMENTAT** (14 iunie 2026). Înlocuitorul Windows Explorer
  pentru cele 4 foldere, necesar pe Azure (cabinetul nu vede discul serverului) și pentru orice
  cabinet real. Totul prin `ICamFileStore` ⇒ identic pe disc local și pe Blob; zero atingere la
  procesare / DB schema.
  - `Areas/CAM/Controllers/FilesController.cs` + `Views/Files/Index.cshtml` + `Models/CamFilesViewModel.cs`:
    tab-uri Original / Sends / Sumar / Errors cu contoare; upload drag-and-drop **un fișier per
    request** (XHR, bară de progres, antiforgery prin header, 50 MB/fișier, doar .pdf, nume
    dezambiguizat de store); Descarcă / **Descarcă tot (ZIP, streamed)**; Șterge (doar Original —
    șterge și override-ul — și Errors — șterge și `.reasons.txt`); **Repune în Original** din
    Errors; coloana Motiv la Errors (din `ClinicBatchErrors` potrivit pe nume, fallback
    `.reasons.txt`); `.reasons.txt` ascunse din listă; Sends/Sumar read-only; izolare per clinică
    din sesiune; nume de fișier doar „bare” (traversal respins).
  - Dashboard: buton „Fișiere” în acțiuni rapide + „Deschide” pe fiecare rând din panoul Foldere.
  - Chei Loc noi (7 limbi): `CamFiles*`, `CamDashGotoFiles`, `CamDashFolderOpen`.
  - `CheckPdfs` (upload vechi, verificare, override) **neschimbat**.
  - Testat: probă `/app/probe_cam_files` (copie `memory/probes/CamFilesControllerProbe.cs.txt`),
    41 checkuri **ALL PASSED**; build 0 erori / 0 warning-uri. **Utilizatorul validează local.**
- **Unificare într-o singură pagină „Fișiere PDF”** (14 iunie 2026, decizie utilizator după test):
  „Selectare PDF-uri” a dispărut din Dashboard; tab-ul **De trimis** (Original) randează masa de
  lucru CheckPdfs (verificare identitate + email, Editează/override, Șterge, Lansează lot → pagina
  de confirmare) prin partialul `Views/Files/_OriginalWorkbench.cshtml`, alimentat de serviciul nou
  `Services/CamCheckPdfsBuilder.cs` (logica extrasă 1:1 din `CheckPdfsController.Index`).
  `CheckPdfsController`: GET Index → redirect la Files/Original; POST-urile (SaveOverride,
  ClearOverride, DeletePdf, SaveBlacklist, UploadFiles) neschimbate, redirecționează la Files/Original.
  Vechea view `CheckPdfs/Index.cshtml` ștearsă. Tab-uri în limbaj de cabinet: **De trimis / Trimise /
  Sumare / Netrimise** (`CamFilesTab*`), numele tehnic al folderului doar în linia „Locația de stocare”
  și în panoul Foldere. Ajutorul (metodele 1/2) e pliat sub „Cum pregătesc PDF-urile?”.
  Dashboard: acțiuni rapide = Lansează lot · Fișiere PDF · Pacienți; butonul „Corectează” de la
  problemele de email duce la Files/Original. Probă extinsă la 48 checkuri **ALL PASSED**; build 0/0.
- **Istoric + comparații + dedupe** (14 iunie 2026, cerute de utilizator):
  - B2C/CM: plafon **24** interpretări reușite per (user, profil) — `B2cInterpretationRunner.MaxHistoryPerProfile`,
    pruning după fiecare succes (cele mai vechi după `CreatedAt`; rândurile pending/error nu sunt atinse).
  - B2C/CM „Compară interpretări”: **2–6** selecții (`CompareInterpretationsViewModel.MaxSelections = 6`);
    JS-ul din History citește constanta; PDF-ul de comparație (A4 landscape) umple până la 6 coloane;
    grila de carduri trece pe `col-xl-2` peste 4 coloane; textele `HistoryCompareHint`/`Feat4Body` 4→6 (7 limbi).
  - B2B: **6** analize păstrate per pacient (`CamBatchService.MaxAnalysesPerPatient`), regula rămâne „cele mai
    recente după data recoltării” (nu FIFO după sosire); PDF-ul de comparație CAM până la 6 coloane.
  - B2B dedupe **byte-cu-byte**: coloană nouă `ClinicAnalyses.PdfSha256` (+ index `IX_ClinicAnalyses_Clinic_PdfSha256`),
    migrare **`AddClinicAnalysisPdfSha256`** (utilizatorul rulează `Update-Database`). Înainte de orice apel AI,
    hash-ul PDF-ului e căutat în analizele clinicii: dacă există ⇒ omis (fără cost AI, fără credit), NotSend cu
    motiv „Duplicate PDF: identical to 'X' processed <data>”, mutat imediat în Errors (+ `.reasons.txt`,
    header `CamBatchDuplicateHeader`). Prefetch-ul paralel sare și el duplicatele (DB + `SeenHashes` în lot).
    Rândurile vechi (hash null) nu se potrivesc niciodată.
  - Testat: probă paralelizare extinsă la 10 scenarii / **44 checkuri ALL PASSED** (dedupe în lot fără apel AI,
    retenție 6 după data recoltării, rând legacy fără hash); build 0/0. Plafonul B2C 24 verificat prin citire
    (nu are probă — runner-ul B2C are prea multe dependențe), **de validat local**.
- **P2 (experiment)**: `ThinkingBudget` configurabil pentru modul monolitic (azi -1 dinamic) —
  potențial −30-40% timp/cost per fișier; de comparat calitatea pe 2-3 buletine cunoscute.
- **P2**: 2-3 loturi CAM simultane per instanță (după validarea paralelizării).
- **P1**: validare de către utilizator a pachetului anterior (JSON repair + batch encoding LOINC);
  revenire la `PipelineMode: "split"` după validare
- **P2**: „Verdict pe axe” (Axis Verdict) în Admin Dashboard
- **P2**: buton de re-probe LOINC din UI (fără restart aplicație)
- **P4**: Integrare Stripe / Netopia
- **P4**: Deploy cloud (Azure) — serviciu LOINC ca resursă separată

---

## 2026-06 — Containerizare Docker (Pasul 2: fișiere pregătite)

**Context**: utilizatorul a instalat cu succes WSL 2 + Docker Desktop 29.8.0 pe Windows 11
(`docker run hello-world` → OK). Decizii luate: bază nouă goală în container, toate cele 3
servicii în compose, repo local `C:\Projects\MedicalApp-repo`, secrete în `.env` local.

**Implementat (necesită rulare/validare pe mașina utilizatorului — Docker nu există în podul Emergent)**:
- `MedicalApp/Dockerfile` — multi-stage .NET 9 SDK → aspnet runtime; `libfontconfig1` +
  `fonts-liberation` pentru QuestPDF pe Linux; ascultă pe 8080.
- `MedicalApp/.dockerignore`, `loinc_service/.dockerignore`.
- `loinc_service/Dockerfile` — python:3.11-slim, **torch CPU-only** (evită ~2.5 GB CUDA),
  modelul `all-MiniLM-L6-v2` pre-descărcat în imagine, uvicorn 0.0.0.0:8000, healthcheck `/health`.
- `docker-compose.yml` — servicii `sql` (mssql 2022, PID Developer, port 14330→1433, volum
  `mma-sqldata`, healthcheck sqlcmd), `loinc`, `app` (8080, volum `mma-files`, `depends_on`
  sql healthy). Secretele injectate din `.env` prin variabile `Section__Key`.
- `.env.example` (versionat prin `!.env.example` în `.gitignore`).
- `MedicalApp/appsettings.Docker.json` — `LoincMatcher:BaseUrl=http://loinc:8000`,
  `CamSettings:FilesRoot=/app/files`, `LoincAutoStart:Enabled=false`,
  `Hosting:UseHttpsRedirection=false`, `Database:AutoMigrate=true`.
- `Program.cs` — 2 comutatoare noi, ambele default = comportamentul vechi:
  `Database:AutoMigrate` (aplică migrările EF la pornire) și `Hosting:UseHttpsRedirection`.
- Build verificat: **0 warnings / 0 errors**.
- Documentație: `/app/memory/DOCKER.md` (comenzi, porturi, troubleshooting, ce rămâne pentru Azure).

**Următorii pași (Docker)**:
- P0: utilizatorul rulează `docker compose build` + `up -d` local și confirmă că
  http://localhost:8080 servește aplicația și migrările s-au aplicat.
- P1: verificare flux CAM în container (upload fișiere → volum `mma-files`) și apel LOINC
  `app → http://loinc:8000`.
- P1: push imagini în Azure Container Registry + deploy pe App Service for Containers.
- P1: comutare `CamSettings:Storage=Blob` + `ScaleOut:Enabled=true` pentru multi-instanță.
- P0 (după domeniu public): webhook Stripe cu chei LIVE.

## 2026-06 — Docker validat pe mașina utilizatorului + corecție fonturi PDF

**Docker: RULEAZĂ**. Utilizatorul a executat local întregul flux:
`docker compose build` (276 s) → `up -d` → 3 containere Up, `mma-sql` Healthy.
Log-uri confirmate: `Database:AutoMigrate — EF Core migrations applied`,
`StartupSeed: Clinica Demo created`, `LoincStore loaded: 97314 entries, ~149.5 MB`,
`LOINC matcher READY (entries=97314)`, iar `mma-app` apelează cu succes
`GET /ready 200 OK` pe `mma-loinc` ⇒ comunicarea C# ↔ Python în rețeaua Docker merge.
Avertismentul `LoincSeeder: CSV file not found` este inofensiv: tabela SQL
`LoincDictionary` e folosită doar de `seed_embeddings.py` (o dată) și de
`LoincValidator.cs` (fără apelanți); potrivirea LOINC la runtime merge prin
`LoincMatcherClient` → serviciul Python → fișierele `data/*.npy`.

**Corecție fonturi PDF (Docker-only)**: `✓` și `⚠` apăreau ca pătrățele cu `?`.
Introdus `PdfBranding.FontChain = { "Arial", "Liberation Sans", "DejaVu Sans" }`
în toate cele 6 generatoare PDF; `Dockerfile` instalează `fonts-dejavu-core`;
emoji-ul `🔒` înlocuit cu `▪` (3 locuri în `PdfReportGenerator`).
Probă nouă `/app/probe_pdf_glyphs/` cu `CheckIfAllTextGlyphsAreAvailable=true`:
14 PASS / 1 FAIL (doar emoji-ul lacăt). Build 0/0.
**De validat de utilizator** după `docker compose up -d --build app`.

**Stripe**: `Public business name` schimbat de utilizator în Dashboard
(`speak-romanian-77` → `mymedicalapp.net`). Setare pe partea Stripe, nu în cod ⇒
activă imediat și în container. **De refăcut o dată în contul LIVE.**
Decizie: metodele de plată rămân toate (Card + Apple Pay + Link + Klarna),
pentru conversie. Badge-ul „Sandbox” dispare automat pe cheile live.

**Rămas pe listă (Docker/Azure)**:
- P1: validare PDF-uri după rebuild (bife, ⚠, diacritice)
- P1: Embedded Checkout — buton „Înapoi” și antet proprii în loc de cele Stripe (~2-3 h);
  utilizatorul a amânat, nu a fost respins
- P1: push imagini în Azure Container Registry + App Service for Containers
- P1: `CamSettings:Storage=Blob` + `ScaleOut:Enabled=true` pentru multi-instanță
- P0 (după domeniu public): webhook Stripe cu chei LIVE

**Decizie 2026-06 — RESPINS: logo de clinică în PDF-urile B2B.** Propus de agent
(coloane `Clinics.LogoBytes/LogoContentType`, upload din CAM Dashboard, antet
comparație). Utilizatorul a refuzat explicit: „DEOCAMDATĂ RENUNȚĂM LA ACEST LOGO”.
**A NU se mai propune** fără cerere directă din partea utilizatorului.

## 2026-06 — Localizarea paginii Stripe Checkout

**Confirmat de utilizator**: corecția fonturilor PDF funcționează în container
(`✓`, `⚠`, diacritice, `▪` — toate corecte). Problema fonturilor e ÎNCHISĂ.

**Problemă nouă raportată**: pagina Stripe apărea integral în română indiferent de
limba selectată în aplicație (reprodus cu UI pe spaniolă). Două cauze distincte:
1. `SessionCreateOptions.Locale` nu era setat ⇒ Stripe folosea implicit `auto`,
   adică **limba browserului**, nu limba aplicației.
2. `Description = $"{package.Credits} credite / credits"` era hardcodat în
   `StripePaymentService.cs` (română + engleză lipite).

**Rezolvat**:
- `StripePaymentService.CreateCheckoutAsync` primește `string? uiLanguage` și setează
  `Locale = StripeLocale(uiLanguage)`. Helper privat `StripeLocale`: normalizează
  `es-ES`→`es`, validează contra `SupportedLanguagesConfig.Codes`, altfel `"auto"`.
  Toate cele 7 limbi livrate (en/ro/fr/es/de/it/pt) sunt locale Stripe valide ⇒ mapare 1:1.
- `CreditsController` trimite `CultureInfo.CurrentUICulture.TwoLetterISOLanguageName`.
- Cheie nouă `StripeLineItemCredits` adăugată în toate cele 7 blocuri din `Loc.cs`.
- Probă nouă `/app/probe_stripe_locale/` (referință directă la MedicalApp.dll):
  7/7 PASS traduceri + mapare locale corectă. Build 0/0.
**De validat de utilizator** (F5 în VS2026 cu UI pe spaniolă, apoi rebuild container).

**Explicat, NU defecte**: selectorul de monedă RON/EUR ține de Adaptive Pricing
(țara/IP-ul clientului, nu limba); badge-ul „Sandbox” dispare doar pe chei live;
avertismentul `SessionMiddleware: Error unprotecting the session cookie` apare
pentru că browserul partajează cookie-urile `localhost` între container (:8080) și
VS2026 (:7xxx), care au key ring-uri Data Protection diferite — inofensiv, sesiunea
se recreează. Pe Azure e deja acoperit de `PersistKeysToAzureBlobStorage` când
`ScaleOut:Enabled=true`. Utilizatorul a fost întrebat dacă vrea nume de cookie
distinct per mediu (cosmetic) — fără răspuns, NEIMPLEMENTAT.

## 2026-06 — Fix suprapunere banner hero pe ecrane înguste (landing page)

**Raportat**: pe fereastră îngustă, bannerul verde multilingv se suprapunea peste
rândul de statistici `30+ / ~30s / LOINC` din hero.

**Cauză**: `.land-hero-visual-wrap .land-lang-banner` avea `margin-top: -7.5rem`
(≈ −120 px), ridicare gândită pentru layout-ul pe 2 coloane (banner lângă doctorul
care se plimbă). Sub 980 px `.land-hero-grid` trece pe `1fr`, coloana dreaptă ajunge
SUB cea stângă, iar ridicarea trage bannerul peste `.land-hero-stats`.
Fereastra de reproducere: **900–980 px** (meniul se pliază abia la 900 px, deci
linkurile par încă „normale”); antetul `sticky` amplifica confuzia.

**Rezolvat**:
- scos `margin-top:-7.5rem` din `style` inline în `Views/Home/Landing.cshtml`
  (rămâne o singură sursă, în CSS);
- `wwwroot/css/landing.css`: `@media (max-width: 980px) { ... margin-top: 0 !important }`.

**Verificat** cu banc temporar (landing.css real + grila reală + verificator geometric
de suprapunere, apoi șters): BEFORE 940px = overlap YES; AFTER 940px = NO;
AFTER 390px = NO + zero overflow orizontal; AFTER 1920px = margin-top tot −120px
⇒ **desktop-ul rămâne neschimbat**. Build 0/0.
**De validat de utilizator** (F5 + Ctrl+F5 pentru cache CSS).

**Lecție de reținut**: pe landing page există mai multe offset-uri negative fixe
(`margin-top`, `translate`) calibrate pentru grila pe 2 coloane. Orice offset negativ
nou TREBUIE însoțit de resetare în `@media (max-width: 980px)`, altfel reapare exact
acest tip de suprapunere. Breakpoint-uri active pe landing: 980 (grilă), 900 (meniu),
600/520/480 (detalii).

## 2026-06 — Revizie responsive + accesibilitate pe landing page (1280/980/768/390)

**Metodă nouă, reutilizabilă**: aplicația C# poate fi pornită ÎN POD-UL EMERGENT
fără SQL Server, pentru audit de UI:
```
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://0.0.0.0:8099 \
Hosting__UseHttpsRedirection=false LoincAutoStart__Enabled=false \
LoincMatcher__Enabled=false Database__AutoMigrate=false \
ConnectionStrings__DefaultConnection="Server=127.0.0.1,1433;...Connect Timeout=1" \
dotnet run -c Release --no-launch-profile
```
Funcționează pentru că `StartupSeed` e prins în try/catch („app will continue
running") și `HomeController.Index` nu atinge baza. Limba se comută prin cookie
`.AspNetCore.Culture` = `c=ro|uic=ro`. **Paginile din spatele login-ului NU pot fi
auditate aici** (necesită DB) — vezi `wwwroot/dev/ui-audit.js`.

**Defecte găsite și reparate**:
1. **CTA invizibil, pe TOATE lățimile inclusiv desktop**: `body.landing-body a`
   (specificitate 0,1,2) învingea `.land-btn-primary` (0,1,0) ⇒ toate butoanele
   `<a>` moșteneau culoarea de link. `land-btn-primary`: text #4A5C50 pe fundal
   #4A5C50, contrast **1.00**. Reparat cu reguli de specificitate egală
   (`body.landing-body a.land-btn-primary`) ⇒ **7.15**. Același mecanism scădea
   CTA-ul coral la 2.42.
2. **Coliziune meniu între 1010px și 769px**: `.land-nav-inner` avea
   `justify-content: space-between` FĂRĂ `gap` ⇒ grupul de linkuri se lipea de
   pilula de limbă (gap măsurat 0px); în ro/de/pt trecea unul peste altul.
   Reparat în 3 pași: `gap: 1.5rem` pe container + `flex-wrap: wrap` + `min-width: 0`
   pe rândul de linkuri + ascunderea linkurilor sub 940px (altfel rândul cerea
   3-4 linii și meniul ajungea la 221-326px înălțime).
   ATENȚIE: prima încercare, doar cu `gap`, a făcut ultimele 2 linkuri să DISPARĂ
   sub pilulă (container strâns + copii care ies din el) — prinsă la măsurare.
3. **Contrast sub WCAG AA** (aprobat de utilizator „închide ambele culori"):
   - banner multilingv: #00C853 → **#00693C** (1.98 → 5.3 pe fundalul compus)
   - CTA coral: variabile noi `--land-coral-cta: #C25436` / `--land-coral-cta-dark:
     #B0462B` folosite DOAR pe pilule solide cu text alb (2.95 → 4.55).
     `--land-coral` rămâne pentru borduri/iconițe/fundaluri cu text închis.
   - `.land-compare-best` (badge „CEA MAI BUNĂ ALEGERE"): 2.95 → 4.55
   - `.land-eyebrow-coral`: #D1674A → **#B0462B** pe #FCEEEB (3.23 → 4.94)
4. **Gol vertical pe mobil** (aprobat „strânge-l"): `@media (max-width: 600px)`
   reduce `.land-section` de la 6rem la 3.25rem și hero la 3/3.5rem.
   Înălțimea paginii la 390px: 12740 → 12104px. `#clinics` are `padding-top:0`
   inline, deci nu e afectat.

**Rezultat final verificat**: RO + EN × 1280/980/768/390 ⇒ **0 depășiri de cadru,
0 suprapuneri, 0 texte sub prag**, fără bară de derulare orizontală. Separat,
70 combinații (7 limbi × 10 lățimi) pe meniu ⇒ 0 probleme, înălțime meniu max 179px.
Build 0/0.

**Alarme false documentate** (nu sunt defecte):
- `.land-brand-mark` („M+"): fundal `linear-gradient`, contrastul nu se poate
  calcula din `backgroundColor` ⇒ raportat 1.06, în realitate alb pe verde.
- `.land-deck-tag`: stiva de carduri se suprapune PRIN DESIGN.
- `.doc-walker` / `.doc-bouncer`: doctorul animat iese intenționat din cadru
  (clipat de un părinte, nu produce bară de derulare).
- iconițe emoji: raportul de contrast nu are sens pentru glife emoji.

**Instrument livrat**: `MedicalApp/wwwroot/dev/ui-audit.js` — auditor client-side
(read-only) pentru paginile din spatele login-ului. Se încarcă din consola
browserului cu:
`var s=document.createElement('script');s.src='/dev/ui-audit.js';document.body.appendChild(s);`
apoi `uiAudit()` la fiecare lățime. Raportează depășiri, suprapuneri și contrast,
cu excluderile de mai sus deja aplicate. Validat pe landing: 0/0/0 la 4 lățimi.

**Decizii utilizator**: linkurile ascunse între 769-939px sunt OK (fără hamburger).
Următorul audit cerut: **Dashboard B2C + Dashboard cabinet** (de rulat de utilizator
cu ui-audit.js, pentru că necesită autentificare).

### 2026-06 — ui-audit.js: măsurarea contrastului pe gradiente (corecție importantă)

Prima versiune a auditorului renunța complet la măsurarea contrastului dacă
întâlnea ORICE `background-image`/gradient în lanțul de părinți. Pe `/Profiles`
(raportat de utilizator) asta a dus la **157 elemente „nemăsurabile"**, adică
majoritatea textelor paginii nu erau verificate, iar „0 sub prag" era înșelător.

**Reparat**: `bgCandidates(el)` extrage stopurile de culoare declarate în gradient
(`linear-gradient(... rgb(a) ... rgb(b) ...)`) și testează textul împotriva
fiecărui stop, raportând **cazul cel mai defavorabil**. Variază un singur strat
pe rând (restul pe prima culoare), pentru a evita explozia combinatorică.
Rămân nemăsurabile doar bitmap-urile `url(...)` și emoji-urile.
Rezultat pe landing: nemăsurabile **57 → 8**.

**Consecință — o „alarmă falsă" declarată anterior era un defect real**:
`.land-brand-mark` („M+" din logo) avea gradient `#7A9180 → #4A5C50`. Pe stopul
închis contrastul e 7.15, dar pe stopul **deschis** doar **3.39** ⇒ litera se
estompa în partea de sus a pătrățelului. Agentul o clasase drept alarmă falsă
pe baza inspecției vizuale, NU a unei măsurători. Stopul deschis schimbat în
**#657A6B** (4.62, aceeași nuanță de sage).

**LECȚIE**: nu declara „alarmă falsă" pe baza a ceea ce pare corect într-o
captură de ecran. Dacă instrumentul nu poate măsura, repară instrumentul.

**Stare finală landing**: RO + EN × 1280/980/768/390 ⇒ 0 depășiri, 0 suprapuneri,
0 texte sub prag, 8 nemăsurabile. Build 0/0.

**Următorul pas (la utilizator)**: rulare `ui-audit.js?v=2` pe Dashboard B2C,
Dashboard cabinet și `/Profiles`, la 980/768/390, cu date REALE (nume lungi,
multe analize, text lung în Istoric medical). Semn că are versiunea nouă:
`nemasurabile` scade de la 157 la câteva unități.

### 2026-06 — P0 REPARAT: bară de derulare orizontală pe TOATE paginile logate

**Găsit de utilizator** rulând `ui-audit.js` pe `/Profiles` la 390px:
`bara de derulare orizontala : DA`, elementul vinovat
`div.ms-auto.d-flex.align-items-center` cu lățime 636-814px.

**Amploare reală (mult mai mare decât „problemă de mobil")**: grupul din dreapta
din `Views/Shared/_Layout.cshtml` ține până la 6 elemente (credite, Admin,
comutator CAM, Profiluri, e-mail, Deconectare), lățime naturală ~814px, într-un
`d-flex` FĂRĂ `flex-wrap`. Măsurat pe banc de test cu markup-ul real:
bară orizontală la **390, 480, 768, 900, 980 ȘI 1100px** ⇒ orice ecran sub ~1150px
avea derulare laterală pe Dashboard, Profiles, Fișiere, Admin — toate paginile
autentificate. Cazul cel mai rău = cont Cabinet + Admin (toate 6 elementele).

**Reparat**: `ms-auto d-flex flex-wrap flex-xl-nowrap justify-content-end
align-items-center gap-2` + adresa de e-mail ascunsă sub 768px
(`d-none d-md-inline`, 211px, cel mai lat element și cel mai puțin util pe telefon).

**Regresie evitată**: cu `flex-wrap` necondiționat, bara creștea la 2 rânduri și pe
desktop (navH 76 → 102 la 1280px), unde încăpea deja. De aceea `flex-xl-nowrap`:
de la 1200px în sus rămâne un singur rând, navH 76px / 56px — identic cu înainte.

**Verificat**: banc de test cu markup-ul real din _Layout (cont Cabinet+Admin,
gmail lung, etichete RO), 10 lățimi, înainte/după ⇒ 0 depășiri și fără bară
orizontală la toate lățimile după fix; desktop ≥1200px neschimbat. Build 0/0.
Bancul a fost șters după verificare.

**ATENȚIE pentru următorul audit**: utilizatorul rula `ui-audit.js` din cache
(versiunea fără suport pentru gradiente, `nemasurabile: 157`). Rezultatele de
contrast din acel raport NU sunt valide. Instrucțiune dată: Ctrl+F5 + încărcare cu
`/dev/ui-audit.js?v=2`; confirmarea versiunii noi = `nemasurabile` scade la
câteva unități.

**Rămâne de auditat (la utilizator, necesită autentificare)**: Dashboard B2C,
Dashboard cabinet, /Profiles, la 980/768/390, cu date reale.
