# Audit indexuri SQL (pregătire hostare — pasul 4)
Iunie 2026. Migrare: **`AddScaleOutIndexes`**. Script T-SQL generat: `probes/AddScaleOutIndexes.sql`.
Probă de regresie: `probes/SqlIndexAuditProbe.cs.txt` (proiect `/app/probe_indexes`) — **17/17 PASS**.

## Metodă
Nu am ghicit indexuri: am inventariat interogările reale din `Controllers/`, `Services/` și
`Areas/CAM/`, am notat pentru fiecare *filtrul*, *sortarea* și *coloanele citite*, apoi am cerut
un index care să acopere exact acel tipar. Verificarea se face pe modelul EF de design-time
(nu are nevoie de SQL Server), plus generarea scriptului T-SQL real.

## Tabelele mari și ce s-a schimbat

### `InterpretationHistories` (cel mai mare tabel; fiecare rând cară `RawJsonResult` nvarchar(max))
| Interogare (unde apare) | Index |
|---|---|
| Arhiva profilului, grafice, comparații: `UserEmail + ProfileId + Status` ORDER BY `CreatedAt DESC` (`ProfilesController.History/Compare/Charts/Dossier`) | `IX_..._User_Profile_Status` = `(UserEmail, ProfileId, Status, CreatedAt DESC)` — **CreatedAt adăugat în cheie**; înainte SQL Server sorta toată arhiva profilului la fiecare afișare |
| Pastila de job: „ultimul rând al meu”, la 6–30 s per utilizator logat (`InterpretationController.JobStatus`) — **cea mai frecventă interogare din aplicație** | `IX_..._User_Id_Desc` = `(UserEmail, Id DESC) INCLUDE (Status)` — **acoperitor**, deci pollingul nu atinge tabelul unde stă JSON-ul |
| „câte rânduri sunt `processing`” (widget admin + curățarea orfanilor la pornire) și media ultimelor 20 de durate (ETA) | `IX_..._Status_Id_Desc` = `(Status, Id DESC) INCLUDE (DurationMs)` |
| Număr de interpretări per profil, filtrat pe un set de `ProfileId` (`ProfilesController.Index`) | `IX_..._Profile_Status` = `(ProfileId, Status)` |
| „am mai interpretat acest PDF?” la fiecare upload | `IX_..._User_PdfSha256` (nemodificat) |
| — | **șters** `IX_..._UserEmail`: `UserEmail` e prima coloană a indexurilor de mai sus, deci SQL Server le folosește oricum; scăpăm de o scriere în plus la fiecare rând |

### `AiUsageLogs`
Toate widget-urile admin încep cu `CreatedAt >= ultimele 30 de zile` și apoi grupează pe
`Source` / `ModelUsed` / `Status`. Deci: un singur index
`IX_AiUsageLogs_CreatedAt_Status = (CreatedAt, Status) INCLUDE (Source, ModelUsed, InputTokens, OutputTokens)`.
**Șterse** `IX_..._Status`, `IX_..._Source`, `IX_..._CreatedAt`: nicio interogare nu filtrează pe
Status sau Source ca primă coloană (gruparea nu face seek), iar fiecare index costa o scriere după
FIECARE apel Gemini.

### `Purchases`
`IX_Purchases_PurchasedAt` → `INCLUDE (AmountEur)`: toate cifrele de venit din dashboard sunt
`SUM(AmountEur)` / `COUNT` / `GROUP BY zi` peste un interval de date ⇒ devin index-only.

### `ClinicAnalyses` (CAM / B2B)
- `(ClinicId, PatientId)` — comparațiile B2B (nemodificat).
- **nou** `(ClinicId, ProcessedAt) INCLUDE (PatientId)` — „câți pacienți distincți în perioada X”
  din CAM Dashboard.
- **șters** `IX_ClinicAnalyses_ClinicId` (prima coloană a ambelor compozite).
- `(PatientId)` păstrat: `CamBatchService` citește toate analizele unui pacient fără ClinicId.

### Neschimbate, verificate ca fiind corecte
- `InterpretationJobs`: `(Status, EnqueuedAt)` pentru scanarea de recuperare + `HistoryId` unic.
- `LoincMatchCache`: citit exclusiv pe cheia primară (`CacheKey`), plus `PipelineVersion` pentru admin.
- `ClinicPatients`: `(ClinicId, NameKey, Email)` unic.
- `LoincDictionary`: cheie pe `LoincCode` + `LongCommonName` pentru căutări admin.

## Verificări de siguranță incluse în probă
- Nicio cheie de index pe o coloană nemărginită (`nvarchar(max)`) — SQL Server ar refuza crearea.
- Fiecare cheie de index încape în limita de **1700 bytes** a indexurilor non-clustered.
- `dotnet ef migrations has-pending-model-changes` ⇒ „No changes” (snapshot sincron cu modelul).

## La hostare
1. `Update-Database` (sau scriptul din `probes/AddScaleOutIndexes.sql`) — rulează într-o
   tranzacție și e reversibil (`Down` recreează exact indexurile vechi).
2. Pe un tabel deja mare, în Azure SQL **Premium/Business Critical**, adaugă `WITH (ONLINE = ON)`
   la `CREATE INDEX` ca să nu blochezi scrierile; pe Standard/General Purpose fă-o în afara orelor
   de vârf. Cu volumul actual (mii de rânduri) diferența e nesemnificativă.
3. De monitorizat după primele săptămâni de trafic real:
   `sys.dm_db_index_usage_stats` (indexuri neatinse = scrieri irosite) și
   `sys.dm_db_missing_index_details` (ce mai cere optimizatorul).
4. Observație rămasă (nu blochează hostarea): `LoincMatchCache` are cheia primară clustered pe un
   SHA-256 aleator ⇒ inserările cad în pagini aleatorii și fragmentează indexul. La zeci de mii de
   mapări, fie `FILLFACTOR 80` + reindexare periodică, fie mutarea pe un `Id` identity cu index
   unic pe `CacheKey`. Astăzi tabelul e mic și citirile sunt seek-uri pe cheie.
