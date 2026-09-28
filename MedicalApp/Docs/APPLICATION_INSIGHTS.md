# Application Insights — ghid pas cu pas (pentru începători)

> **Ce este?** Application Insights este serviciul Azure care „se uită" în aplicație în timp real:
> câte cereri primește, cât durează, ce erori apar, cât durează apelurile la Gemini, câte
> interpretări stau în coadă, câte plăți s-au făcut. Fără el, când un utilizator zice „nu merge",
> ghicești. Cu el, vezi exact ce s-a întâmplat, la ce oră, pe ce instanță.

> **Ce s-a făcut deja în cod (iunie 2026)** — nu mai ai nimic de programat:
> - pachetul NuGet `Microsoft.ApplicationInsights.AspNetCore` **3.1.2** (bazat pe OpenTelemetry);
> - `Program.cs` pornește monitorizarea **doar dacă există connection string** → local, în
>   Visual Studio, nu se schimbă absolut nimic (nu se face niciun apel de rețea);
> - **Adaptive Sampling** activat: `TracesPerSecond = 5` (valoarea implicită Microsoft);
> - logurile trimise: **doar Warning + Error** (cost minim) + liniile de plăți;
> - metrici de business în `Services/AppTelemetry.cs`: apeluri Gemini (durată, 429, tokeni),
>   coada B2C, loturi CAM, plăți Stripe.

---

