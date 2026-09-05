# Serviciul LOINC — studiu de scalare (iunie 2026)

> Scop: să înțelegem **exact** de ce serviciul Python de potrivire LOINC este gâtuirea
> numărul 1 la hostare (așa cum am notat în `AZURE_SCALING.md` §3) și ce soluții avem,
> ordonate după raportul beneficiu/risc. Documentul conține **măsurători reale**, făcute
> în container, nu estimări din memorie.

---

## 1. Ce face serviciul la fiecare cerere

`POST /loinc/match-batch` (folosit de C# — un apel per buletin, nu unul per analiză):

1. **Encoding**: toate numele de analize sunt vectorizate într-un singur apel
   `SentenceTransformer.encode(...)` — model `all-MiniLM-L6-v2` (22M parametri, 384 dim),
   rulat pe CPU prin PyTorch.
2. **Scanare de similaritate**: pentru FIECARE analiză se calculează produsul scalar cu
   **toată** matricea dicționarului (97.000 × 384 float32 = **142 MB**), apoi `argpartition`
   pentru top 25 candidați.
3. **Re-rank**: fuzzy (RapidFuzz) + reguli + scor pe axe LOINC peste cei 25 candidați.
4. Post-corecție pe unitate (`_find_peer_with_property`).

Pașii 1-3 sunt **CPU-bound** și rulează în `ThreadPoolExecutor(max_workers=8)`.
Endpoint-urile sunt `def` (sincrone), deci FastAPI le trimite în threadpool-ul Starlette
(40 de fire implicit).

## 2. Măsurători reale (container 16 vCPU, PyTorch CPU)

`python3 /app/memory/probes/loinc_capacity_probe.py`

| Măsurătoare | Valoare |
|---|---|
| `import sentence_transformers` | **3.257 ms** |
| Încărcare model (rece) | **3.558 ms** |
| `encode()` pentru 1 nume (apel separat) | **207 ms** |
| `encode()` pentru 90 de nume (batch) | **1.305 ms** ⇒ **14,5 ms/nume** |
| Scanare similaritate pe 97k rânduri | **45,4 ms** per analiză |
| RSS după încărcarea modelului | **855 MB** |
| RSS cu matricea de 97k inclusă | **1.191 MB** |
| Matricea singură | 142 MB |

Trei concluzii care schimbă tot planul:

1. **Batch-ul e obligatoriu** (deja implementat): 14,5 ms vs 207 ms per nume — de 14× mai
   rapid. Nu regresa niciodată la `/loinc/match` unul-câte-unul.
2. **Un worker costă ~0,9-1,2 GB RAM**, aproape tot din PyTorch, nu din date. Deci
   `uvicorn --workers 4` = **~4 GB** — scump și inutil. Scalarea corectă e pe **replici mici**,
   nu pe workeri în același container (sau `gunicorn --preload` ca să se partajeze matricea).
3. **Scanarea e limitată de banda de memorie**, nu de CPU: fiecare analiză citește 142 MB.
   40 de analize = **5,7 GB citiți din RAM** per buletin. De aceea cele 8 fire din
   `ThreadPoolExecutor` nu se scalează liniar — se bat pe aceeași bandă de memorie.

### Cost estimat per buletin (40 de analize)

| Mediu | Encoding | Scanare | Fuzzy+reguli | **Total** |
|---|---|---|---|---|
| Container 16 vCPU (măsurat aici) | 0,6 s | 1,8 s | ~0,5 s | **~3 s** |
| Container 2 vCPU (Azure, estimat 2,5-3×) | ~1,7 s | ~4 s | ~1 s | **~7 s** |
| Container 1 vCPU (estimat 6-8×) | ~4 s | ~9 s | ~2 s | **~15 s** |

## 3. De ce e gâtuirea (și cum se manifestă)

| Problemă | Efect la hostare |
|---|---|
| **Un singur proces, muncă CPU-bound** | 10 buletine simultane ⇒ fiecare durează de ~10× mai mult (nu se pierde nimic, dar se serializează) |
| **GIL + threadpool de 40 de fire** | `/ready` și `/health` stau la coadă în spatele muncii grele ⇒ **alarma falsă „Serviciul LOINC nu e disponibil”** (exact bug-ul reparat în iunie prin `ProbeTimeoutMs=3000` + 2 ratări consecutive). La hostare revine, amplificat |
| **Zero cache de rezultate** | „Hemoglobina glicata” e recalculată de la zero pentru fiecare utilizator, la fiecare buletin. Numele de analize se repetă masiv — asta e risipa cea mai mare din tot sistemul |
| **Encoding inutil pentru ancore** | `match-batch` vectorizează TOATE numele, inclusiv cele 143 care se rezolvă determinist prin `canonical_anchors` (score 1.0, fără embeddings) |
| **Cold start 8-10 s** | orice replică nouă (autoscale, deploy) e „not ready” ~10 s; cu `minReplicas=0` fiecare utilizator care vine primul plătește pauza |
| **`LoincAutoStart` e specific Windows** | `StartCommand` cu PowerShell și `C:\Projects\...` — nu are sens în container; trebuie dezactivat la hostare |
| **Fără autentificare** | serviciul acceptă orice cerere de la oricine îi vede portul; pe Azure trebuie ingress intern / API key |
| **Log INFO pe fiecare cerere** cu numele analizei | volum mare de loguri + date de pacient în telemetrie |
| **Timeout C# de 5 s** (`LoincMatcher:TimeoutSeconds`) ×3 pentru batch | pe o replică de 1 vCPU încărcată, un buletin de 40 de analize depășește pragul ⇒ interpretări fără coduri LOINC |

**Dimensionare la 100.000 utilizatori** (ipoteza din `AZURE_SCALING.md`: ~600 interpretări/oră
în vârf = 10/minut): pe replici de 2 vCPU, un buletin costă ~7 s CPU ⇒ **~70 s CPU/minut** ⇒
**2 replici** acoperă vârful, 3 dau marjă. Deci problema NU e „nu facem față”, ci:
*latență în creștere, alarme false de disponibilitate și risipă de 10-20× de resurse.*

---

## 4. Soluții, ordonate după beneficiu/risc

### S1. Cache persistent de mapări (în C#, ÎNAINTE de apelul HTTP) — **impact maxim**
Tabel nou `LoincMatchCache`:

```
Key (PK)      = SHA256(nume normalizat | unitate | panel_header | analyte_line | PipelineVersion)
LoincCode, LongName, Class, Score, Source, AxisVerdictJson, CreatedAt, HitCount
```

- C# caută în cache; trimite la Python **numai** analizele necunoscute.
- `PipelineVersion` în cheie ⇒ când schimbi ponderile (`LOINC_SEM_WEIGHT` etc.) sau ancorele,
  bump la versiune și cache-ul vechi devine inert (nu trebuie șters).
- Hit rate realist după câteva săptămâni: **>90%** (numele de analize din laboratoarele
  românești sunt un set de câteva mii, foarte repetitiv).
- Efecte: încărcarea serviciului LOINC scade de ~10-20×, interpretarea devine cu ~5 s mai
  rapidă, iar **dacă serviciul LOINC e picat, buletinele cunoscute se codifică oricum**
  (degradare grațioasă reală, nu doar un banner).
- Risc: mic. Cache doar pentru rezultate deja validate; cheia include tot contextul care
  influențează decizia.

### S2. Cache LRU în proces + fără encoding pentru ancore (în Python) — **ieftin, imediat**
- `functools.lru_cache` (sau dict cu limită) pe `(nume_normalizat, unitate, context_hash)`
  în `find_loinc` ⇒ repetițiile din același buletin și din buletine consecutive devin gratuite.
- În `match-batch`: rezolvă prima ancorele deterministe și **vectorizează doar restul**.
- `max_workers = os.cpu_count()` în loc de 8 fix (pe 1-2 vCPU, 8 fire doar se calcă pe picioare).
- `/health` și `/ready` devin `async def` ⇒ nu mai stau la coadă în spatele muncii grele
  ⇒ **dispar alarmele false**.
- Risc: foarte mic, zero schimbare de rezultate (aceleași vectori, aceleași coduri).

### S3. Scalare orizontală corectă (la hostare)
Azure Container Apps (recomandat) sau App Service Linux container:

```yaml
resources:  { cpu: 2.0, memory: 4Gi }     # NU 0.5 vCPU: modelul e CPU-bound
scale:      { minReplicas: 1, maxReplicas: 5 }
rules:      - http: { concurrentRequests: 2 }   # un buletin ocupă efectiv toată replica
ingress:    internal          # doar aplicația o vede, fără expunere publică
probes:     liveness /health (10s)  |  readiness /ready (initialDelay 30s)
env:        LOINC_HOST=0.0.0.0, LOINC_PORT=8000, LOINC_TOP_K, ponderile
command:    uvicorn main:app --host 0.0.0.0 --port 8000 --workers 1
```

- `minReplicas: 1` (nu 0) — altfel plătim cold start-ul de 10 s la fiecare trezire.
- **Un worker per container**, scalare pe replici (RAM: 855 MB/worker).
- Dacă chiar vrei 2 workeri într-un container: `gunicorn -k uvicorn.workers.UvicornWorker
  --preload -w 2` — cu `--preload` matricea de 142 MB se partajează prin copy-on-write.
- În aplicația C#: `LoincMatcher:BaseUrl` = numele intern al serviciului,
  `TimeoutSeconds` urcat la 15-20 (batch-ul înmulțește ×3), `LoincAutoStart:Enabled = false`.

### S4. Model mai ieftin: ONNX Runtime + int8 (opțional, măsurabil)
PyTorch e responsabil de aproape tot cei 855 MB. `all-MiniLM-L6-v2` exportat în ONNX și
cuantizat int8 rulează în ~100-150 MB și, tipic, de 2-3× mai rapid pe CPU.
- Beneficiu: replici de 1 vCPU / 1 GiB devin viabile ⇒ cost de infrastructură mult mai mic.
- Condiție obligatorie: re-rularea suitei `test_pipeline_smoke.py` (509 linii) și compararea
  codurilor rezultate **cod cu cod**. Cuantizarea schimbă vectorii la a 3-a zecimală; trebuie
  să dovedim că nu schimbă nici o decizie.

### S5. Index ANN (hnswlib / faiss) în loc de scanarea completă (opțional)
45 ms → <1 ms per analiză, și dispare presiunea pe banda de memorie (5,7 GB citiți per buletin).
- Beneficiu real doar după S1+S2 (altfel encoding-ul rămâne dominant).
- Risc: recall-ul ANN nu e 100%. Se acceptă doar cu dovada că top-25 conține aceiași candidați
  ca scanarea exactă pe toată suita de test.

### S6. Igienă operațională (la hostare)
- Log per-cerere: INFO → DEBUG; păstrăm doar agregate (număr de analize, ms, câte potrivite).
- API key (`X-Loinc-Key`) sau ingress intern — obligatoriu unul din cele două.
- Metrici către Application Insights: durata batch-ului, hit rate cache, ms/analiză.
- Seed-ul de embeddings (`seed_embeddings.py`) rulat la build, iar `.npy` + `.json` **incluse
  în imagine** (nu generate la pornire, nu citite din SQL în hot-path).

---

## 5. Ce recomand, pe faze

| Fază | Ce | Când | Risc |
|---|---|---|---|
| **0** ✅ IMPLEMENTAT (iunie 2026) | cache LRU în proces, dedupare în batch, `max_workers=cpu_count`, `/health` + `/ready` async, `/loinc/cache` | acum — ajută și pe local | foarte mic |
| **1** ✅ IMPLEMENTAT (iunie 2026) | cache persistent global în SQL (`LoincMatchCache`), comutabil din config | acum, activ și local | mic |
| **2** | S3 + S6 (Dockerfile, Container App, ingress intern, timeouts, `LoincAutoStart=false`) | la deploy | mediu (config) |
| **3** | S4 / S5 (ONNX int8, ANN) | doar dacă factura sau latența o cer, cu suita de test ca arbitru | mediu-mare |

### Ce s-a implementat concret în fazele 0 și 1

**Python (`/app/loinc_service`)**
- `pipeline.py`: cache LRU în proces (`cache_key` / `cache_lookup` / `cache_store` /
  `cache_stats` / `cache_clear`), cu cheia = (nume, unitate, nume brut, panel header, linia
  analitului). Capacitate `LOINC_CACHE_SIZE` (implicit 20.000; 0 = dezactivat).
- `loinc_store.py`: `STORE.load()` golește cache-ul ⇒ imposibil să servești un răspuns
  calculat pe alt dicționar.
- `main.py`: `match-batch` vectorizează doar analizele necunoscute, **dedupează** întrebările
  identice din același buletin, folosește `min(cpu_count, n)` fire; `/health` și `/ready` sunt
  `async` (nu mai stau la coadă în spatele muncii grele ⇒ fără alarme false); `/ready` și noul
  `/loinc/cache` raportează hit rate-ul.

**C# (`/app/MedicalApp`)**
- `Models/LoincMatchCacheEntry.cs` + tabelul `LoincMatchCache` (migrarea `AddLoincMatchCache`).
- `Services/LoincMatchCacheStore.cs`: cheie SHA-256 peste (versiune | nume | unitate | nume brut |
  panel header | linia analitului); citire/scriere în **scope propriu de DbContext** (nu poate
  salva modificările altcuiva), orice eroare e înghițită și logată.
- `Services/LoincMatcherClient.cs`: întreabă întâi cache-ul, trimite la Python **doar** ce nu
  știe, memorează ce primește, incrementează `HitCount`/`LastUsedAt`. Dacă tot buletinul e
  cunoscut, nu se face nici un apel HTTP.
- Configurație: `LoincMatcher:Cache:Enabled` (implicit `true`) și
  `LoincMatcher:Cache:PipelineVersion` (implicit `v1` — bump la orice schimbare de ponderi /
  ancore / re-seed al dicționarului).

Verificat: probă Python **20/20 PASS** (`loinc_cache_probe.py`), probă C# **33/33 PASS**
(`LoincMatchCacheProbe.cs.txt`), suita LOINC de aur **56/56 PASS** neschimbată
(`test_pipeline_smoke.py`), regresie B2C **66/66 PASS**, build 0 warning-uri.

Rezultatul fazelor 0+1: **serviciul LOINC încetează să fie gâtuire** înainte de a atinge
infrastructura — pentru că 9 din 10 analize nici nu mai ajung la el.

### Corecție iunie 2026: cheia de cache trebuie să fie STABILĂ

Prima versiune a cheii hașura și **normalizarea în engleză produsă de Gemini**. Măsurat pe două
interpretări ale aceluiași buletin: **hit rate 0%** și 122 de rânduri în loc de 61, pentru că
modelul reformulează la fiecare rulare — *„Hematocrit [Volume Fraction] in Blood”* vs
*„…in Blood by Automated count”*, *„Carcinoembryonic Ag”* vs *„…antigen … immunoassay”* —
ajungând totuși la **același cod LOINC**.

Cheia actuală conține doar ce e stabil:
1. **numele analizei exact cum e tipărit în buletin**, în limba lui (`Glicemie`, `Glukose`,
   `Glycémie`) — cache-ul se partiționează astfel singur pe limbă;
2. **unitatea canonizată** (`µL` = `uL`, fără spații, case-insensitive);
3. **markerii decisivi de specimen și metodă** găsiți în titlul secțiunii și în linia
   analitului — și NIMIC din restul textului liber;
4. versiunea pipeline-ului (`LoincMatcher:Cache:PipelineVersion`, acum `v2`).

**Multilingvismul (20+ limbi) — o singură sursă de adevăr.** Vocabularul de markeri NU e
duplicat în C#. Serviciul Python îl expune la **`GET /loinc/context-keywords`** (160 de fraze:
uniunea declanșatorilor din stratul de reguli plus `_CACHE_KEY_EXTRA_PHRASES`, adăugările în
limbile native pentru specimen). C# îl ia o dată per proces (`LoincContextVocabulary`), îl
**persistă în tabelul `LoincVocabulary`** și îl restaurează de acolo dacă serviciul Python e
oprit — altfel un restart al aplicației în timpul unei pene ar schimba forma tuturor cheilor
exact când cache-ul e singurul care mai poate coda un buletin. Fără vocabular nicăieri, cheia
include tot textul de context (`full:…`): mai puține hit-uri, niciodată o refolosire greșită.

