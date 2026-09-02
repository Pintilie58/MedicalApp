# MyMedicalApp — scalarea interpretărilor pe Azure

> Document pregătitor, scris în iunie 2026, înainte de deploy. Nu descrie codul
> actual, ci ce trebuie schimbat ca aplicația să susțină zeci de mii de
> utilizatori. Codul de azi rulează corect, dar într-un **singur proces**.

## 1. Unde suntem acum

Interpretările B2C rulează în fundal, într-o coadă **în memoria procesului**
(`InterpretationJobQueue` + `InterpretationQueueWorker`), cu limite configurabile
din `appsettings.json` → secțiunea `InterpretationQueue`:

```
"InterpretationQueue": { "MaxConcurrent": 3, "MaxPerUser": 1 }
```

Consecințe importante ale acestui model:

| Aspect | Situația actuală |
|---|---|
| Sfera limitei `MaxConcurrent` | per **instanță** de aplicație (3 instanțe ⇒ 3×3 joburi) |
| Supraviețuire la restart / deploy | NU. Joburile în zbor sunt marcate `error` la pornire, cu credit restituit (`StartupSeed.FailOrphanedInterpretationsAsync`) |
| Limita „1 per utilizator” la scale-out | se aplică per instanță; cu sticky sessions ține, fără ele un user ar putea porni 1 job pe fiecare instanță |
| PDF-ul încărcat | ținut în RAM (`byte[]` în job), max 10 MB / job |
| Serviciu LOINC | un singur worker uvicorn pe `127.0.0.1:8000`, pornit de aplicație |
| Retry după eșec definitiv | nu există; creditul se restituie și userul reîncarcă manual |

## 2. Ce NU e gâtuirea

**Gemini nu e problema.** Un tier plătit este în jur de ~1.000 cereri/minut pe
proiect (tier 2 ~2.000). O interpretare face 1-5 cereri întinse pe 2-4 minute,
deci la 3 joburi simultane consumăm ~1-2 cereri/minut — sub 1% din cotă.
Concluzie: `MaxConcurrent = 3` este de ~10× prea prudent chiar și pe
infrastructura actuală. Se poate urca la 10-20 pe o singură instanță după ce
măsurăm durata reală și consumul de memorie.

## 3. Gâtuirile reale, în ordinea în care vor lovi

1. **Serviciul LOINC (Python)** — un worker, deci potrivirile se serializează.
   Fix: `uvicorn --workers N` sau serviciu separat cu autoscale; modelul de
   embeddings se încarcă o dată per worker (atenție la RAM: ~0,5-1 GB/worker).
2. **Coada în memorie** — pierde joburile la deploy și nu se poate împărți între
   instanțe.
3. **PDF-ul în RAM** — 40 joburi × 10 MB = 400 MB doar pentru fișiere.
4. **SMTP sincron** în interiorul jobului — o întârziere la Brevo blochează un slot.
5. **SQL Server** — scrieri mici, dar `InterpretationHistories.RawJsonResult`
   crește; indexează `(UserEmail, ProfileId, Status, PdfSha256)` și arhivează
   rândurile vechi.

## 4. Arhitectura țintă pe Azure

```
   Browser ──► App Service (Web, N instanțe)
                  │  1. salvează PDF-ul în Blob Storage
                  │  2. rezervă creditul + rând "processing" în SQL
                  │  3. pune mesajul în coadă
                  ▼
        Azure Service Bus / Storage Queue  (coadă durabilă)
                  │
                  ▼
   Container App / WebJob „interpreter” (M instanțe, autoscale pe lungimea cozii)
                  │           │
                  │           └──► LOINC service (Container App, autoscale, K workeri)
                  ▼
             Gemini API ──► PDF ──► email (Brevo) ──► SQL: "success"
```

### Modificări concrete de cod

1. **`IInterpretationJobQueue`** — interfață cu două implementări:
   `InProcessQueue` (dev, cea de azi) și `ServiceBusQueue` (producție).
   `InterpretationController` nu trebuie să se schimbe.
2. **PDF în Blob Storage**, nu în mesaj: jobul cară doar `blobName`.
   Beneficiu imediat: mesajul devine mic, iar jobul poate fi **reluat** după un
   restart, fără să ceară userului să reîncarce fișierul.
3. **Idempotență**: cheia jobului = `HistoryId`. Workerul trebuie să verifice
   `Status == "processing"` înainte să înceapă (Service Bus livrează „at least
   once”, deci un mesaj poate fi procesat de două ori).
4. **Limita „1 per user” devine o interogare SQL** (`EXISTS(processing pentru
   user)`) în loc de un dicționar în memorie — singura variantă corectă la
   scale-out. Se poate păstra dicționarul ca optimizare locală.
5. **Retry gestionat de coadă** (dead-letter după N încercări) în loc de
   restituirea imediată a creditului; creditul se restituie doar când mesajul
   ajunge în dead-letter.
6. **LOINC ca serviciu separat**, cu `BaseUrl` din configurație (deja e) și
   pornire proprie — se elimină `LoincAutoStart` (specific Windows/dev).
7. **Health & metrici**: expune lungimea cozii și durata medie către Application
   Insights; scalează workerii după lungimea cozii, nu după CPU (joburile sunt
   I/O bound, CPU-ul rămâne mic și autoscale-ul pe CPU nu va porni niciodată).

## 5. Dimensionare estimativă

Ipoteză: 100.000 utilizatori, 1 interpretare/lună fiecare ⇒ ~3.300/zi, dar
concentrate seara (vârf ~3× media orară) ⇒ ~600 interpretări/oră în vârf.
La 3 minute per interpretare: **~30 joburi simultane** în vârf.

- 3 instanțe de worker × 10 joburi simultane = 30 → suficient, cu autoscale la 6
  instanțe pentru siguranță.
- Gemini: ~30 joburi × ~2 cereri/minut = ~60 RPM — confortabil chiar și pe tier 1.
- LOINC: ~30 cereri de potrivire/minut → 2-4 workeri.

## 6. Ordinea recomandată a lucrărilor

1. (Făcut) limite configurabile din `appsettings.json`.
2. (Făcut) widget în admin cu „în lucru / la rând”, ca să vedem dacă limita strânge.
3. Măsoară durata reală a unei interpretări pe zile normale → urcă `MaxConcurrent`.
4. Mută PDF-ul în Blob Storage + reluare job după restart (câștig mare, risc mic).
5. Coadă durabilă (Service Bus) + worker separat.
6. LOINC ca serviciu propriu, cu mai mulți workeri.
7. Limita per utilizator pe SQL + idempotență în worker.