## Cuprins
1. [Creezi resursa în portal](#1-creezi-resursa-application-insights) (5 min)
2. [Copiezi connection string-ul](#2-copiezi-connection-string-ul)
3. [Îl pui în App Service](#3-pui-connection-string-ul-în-app-service)
4. [Verifici că vin date](#4-verifici-că-vin-date)
5. [Unde te uiți zi de zi](#5-unde-te-uiți-zi-de-zi)
6. [Adaptive Sampling — ce e, cum se reglează](#6-adaptive-sampling)
7. [Metricile aplicației (Gemini, cozi, plăți)](#7-metricile-aplicației)
8. [Alerte pe email](#8-alerte-pe-email)
9. [Costuri și plafon zilnic](#9-costuri)
10. [Probleme frecvente](#10-probleme-frecvente)

---

## 1. Creezi resursa Application Insights

1. Intră pe [portal.azure.com](https://portal.azure.com) → sus, în căsuța de căutare, scrie
   **Application Insights** → click pe serviciu → **+ Create**.
2. Completează:
   | Câmp | Ce pui | De ce |
   |---|---|---|
   | **Subscription** | abonamentul tău | |
   | **Resource group** | **același** resource group în care e App Service-ul | ca să le vezi și să le ștergi împreună |
   | **Name** | `mymedicalapp-insights` | orice nume, doar litere/cifre/cratimă |
   | **Region** | **aceeași regiune** ca App Service (ex. *West Europe*) | latență mică, fără costuri de transfer între regiuni |
   | **Workspace** | lasă-l să creeze unul nou (*Log Analytics workspace*) sau alege unul existent | acolo se stochează fizic datele; îl vei folosi la Logs (KQL) |
3. **Review + create** → **Create**. Durează ~1 minut.

> Nu bifa nimic la „Enable Application Insights" din pagina **App Service** (vezi §10, punctul
> „date duble"). Noi folosim SDK-ul din cod, nu agentul automat.

## 2. Copiezi connection string-ul

1. Deschide resursa creată → pagina **Overview**.
2. În dreapta sus vezi **Connection String** → click pe iconița de copiere.
   Arată așa: `InstrumentationKey=xxxxxxxx-xxxx-...;IngestionEndpoint=https://westeurope-x.in.applicationinsights.azure.com/;...`
3. **Nu** este o parolă în sensul clasic (nu dă acces la date), dar nu o publica: oricine o are
   poate trimite date false în monitorizarea ta. **Nu o pui în `appsettings.json`** — o pui în
   portal, ca la toate celelalte setări.

## 3. Pui connection string-ul în App Service

1. Portal → **App Service-ul tău** → meniul din stânga **Settings → Environment variables**
   (la unele conturi se numește încă *Configuration → Application settings*).
2. **+ Add**:
   - **Name:** `APPLICATIONINSIGHTS_CONNECTION_STRING`
   - **Value:** connection string-ul copiat la pasul 2
3. **Apply** → **Confirm**. App Service repornește singur aplicația (~30 s).

Asta e tot. `Program.cs` vede variabila și pornește Application Insights. Dacă variabila
lipsește, aplicația pornește normal, fără monitorizare — deci nu poți „strica" nimic.

> Setări opționale (tot aici, dacă vrei să le schimbi fără rebuild):
> | Name | Value implicit | Ce face |
> |---|---|---|
> | `ApplicationInsights__TracesPerSecond` | `5` | Adaptive Sampling — vezi §6 |
> | `ApplicationInsights__EnableTraceBasedLogsSampler` | `false` | `true` = și logurile Warning/Error se aruncă odată cu cererea eșantionată. Lasă `false`. |
> | `Logging__OpenTelemetry__LogLevel__Default` | `Warning` | pune `Information` **temporar** la depanare (crește volumul mult) |

## 4. Verifici că vin date

1. Deschide aplicația în browser (câteva pagini, un login).
2. Portal → resursa Application Insights → **Investigate → Live metrics**.
   - În **maxim 1 minut** trebuie să vezi în dreapta *Servers: 1* (instanța ta) și graficele
     *Incoming Requests* mișcându-se când dai refresh în aplicație.
   - Live metrics **nu** e eșantionat și **nu** costă — e ideal pentru „e viu sau nu?".
3. După **2–5 minute** (atât durează ingestia normală) → **Investigate → Transaction search**:
   vezi lista cererilor (`GET /Interpretation/History` etc.). Click pe una → vezi și apelurile
   SQL / HTTP făcute de ea.
4. Fă o interpretare de test. În **Transaction search** filtrează pe *Dependency* → apare
   `POST /v1beta/models/gemini-...:generateContent` cu durata. **Cheia API nu apare**: SDK-ul
   înlocuiește automat valorile din query string cu `Redacted`.

Dacă nu apare nimic după 5 minute → §10.

## 5. Unde te uiți zi de zi

| Vrei să știi… | Deschizi | Ce vezi |
|---|---|---|
| E ceva stricat **acum**? | **Investigate → Failures** | erori 5xx, excepții grupate pe tip, apeluri Gemini/SQL eșuate. Click pe un grup → *Samples* → stack trace complet |
| Ce e **lent**? | **Investigate → Performance** | fiecare pagină cu durata medie/P95; *Dependencies* = cât din timp e Gemini, cât SQL |
| Câți utilizatori, ce pagini | **Usage → Users / Events** | |
| Un caz concret („ieri la 14:20 userul X a primit eroare") | **Transaction search** + interval de timp | cererea exactă, cu toate logurile Warning/Error legate de ea |
| Grafice cu **metricile tale** | **Monitoring → Metrics** | §7 |
| Întrebări libere | **Monitoring → Logs** (KQL) | §7 — interogări gata scrise |

Interogări utile în **Logs** (copy/paste):

```kusto
// Cele mai lente pagini, ultima oră
requests
| where timestamp > ago(1h)
| summarize cereri = sum(itemCount), medie_ms = avg(duration), p95_ms = percentile(duration, 95) by name
| order by p95_ms desc
```

```kusto
// Erorile din ultimele 24 h, grupate
exceptions
| where timestamp > ago(24h)
| summarize n = count() by type, outerMessage
| order by n desc
```

```kusto
// Apeluri Gemini eșuate (429 = cotă depășită, 503 = Google supraîncărcat)
dependencies
| where timestamp > ago(24h) and target has "generativelanguage"
| summarize apeluri = sum(itemCount), esuate = countif(success == false), medie_s = avg(duration) / 1000 by resultCode
```

```kusto
// Toate plățile (liniile Information din CreditsController ajung aici)
traces
| where timestamp > ago(30d) and message startswith "Payment ("
| project timestamp, message
| order by timestamp desc
```

## 6. Adaptive Sampling

### Ce problemă rezolvă
Fiecare cerere HTTP, fiecare apel SQL, fiecare apel Gemini = un rând trimis în Azure. La trafic
mare asta înseamnă GB de date pe lună, plătiți la GB. **Sampling** = păstrezi un eșantion
reprezentativ și arunci restul, dar **statistic corect**: Azure știe câte rânduri reprezintă
fiecare rând păstrat (coloana `itemCount`), așa că graficele de *count* și *avg* rămân exacte.

### Ce înseamnă „adaptive"
În SDK-ul 3.x (OpenTelemetry) sampling-ul adaptiv se numește **rate-limited sampling**:
`TracesPerSecond = 5` = „păstrează **maximum 5 cereri pe secundă** per instanță".
- Trafic mic (sub 5 cereri/s — cazul normal al aplicației) → **se păstrează 100 %**, nu pierzi nimic.
- Vârf de trafic → procentul scade automat (ex. la 50 cereri/s păstrează ~10 %) și revine la
  100 % când traficul scade. De aici „adaptiv": se adaptează singur la trafic.
- O cerere păstrată e păstrată **întreagă**: cu apelurile ei SQL/Gemini și logurile ei. Nu vezi
  niciodată „jumătate" de tranzacție.

### Ce NU se eșantionează niciodată (setat în cod)
- **Metricile** (§7) — sunt numere agregate, nu rânduri; sampling-ul nu le atinge.
- **Logurile Warning / Error** — `EnableTraceBasedLogsSampler = false` înseamnă că un log de
  eroare ajunge în Azure chiar dacă cererea lui a fost aruncată de sampling.
- **Live metrics** — citește direct din proces.

### Cum verifici cât se păstrează
```kusto
requests
| where timestamp > ago(1d)
| summarize reale = sum(itemCount), pastrate = count()
| extend procent_pastrat = round(100.0 * pastrate / reale, 1)
```
`procent_pastrat = 100` = niciun sampling activ (normal la trafic mic).

### Cum reglezi
- Portal → App Service → Environment variables → `ApplicationInsights__TracesPerSecond`:
  - `5` — implicit, bun pentru lansare;
  - `10`–`20` — temporar, când depanezi ceva și vrei toate cererile;
  - `2` — dacă factura crește și îți ajung statisticile.
- Alternativ, în `appsettings.Azure.json` → `ApplicationInsights.TracesPerSecond` (necesită deploy).
- **Nu** activa și *Data sampling* din portal (*Configure → Usage and estimated costs → Data
  sampling*, numit „ingestion sampling"): las-o pe **100 %**. Două sampling-uri suprapuse strică
  statisticile.

### Dacă vrei procent fix în loc de adaptiv
`ApplicationInsights__SamplingRatio = 0.25` (25 %) ar înlocui rate-limiting-ul, dar nu e
implementat în `Program.cs` intenționat: la trafic mic ai arunca 75 % din date degeaba.
Adaptivul e alegerea corectă pentru aplicația asta.

## 7. Metricile aplicației

Sunt emise din `Services/AppTelemetry.cs` și apar în **Monitoring → Metrics** → *Metric
Namespace* = **azure.applicationinsights** (sau „Log-based metrics") → *Metric* = numele de mai jos.
Pentru fiecare poți alege *Aggregation* (Sum / Avg / Max) și *Apply splitting* pe un tag.

| Metric | Unitate | Tag-uri (splitting) | Ce îți spune |
|---|---|---|---|
| `gemini.calls` | apeluri | `model`, `outcome` = `ok` / `rate_limited` (429) / `unavailable` (503) / `model_retired` / `cancelled` / `error` | sănătatea Gemini. **`rate_limited` în creștere = cota Google e prea mică** → urcă Tier-ul sau coboară `Gemini:RateLimit` |
| `gemini.call.duration` | ms | `model`, `outcome` | cât durează un apel, **inclusiv** așteptarea în limitatorul intern. Dacă crește dar Gemini e la fel de rapid → coada internă e plină |
| `gemini.tokens` | tokeni | `model`, `kind` = `input` / `output` / `thinking` | consum ⇒ cost Google; îl compari cu factura |
| `interpretation.b2c.queue.waiting` | joburi | — | **câte interpretări B2C aşteaptă** un slot liber (per instanță). Constant > 0 ⇒ urcă `InterpretationQueue:MaxConcurrent` |
| `interpretation.b2c.queue.active` | joburi | — | așteptate + în lucru |
| `interpretation.b2c.jobs` | joburi | `outcome` = `completed` / `crashed` | debit; `crashed` trebuie să fie 0 |
| `interpretation.b2c.duration` | secunde | `outcome` | cât durează o interpretare de la preluare la final (sursa pentru ETA) |
| `cam.batches` | loturi | `outcome` | loturi B2B terminate |
| `cam.batch.duration` | secunde | `outcome` | cât durează un lot CAM |
| `payments.completed` | plăți | `provider`, `package` | câte cumpărări |
| `payments.amount` | EUR | `provider`, `package` | **venit** |
| `payments.credits` | credite | `provider`, `package` | credite vândute |

> Coada CAM (B2B) nu are gauge de „așteptare" pentru că trăiește în SQL, nu în memorie; o vezi
> deja în *Admin* și în `cam.batches` / `cam.batch.duration`.

Aceleași metrici în **Logs** (tabela `customMetrics`; `valueSum`/`valueCount` pentru medii):

```kusto
// Rata de 429 pe ore
customMetrics
| where timestamp > ago(7d) and name == "gemini.calls"
| extend outcome = tostring(customDimensions["outcome"])
| summarize apeluri = sum(value) by outcome, bin(timestamp, 1h)
| render timechart
```

```kusto
// Durata medie a unui apel Gemini (secunde), pe 15 minute
customMetrics
| where timestamp > ago(1d) and name == "gemini.call.duration"
| summarize medie_s = sum(valueSum) / sum(valueCount) / 1000 by bin(timestamp, 15m)
| render timechart
```

```kusto
// Coada B2C: câți așteaptă (maxim pe 5 minute)
customMetrics
| where timestamp > ago(1d) and name == "interpretation.b2c.queue.waiting"
| summarize asteapta = max(value) by bin(timestamp, 5m)
| render timechart
```

```kusto
// Venit pe zi
customMetrics
| where timestamp > ago(90d) and name == "payments.amount"
| summarize EUR = sum(value) by bin(timestamp, 1d)
| render columnchart
```

**Dashboard:** în orice grafic din *Metrics* → **Pin to dashboard** → ai un panou cu erorile,
coada, Gemini și veniturile pe o singură pagină (*Dashboard* din meniul principal al portalului).

## 8. Alerte pe email

Prima dată creezi „cine primește mailul", apoi „când".

### 8.1 Action group (o singură dată)
1. Portal → **Monitor** (caută „Monitor") → **Alerts** → **Action groups** → **+ Create**.
2. *Basics*: Resource group = al aplicației; **Action group name** = `mymedicalapp-admins`;
   **Display name** = `MMA` (max 12 caractere, apare în subiectul mailului).
3. *Notifications*: **Notification type** = *Email/SMS message/Push/Voice* → bifezi **Email** →
   adresa ta → **Name** = `email-admin`. Poți adăuga și **SMS** (contra cost mic).
4. **Review + create**. Primești imediat un mail de confirmare „You've been added…".

### 8.2 Regulile recomandate
Toate se creează din **resursa Application Insights → Monitoring → Alerts → + Create → Alert rule**.
La *Actions* alegi de fiecare dată action group-ul `mymedicalapp-admins`.

| # | Alertă | *Condition* | Prag | *Severity* | De ce |
|---|---|---|---|---|---|
| 1 | **Erori de server** | *Signal* = `Failed requests` (metric) → Aggregation **Count**, Operator **Greater than**, Threshold **5**, *Check every* 5 min, *Lookback* 5 min | > 5 în 5 min | 1 – Error | ceva e stricat pentru mai mulți utilizatori |
| 2 | **Aplicația nu răspunde** | vezi 8.3 (Availability test) | 2 locații eșuate | 0 – Critical | site-ul e jos |
| 3 | **Excepții** | *Signal* = `Exceptions` → Count > **10** în 15 min | | 2 – Warning | erori prinse în cod, dar frecvente |
| 4 | **Gemini 429** | *Signal type* = **Custom log search** → interogarea de mai jos → *Threshold* **> 10** (rezultat), *Frequency* 15 min | > 10 în 15 min | 2 – Warning | ai depășit cota Google — utilizatorii așteaptă |
| 5 | **Coadă B2C aglomerată** | Custom log search → interogarea de mai jos → > **5** | | 3 – Informational | urcă `MaxConcurrent` / instanțe |
| 6 | **Răspuns lent** | *Signal* = `Server response time` → **Average** > **5000** ms în 15 min | | 3 | lentoare generală (SQL? instanță mică?) |

Interogare pentru #4:
```kusto
customMetrics
| where name == "gemini.calls" and tostring(customDimensions["outcome"]) == "rate_limited"
| summarize n = sum(value)
| where n > 10
```
Interogare pentru #5:
```kusto
customMetrics
| where name == "interpretation.b2c.queue.waiting"
| summarize asteapta = max(value)
| where asteapta > 5
```
> La *Custom log search*: **Measure** = `Table rows`, **Aggregation** = `Count`, **Operator** = `Greater than`, **Threshold** = `0`
> (interogarea deja filtrează cu `where`, deci „orice rând returnat" = alertă).

### 8.3 Availability test (aplicația e sus?)
1. Resursa Application Insights → **Investigate → Availability** → **+ Add Standard test**.
2. **Test name** = `healthz`; **URL** = `https://<domeniul-tău>/healthz` (endpoint-ul răspunde `ok`
   fără să atingă SQL/Gemini — e făcut pentru asta); **Test frequency** = 5 min;
   **Test locations** = alege 3–5 (ex. West Europe, North Europe, France Central, UK South).
3. *Success criteria*: **HTTP response** = 200; **Content match** = `ok`.
4. *Alerts*: **Enabled**, **Alert location threshold** = 2 din locații (evită alarmele false),
   *Action group* = `mymedicalapp-admins`.
5. Salvează. În pagina *Availability* vezi un grafic cu procentul de uptime — bun și pentru clienți B2B.

### 8.4 Testul alertelor
La regula #1 poți pune temporar Threshold = 0 și deschide o pagină inexistentă de 6 ori
(`/nu-exista` dă 404, nu 5xx — folosește în schimb un cont fără drepturi pe `/Admin` sau
oprește temporar SQL-ul). Mailul vine în 1–5 minute. **Nu uita să pui pragul la loc.**

## 9. Costuri

- **Ingestie**: primii **5 GB/lună gratis** per workspace, apoi ≈ **2,3–2,8 €/GB** (*Pay-as-you-go*).
  La setările din acest ghid (Warning+, sampling 5/s), aplicația cu câteva mii de interpretări/lună
  consumă de regulă **sub 1 GB** → **0 €**.
- **Retenție**: 90 de zile gratis (interactive). Peste → cost mic per GB/lună.
- **Live metrics, Metrics standard, alertele pe metrici**: gratis. **Alertele pe *Custom log
  search*** (#4, #5): ≈ 1,3 €/lună fiecare la frecvență 15 min. **Availability test**: gratis
  până la 100 teste.
- Ce umflă factura: `Logging__OpenTelemetry__LogLevel__Default = Information` uitat activ,
  `TracesPerSecond` foarte mare, excepții în buclă (o excepție la fiecare cerere).

**Plafon zilnic (obligatoriu la lansare):** resursa Application Insights → **Configure → Usage
and estimated costs → Daily cap** → **0,5 GB/zi** (sau 1 GB). Când e atins, Azure oprește
ingestia până la miezul nopții (UTC) și îți trimite mail — o buclă de erori nu îți poate face
o factură surpriză. Tot acolo: **Data retention** = 90 zile.

## 10. Probleme frecvente

| Simptom | Cauză probabilă | Ce faci |
|---|---|---|
| Nimic în *Live metrics* după 2 min | variabila nu e citită | verifică numele **exact** `APPLICATIONINSIGHTS_CONNECTION_STRING` (fără spații) și că ai dat **Apply**; App Service → **Restart** manual |
| Live metrics merge, dar *Transaction search* e gol | ingestia normală durează 2–5 min | așteaptă; schimbă *Time range* pe *Last 30 minutes* |
| Apar **două** rânduri pentru fiecare cerere / Live metrics arată 2 servers deși ai 1 instanță | e pornit **și** agentul automat („Enable Application Insights" din App Service, variabila `ApplicationInsightsAgent_EXTENSION_VERSION`) | App Service → *Application Insights* → **Turn off** sau șterge `ApplicationInsightsAgent_EXTENSION_VERSION`; SDK-ul din cod ajunge |
| În *Logs*, tabela `traces` e goală | e normal: trimitem doar Warning+; dacă nu ai erori, e goală | pentru investigații pune temporar `Logging__OpenTelemetry__LogLevel__Default = Information`, apoi revino |
| Metricile `gemini.*` nu apar | nu s-a făcut nicio interpretare de la restart / metricile se exportă la ~60 s | fă o interpretare, așteaptă 2 min; în *Metrics* schimbă *Metric Namespace* |
| Cheia Gemini apare în URL-ul dependinței | cineva a setat `OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION=true` | șterge variabila; implicit query string-ul e `Redacted` |
| Aplicația nu pornește după deploy și în *Log stream* apare `Connection String` | connection string trunchiat/greșit (SDK 3.x aruncă excepție dacă e invalid) | copiază-l din nou din *Overview*; sau șterge variabila ca aplicația să pornească fără AI |
| Local, în Visual Studio, vrei să vezi datele | e oprit intenționat local | `Properties/launchSettings.json` → `environmentVariables` → adaugă `APPLICATIONINSIGHTS_CONNECTION_STRING` cu o resursă **separată** de test; șterge-o după |

---

### Rezumat în 5 rânduri
1. Creezi resursa **Application Insights** în același resource group și regiune.
2. Copiezi **Connection String** din *Overview*.
3. App Service → *Environment variables* → `APPLICATIONINSIGHTS_CONNECTION_STRING` = … → Apply.
4. *Live metrics* → vezi *Servers: 1* → gata. Sampling adaptiv (5/s) e deja activ din cod.
5. *Daily cap* 0,5 GB + action group pe email + alertele #1, #2 (availability), #4.