Potrivirea frazelor: prefix de cuvânt pentru frazele ≥ 5 caractere (`sange` prinde `sangele`,
`urina` prinde `urinar`), cuvânt întreg pentru cele scurte (`ser` NU se mai găsește în `seria`),
secvență de cuvinte pentru frazele compuse (`cytometrie en flux`). Diacriticele sunt eliminate cu
același algoritm ca în Python, deci `impedanță` = `impedanta`, `sérique` = `serique`.

Principiu de proiectare: **cheia e exact la fel de discriminantă ca stratul de reguli al
potrivitorului** — nici mai mult, nici mai puțin. Dacă un marker nu e văzut de Python, nu schimbă
codul LOINC, deci nu are ce căuta în cheie.

Coloană nouă `LoincMatchCache.KeyMaterial`: exact textul hașurat, lizibil
(`v2|hemoglobina|g/dl|impedanta|sange`). Orice nepotrivire viitoare se diagnostichează cu o
singură interogare SQL.

Migrare EF: **`AddLoincCacheKeyMaterialAndVocabulary`**.

Verificat: probă C# **55/55 PASS** (inclusiv reformulare Gemini ⇒ HIT; impedanță vs citometrie în
flux RO/FR/DE/PL ⇒ MISS; ser vs urină RO/FR ⇒ MISS; Westergren vs microfotometric ⇒ MISS; altă
limbă ⇒ MISS; diacritice și reformulări nesemnificative ⇒ HIT; vocabular indisponibil ⇒ prudent;
Python oprit de la start ⇒ vocabular restaurat din baza de date; cache oprit ⇒ ca înainte),
regresie B2C **66/66**, unificator **24/24**, suita de aur LOINC **56/56**, cache Python
**21/21**, build 0 warning-uri.

## 6. Cost estimativ (Azure Container Apps, Europa de Vest)
| Configurație | Cost lunar aproximativ |
|---|---|
| 1 replică 2 vCPU / 4 GiB, mereu pornită | ~55-70 € |
| 2 replici (vârf, ~4h/zi) | +~10-15 € |
| După S4 (1 vCPU / 1 GiB, 1-2 replici) | ~20-30 € |

(Consumul e cvasi-liniar în vCPU-secunde; cifrele sunt pentru orientare, nu ofertă.)
