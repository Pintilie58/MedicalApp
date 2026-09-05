# Cotă Gemini gestionată + coadă durabilă (iunie 2026)

Pașii 1 și 2 din pregătirea pentru 50 de utilizatori simultani (pasul 3 — instanțe
multiple — se bifează la hostare, arhitectura e deja pregătită).

---

## 1. Cotă Gemini gestionată — `Services/GeminiRateLimiter.cs`

**Problema:** Google limitează câte cereri pe minut acceptă contul. Aplicația nu știa
nimic despre această limită. Mărirea `InterpretationQueue:MaxConcurrent` de la 3 la 15
ar fi produs erori `429`, nu viteză.

**Soluția, trei mecanisme:**
1. **fereastră glisantă de un minut** — apelantul își așteaptă rândul (`Task.Delay`) în
   loc să fie refuzat de Google;
2. **plafon de apeluri simultane** (`MaxConcurrentCalls`) — o interpretare face 1-5
   apeluri Gemini, deci plafonul nu e egal cu numărul de sloturi de interpretare;
3. **pauză comună după un refuz** — când Google răspunde totuși 429/503, se citește
   header-ul `Retry-After` și **toți** apelanții se opresc atâta timp cât a cerut Google
   (maxim 5 minute). Fără asta, 15 lucrări ar continua să lovească o cotă deja epuizată.

Hook: în `GeminiMedicalInterpretationService.Split.cs → PostAsync` (punctul unic prin
care trec toate apelurile, și cel monolitic și cele 3 etape ale pipeline-ului „split”).
`GeminiTransientException` transportă acum `RetryAfter`, iar backoff-ul din
`B2cInterpretationRunner` îl respectă (minim propriul backoff, maxim 2 minute).

**Configurare** (`appsettings.json → Gemini:RateLimit`):
```
Enabled: true                  // false = comportamentul vechi, fără limitare
RequestsPerMinute: 60          // cota reală a contului Google (AI Studio → Quotas)
MaxConcurrentCalls: 6
CooldownSecondsOnReject: 20    // folosit doar dacă Google nu trimite Retry-After
WindowSeconds: 60              // se schimbă doar în teste
```
Valorile implicite sunt generoase intenționat: comportamentul de azi nu se schimbă.
**Înainte de a urca `MaxConcurrent` peste 3, pune aici cota reală.**

Statistici disponibile în memorie prin `GeminiRateLimiter.Stats()`: apeluri, câte au
așteptat, milisecunde totale de așteptare, refuzuri, apeluri în ultimul minut, dacă e în
pauză. (Nu sunt încă expuse în Admin — candidat pentru panoul de diagnostic.)

---

## 2. Coadă durabilă — tabelul `InterpretationJobs`

**Problema:** rândul de așteptare trăia doar în memoria procesului (`Channel`). Un
restart, un deploy sau un crash ștergea tot ce aștepta — utilizatorul plătise creditul și
nu primea niciodată rezultatul. Iar cu mai multe instanțe, fiecare ar avea propria coadă,
deci una ar sta degeaba în timp ce alta e sufocată.

**Soluția:** SQL e sursa de adevăr, `Channel` rămâne doar calea rapidă de dispecerizare.

| Fișier | Rol |
|---|---|
| `Models/InterpretationJobRecord.cs` | rândul durabil (inclusiv PDF-ul, `RowVersion` pentru concurență) |
| `Services/InterpretationJobStore.cs` | scriere, preluare (`MarkRunning`), ștergere, găsire abandonate, revendicare optimistă |
| `Services/InterpretationJobRecoveryWorker.cs` | scanează la fiecare `RecoveryIntervalSeconds` (implicit 60) și repune în coadă |
| `Services/InterpretationJobQueue.TryRequeue` | repune o lucrare recuperată **fără** limita per utilizator |
| `Controllers/InterpretationController` | scrie rândul durabil imediat după ce lucrarea a intrat în coadă |
| `Services/InterpretationQueueWorker` | `MarkRunning` la start, ștergerea rândului la final (succes sau eșec) |
| `Services/StartupSeed.FailOrphaned…` | **nu** mai eșuează rândurile „processing” care au încă lucrare durabilă |

**Ciclul de viață:** `queued` → `running` (cu *lease* de **2 minute**, reînnoit prin
heartbeat la fiecare 30 s, proprietar = `Mașină/PID`) → rândul e **șters** la finalul
interpretării. Tabelul rămâne mic; PDF-ul (≤10 MB) dispare împreună cu rândul.

**Cât durează recuperarea** (corecție iunie 2026, după testul real al utilizatorului):
prima versiune folosea un lease de 20 de minute, deci după un restart lucrarea rămânea
„îngheţată” până expira — utilizatorul a măsurat **12 minute** de așteptare. Acum:
- lease-ul e de **2 minute**, ținut în viață de un heartbeat cât timp lucrarea rulează
  (`RenewLeaseAsync` la 30 s, din propriul scope de DbContext);
- cu **o singură instanță** (`ScaleOut:Enabled = false` — dezvoltare locală și hostarea
  de start) orice rând rămas `running` de la un proces anterior e abandonat **prin
  definiție**, deci se reia imediat, fără să se aștepte lease-ul;
- niciodată nu se fură o lucrare al cărei `Owner` e procesul curent (altfel același
  buletin ar fi interpretat de două ori);
- cu mai multe instanțe, lucrarea unui frate viu (lease valid) nu se atinge.

Rezultat: reluare în **~10 secunde** local (prima trecere a workerului) și în cel mult
~2 minute + intervalul de scanare într-un scenariu multi-instanță.

**Recuperare** — o lucrare e considerată abandonată dacă e `queued` sau `running` cu
lease expirat. Revendicarea e optimistă (`RowVersion`): dacă două instanțe încearcă
aceeași lucrare, exact una câștigă. Fiecare instanță ia **cel mult**
`MaxConcurrent - ActiveCount` lucrări, ca să nu monopolizeze coada (verificat în probă).
După `MaxAttempts = 3` încercări eșuate, lucrarea e abandonată definitiv: rândul e șters,
istoricul trece pe „error” și **creditul e restituit** — o lucrare otrăvită nu poate
bucla la infinit.

---

## Testare

`/app/memory/probes/QuotaAndDurableQueueProbe.cs.txt` — **39/39 PASS**:
cotă depășită ⇒ așteaptă (nu eșuează); sub cotă ⇒ zero întârziere; plafon de apeluri
simultane respectat și eliberat; `Retry-After` respectat; refuz fără `Retry-After` ⇒
cooldown din config; `Retry-After` absurd (3 ore) ⇒ plafonat; `Enabled=false` ⇒
comportament identic cu înainte; rând scris/preluat/șters corect; lease viu ⇒ nimeni nu
atinge lucrarea; lease expirat ⇒ recuperare; revendicarea reconstruiește lucrarea exact
(inclusiv PDF-ul); lucrare otrăvită ⇒ abandonată + credit restituit; recuperarea repune 3
lucrări și nu le dublează la a doua trecere; buget per instanță respectat; cea mai veche
lucrare e luată prima; curățarea de la pornire nu mai omoară lucrările recuperabile, dar
omoară în continuare orfanele reale; limita per utilizator rămâne activă pe calea normală.

Regresie: B2C **66/66**, DI **1/1**, cache LOINC **55/55**, build **0 warning-uri**.

Migrare EF nouă: **`AddDurableInterpretationQueue`**.
