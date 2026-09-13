# Găzduire pe Azure — ghid de configurare

Scris în iunie 2026, după lucrarea „procesare în fundal, nu în request”.
Documentul are două părți: **(A) ce trebuie bifat în Azure** și **(B) de ce**, ca să nu
fie nevoie să reciteşti codul peste șase luni.

---

## A. Checklist de configurare (App Service, Windows sau Linux)

### 1. Setări obligatorii ale planului
| Setare | Valoare | De ce |
|---|---|---|
| **Always On** | **On** | Fără ea, App Service descarcă procesul la inactivitate și **workerii de fundal mor** (interpretările și loturile CAM rămân neprocesate până la următorul request). |
| **ARR affinity cookie** | **Off** | Aplicația e stateless (sesiuni + cache în SQL, progres în cache distribuit). Affinity ar dezechilibra instanțele. |
| **HTTP version** | 2.0 | Nimic esențial, doar latență mai bună. |
| **Health check path** | `/healthz` | Azure scoate din rotație o instanță bolnavă. Endpointul există în `Program.cs` și răspunde `ok`. |
| **Platform** | 64-bit | QuestPDF și PdfPig lucrează cu fișiere în memorie. |
| **Minimum instances** | 2 (recomandat) | Un deploy sau o repornire nu lasă aplicația fără nicio instanță. |

### 2. Variabile de mediu / App settings
Se pun în **Configuration → Application settings** (nu în `appsettings.json` din repo):

```
ConnectionStrings__DefaultConnection = Server=tcp:<server>.database.windows.net,1433;Initial Catalog=<db>;...
Gemini__ApiKey                       = <cheia Google>
Gemini__RateLimit__InstanceCount     = <numarul de instante, ex. 3>
ScaleOut__Enabled                    = true
ScaleOut__InstanceId                 = %WEBSITE_INSTANCE_ID%
Azure__Blob__ConnectionString        = <connection string storage>
Email__...                           = <SMTP / SendGrid>
WEBSITE_TIME_ZONE                    = Europe/Bucharest
```

> Atenție la `Gemini__RateLimit__InstanceCount`: **se actualizează manual când schimbi
> numărul de instanțe**. Dacă îl lași 1 și rulezi 3 instanțe, trimiți de 3 ori cota
> către Google și primești `429` exact în vârf de trafic.

### 3. Azure SQL
- Minim **S1 (20 DTU)** pentru început; cozile durabile fac un `SELECT` scurt la câteva secunde per instanță.
- `Connection Resiliency`: `EnableRetryOnFailure` e deja activat în cod.
- Migrările: rulează **`Update-Database` înainte de swap** (aplicația nu migrează singură în producție).
- Tabelele de cache/sesiune pentru scale-out se creează de EF: `Sessions`, `DistributedCache` (vezi `SCALE_OUT.md`).

### 4. Scalare
- Regula recomandată: scale-out după **CPU > 70% pe 10 minute**, maximum 3-4 instanțe.
- La fiecare schimbare a numărului de instanțe, ajustează `Gemini__RateLimit__InstanceCount`.
- `Interpretation:MaxConcurrent` (din `appsettings`) este **per instanță**: 3 instanțe × 3 = 9 interpretări simultane. Corelează cu cota Gemini și cu DTU-urile.

### 5. Deploy
- Folosește **deployment slots** + swap: slotul se încălzește, apoi intră în trafic.
- La oprire, App Service dă doar câteva secunde de grație, iar o interpretare durează 2-4 minute. **Este normal ca un job să fie întrerupt.** Nu se pierde:
  - interpretările B2C au lease + `InterpretationJobRecoveryWorker` → se reia automat;
  - loturile CAM au lease → sunt marcate `Failed` rapid (fără re-trimitere de emailuri duble) și operatorul relansează;
  - emailurile în masă au lease + `NextIndex` → se reiau **din locul unde au rămas**.

### 6. Endpoint de sănătate
`GET /healthz` întoarce `ok` (text simplu). Intenționat **nu** atinge SQL, Gemini sau Blob:
dacă ar depinde de ele, o dependență lentă ar face Azure să recicleze instanțe sănătoase și
ar cădea tot site-ul. Pune exact această cale în Health check.

---

## B. Ce rulează unde (și de ce nu blochează requesturile)

### B.1 Interpretarea B2C
`InterpretationController.Upload` (POST) face **doar**: validare, rezervare credit,
scriere în coada durabilă (`InterpretationJobs`, cu PDF-ul în rând) și redirect.
Lucrul propriu-zis (extragere PDF, 3 apeluri Gemini, LOINC, raport PDF, email) rulează în
`InterpretationQueueWorker` (`BackgroundService`), cu:
- **lease** de 2 minute + heartbeat la 30 s (`InterpretationJobStore`);
- **recovery** (`InterpretationJobRecoveryWorker`) care preia joburile cu lease expirat;
- progres publicat în `IDistributedCache`, deci pollingul funcționează de pe orice instanță.

### B.2 Loturile CAM (B2B)
Înainte porneau cu `Task.Run` fire-and-forget **din request** — mureau la orice reciclare
de instanță. Acum:
- butonul „Start” scrie rândul cu `Status="Queued"` (plus limba operatorului) și se întoarce;
- `CamBatchQueueWorker` (orice instanță) revendică lotul cu lease de 3 minute, îl rulează,
  reînnoiește lease-ul din 30 în 30 de secunde și publică un **snapshot de progres** în
  cache-ul distribuit la fiecare 3 secunde (așa vede progresul și o instanță care nu lucrează);
