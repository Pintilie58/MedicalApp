# Rulare pe mai multe instanțe (scale-out) — ghid de activare

> Cod scris și testat în iunie 2026, **inactiv** implicit. Cu
> `ScaleOut:Enabled = false` (valoarea din `appsettings.json`) aplicația se
> comportă **exact** ca înainte: sesiune în memoria procesului, chei de Data
> Protection pe disc local, serviciile programate rulează necondiționat.

## De ce e nevoie

„Stateless” nu înseamnă că aplicația nu ține minte nimic, ci că starea **nu
trăiește în memoria unui proces**. Orice cerere trebuie să poată fi servită de
orice instanță. Simptomele când nu e așa: utilizatori delogați aleatoriu,
„*antiforgery token could not be decrypted*”, coduri de înregistrare invalide,
emailuri trimise de N ori. Toate intermitente și greu de diagnosticat.

## Ce s-a rezolvat

| # | Problemă | Soluție | Activ când |
|---|---|---|---|
| 1 | Sesiunea în memoria procesului (`Session["UserEmail"]` = identitatea userului) | `AddDistributedSqlServerCache` → tabel `AppSessionCache`, creat automat la pornire | `ScaleOut:Enabled` |
| 2 | Cheile Data Protection pe disc local (cookie + antiforgery) | `PersistKeysToAzureBlobStorage` în containerul `dataprotection`, blob `keys.xml`, `SetApplicationName("MyMedicalApp")` | `ScaleOut:Enabled` |
| 3 | Codurile de verificare la înregistrare într-un dicționar în memorie | `PendingRegistrationStore` rescris pe `IDistributedCache` | **întotdeauna** (local = memorie, identic cu înainte) |
| 4 | `DailySummaryService` / `BudgetAlertService` rulează pe fiecare instanță ⇒ emailuri duplicate | `SingletonLeaseService` — ștafetă în tabelul `AppSingletonLease` (MERGE atomic); doar deținătorul lucrează | `ScaleOut:Enabled` |
| 5 | La pornire, o instanță marca drept eșuate interpretările care rulau pe **alte** instanțe | Se marchează doar rândurile `processing` mai vechi de `OrphanGraceMinutes` (30) | `ScaleOut:Enabled` |

Comportamentul ștafetei este **fail-open**: dacă baza de date nu răspunde, jobul
programat rulează oricum. Mai bine un email trimis de două ori decât un sumar
zilnic care nu mai pleacă niciodată.

## Cum activez la hostare

1. Ai nevoie de **Blob Storage configurat** (același cont ca pentru CAM — vezi
   `CAM_BLOB_STORAGE.md`), pentru cheile de Data Protection.
2. În App Service → *Configuration* → *Application settings*:
   ```
   ScaleOut__Enabled = true
   ```
   (opțional) `ScaleOut__OrphanGraceMinutes`, `ScaleOut__SessionCacheTable`,
   `ScaleOut__DataProtectionContainer`, `ScaleOut__InstanceId`.
3. Repornește. La pornire, aplicația **creează singură** tabelele
   `AppSessionCache` și `AppSingletonLease` (nu ai nevoie de
   `dotnet sql-cache create` și nici de o migrare EF).
4. Abia acum poți urca la 2+ instanțe (App Service → *Scale out*).
5. **Nu activa** ARR affinity / sticky sessions: nu mai e necesară, iar dezactivarea
   ei îți dă distribuție reală a sarcinii.

### Rollback
`ScaleOut__Enabled = false` + repornire, cu o singură instanță. Tabelele rămân
în bază, nefolosite. Utilizatorii se vor deloga o dată (sesiunile se mută înapoi
în memorie), nimic altceva.

## Ce a rămas conștient nerezolvat

- **Progresul interpretării** (`InterpretationProgressTracker`) e în memoria
  instanței. Dacă polling-ul nimerește altă instanță, bara de progres nu se mai
  actualizează — dar jobul continuă, iar pastila din dreapta sus (care citește
  din baza de date) funcționează corect. Degradare cosmetică.
- **Cache-ul cu octeții PDF** pentru „reinterpretează totuși” (15 min): dacă
  cererea aterizează pe altă instanță, utilizatorul trebuie să reselecteze
  fișierul. De mutat pe `IDistributedCache` dacă devine deranjant.
- **Coada de interpretări** e per instanță: `MaxConcurrent` se înmulțește cu
  numărul de instanțe (3 instanțe × 3 = 9). Vezi `AZURE_SCALING.md` pentru
  varianta cu coadă durabilă.
- **Markerul „sumar deja trimis”** al `BudgetAlertService` e un fișier temporar
  local; ștafeta rezolvă duplicarea, dar marker-ul se pierde la repornirea
  containerului.

## Testare (iunie 2026)

`/app/memory/probes/ScaleOutProbe.cs.txt` — **22/22 PASS**, printre care:
- două „instanțe” separate citesc și actualizează același cod de înregistrare;
- două provider-e de Data Protection configurate identic: **instanța B decriptează
  token-ul creat de instanța A** (proba directă că antiforgery nu se mai rupe);
- ștafeta cade fail-open când baza de date lipsește;
- recuperarea orfanilor: jobul de acum 2 minute al altei instanțe **nu** e atins,
  cel de acum 5 ore devine `error` cu restituirea exactă a unui credit;
- în modul single-instance, comportamentul vechi e păstrat identic.

Regresie: suita B2C completă **66/66 PASS** după modificări.