- **Cancel** se scrie în rând (`CancelRequested`) și e citit de heartbeat → anularea merge
  chiar dacă apeși butonul pe altă instanță decât cea care lucrează;
- garda „un lot per clinică” e acum o interogare în baza de date, nu un dicționar în memorie;
- `StartupSeed.FailOrphanedBatchesAsync` **nu mai omoară loturile surorilor**: decide după
  lease, nu după simplul fapt că rândul e `Running`.

Decizia de business rămâne: **loturile întrerupte NU se reia automat** (ar re-trimite emailuri).

#### B.2.1 De ce lease-ul stă în tabel separat (`ClinicBatchClaims`) — bug reparat iunie 2026
Prima versiune ținea lease-ul (`LeaseUntil`, `OwnerInstance`) direct pe `ClinicBatchRuns`,
cu o coloană `RowVersion` (`[Timestamp]`) pentru revendicare optimistă. Rezultatul în
producție: la **primul fișier** dintr-un lot apărea `DbUpdateConcurrencyException`.
Cauza: `CamBatchService` ține rândul lotului atașat în DbContext-ul lui pe toată durata
rulării și îi salvează contoarele după fiecare fișier, în timp ce bucla de heartbeat
reînnoia lease-ul **din alt DbContext**. Prima reînnoire schimba `RowVersion`, deci
următorul `SaveChangesAsync` al runner-ului nu mai găsea rândul cu versiunea lui.

Soluția: lock-ul s-a mutat într-un rând propriu — `ClinicBatchClaims`
(`BatchRunId` = cheie primară, `OwnerInstance`, `LeaseUntil`, `ClaimedAt`):
- **revendicarea = INSERT**: cheia primară face operația atomică între instanțe, a doua
  instanță pierde pur și simplu insertul (`DbUpdateException`) și trece la lotul următor;
- **reînnoirea lease-ului nu mai atinge rândul lotului**, deci contoarele runner-ului nu
  mai intră în coliziune;
- `ClinicBatchRuns` **nu mai are** niciun token de concurență (`RowVersion` și `LeaseUntil`
  au fost șterse prin migrarea `AddCamBatchClaim`);
- „lot abandonat” = rând `Running` fără claim viu → marcat `Failed`
  (`CamBatchQueueStore.FailAbandonedAsync`, `StartupSeed.FailOrphanedBatchesAsync`).

Migrare necesară: `Update-Database` (migrarea `AddCamBatchClaim`) sau scriptul idempotent
`memory/probes/AddCamBatchClaim.sql`. Regresia e acoperită de `probe_azure`
(checkurile 6c-6i: claim atomic, contoare salvate în paralel cu heartbeat, release).

### B.3 Emailul în masă din Admin
Bucla de trimitere era în request → la câteva sute de destinatari depășea limita Azure de
**230 secunde** și murea la jumătate. Acum requestul scrie un rând în `BulkEmailJobs`
(`queued`), iar `BulkEmailWorker` trimite cu pauză de 200 ms între emailuri, ține `NextIndex`,
`Sent`, `Failed` și `LastError`, cu lease de 2 minute. Dacă instanța cade, alta continuă
**din destinatarul următor**, fără duplicate. Progresul se vede în tabelul de pe ecranul Admin.

### B.4 Cota Gemini
Fereastra de limitare trăiește în memoria unui proces, deci `Gemini:RateLimit:InstanceCount`
împarte automat cota: fiecare instanță folosește `RequestsPerMinute / InstanceCount`
(minim 1) și `MaxConcurrentCalls / InstanceCount` (minim 1). Panoul
**Admin → Diagnostic infrastructură** arată cota *acestei* instanțe și, dedesubt, cota totală.

### B.5 Ce NU blochează requesturi (verificat)
- Niciun `.Result` / `.Wait()` / `GetAwaiter().GetResult()` pe calea unui request. Singurele
  apariții sunt pe task-uri deja finalizate după `await Task.WhenAll` (etapele Gemini).
- Toate operațiile de I/O (SQL, Blob, HTTP, email) sunt `async`.

### B.6 Ce rămâne de calibrat (conștient, nu scăpat)
1. **Generarea PDF-ului la descărcare** (QuestPDF) rulează în request: ~0,5-2 s de CPU per
   raport. Pe un plan cu 1-2 vCPU, câteva descărcări simultane încetinesc site-ul.
   Opțiune viitoare: salvăm PDF-ul în Blob la prima generare și servim fișierul.
2. **PDF-ul din coadă** e stocat ca `varbinary(max)` în rândul jobului. Simplu și robust, dar
   umflă baza și jurnalul de tranzacții. Opțiune viitoare: Blob + referință în rând.
3. **Logul de progres CAM** din snapshot are ultimele ~30 de linii; dacă pollingul nimerește
   o instanță fără snapshot proaspăt, contoarele vin din baza de date (log gol câteva secunde).
4. Coada pe SQL cu polling e sănătoasă până la ordinul **miilor de joburi/zi**. Peste asta,
   pasul următor e Azure Service Bus — codul e pregătit (lease + owner + attempts), deci se
   schimbă doar transportul.

---

## C. Ordinea la deploy
1. `Update-Database` pe Azure SQL (din VS sau `dotnet ef database update`).
2. Setează App settings (inclusiv `Gemini__RateLimit__InstanceCount`).
3. Always On = On, ARR affinity = Off, Health check.
4. Deploy în slot → verifică `/healthz` și `Admin → Diagnostic infrastructură` → swap.
5. După swap: pornește o interpretare de test și un lot CAM mic; verifică în panoul de
   diagnostic că apar în coadă și se termină.
