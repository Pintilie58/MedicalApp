# Deploy pe Azure — configurația, pas cu pas

> Scris în iunie 2026, împreună cu `appsettings.Azure.json`. Documentul răspunde la o
> singură întrebare: **ce pun unde**, ca aplicația să pornească corect pe Azure App Service
> fără să atingem `appsettings.json` (cel local rămâne neschimbat).

## 1. Cum se citește configurația (de ce NU înlocuim nimic)

ASP.NET Core citește, în ordine, și fiecare strat **suprascrie** doar cheile pe care le conține:

```
1. appsettings.json                  ← baza, valorile de pe local (rămâne exact așa cum e)
2. appsettings.{ASPNETCORE_ENVIRONMENT}.json
      - local, din VS:  appsettings.Development.json
      - pe Azure:       appsettings.Azure.json      (dacă ASPNETCORE_ENVIRONMENT = Azure)
3. variabilele de mediu / Application settings din portal   ← câștigă întotdeauna
```

Consecințe practice:
- `appsettings.Azure.json` **nu se citește niciodată pe local** (acolo environment-ul e
  `Development`), deci poate sta liniștit în proiect, comis în Git.
- **Nu ștergi și nu înlocuiești `appsettings.json`.** Dacă l-ai înlocui, ai pierde tot ce nu
  e repetat în fișierul de Azure (modele Gemini, prețuri, prompturi, admini etc.).
- Secretele **nu stau în niciun fișier**: le pui în portal, unde bat orice valoare din JSON.
  În JSON rămân goale, ca să se vadă ce trebuie completat.
- Numele unei chei imbricate se scrie în portal cu **dublu underscore**:
  `Gemini:RateLimit:InstanceCount` → `Gemini__RateLimit__InstanceCount`.

## 2. Application settings de pus în portal

App Service → *Settings* → *Environment variables* → *App settings*.

### Obligatorii
| Name | Value | De ce |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Azure` | activează `appsettings.Azure.json`. Fără el, App Service rulează ca `Production` și fișierul e ignorat |
| `ConnectionStrings__DefaultConnection` | `Server=tcp:<srv>.database.windows.net,1433;Database=MedicalAppDB;User ID=<user>;Password=<parolă>;Encrypt=True;TrustServerCertificate=False;Connect Timeout=30;Max Pool Size=50;Min Pool Size=2` | Azure SQL. Fără `MultipleActiveResultSets` (încetinește, nu e necesar). `Max Pool Size=50` pentru că pool-ul e per instanță |
| `Gemini__ApiKey` | cheia Google | secret |
| `EmailSettings__Password` | parola SMTP Brevo | secret |
| `Payments__Stripe__SecretKey` | `sk_live_…` după revendicarea contului (`sk_test_…` până atunci) | secret |
| `Payments__Stripe__WebhookSecret` | `whsec_…` din Dashboard → Developers → Webhooks (`/Credits/StripeWebhook`) | secret |
| `CamSettings__Blob__AccountUrl` | `https://<cont>.blob.core.windows.net` | fișierele CAM; `App Service nu are disc C:\` |
| `WEBSITE_TIME_ZONE` | `GTB Standard Time` | serverul e UTC; altfel sumarul zilnic pleacă la 09:00 UTC |

### Recomandate / după caz
| Name | Value | Când |
|---|---|---|
| `LoincMatcher__BaseUrl` | `https://<serviciul-python>` | când hostezi serviciul Python (Container App). Până atunci: `LoincMatcher__Enabled = false` |
| `ScaleOut__Enabled` | `true` | **obligatoriu înainte** de a urca la 2+ instanțe |
| `Gemini__RateLimit__InstanceCount` | numărul real de instanțe (la autoscale: maximul) | la 2+ instanțe |
| `Gemini__RateLimit__RequestsPerMinute` | cota reală a contului Google | înainte de scale-out |
| `CamSettings__MaxParallelFiles` | `4` (în `appsettings.Azure.json`); `1` = secvențial clasic | dacă un lot CAM dă erori 429 sau vrei să revii la comportamentul vechi, fără rebuild |
| `Gemini__RateLimit__MaxConcurrentCalls` | `20` (Azure json, calibrat Tier 1); trebuie ≥ `InterpretationQueue__MaxConcurrent` + `CamSettings__MaxParallelFiles` | când urci paralelismul |
| `InterpretationQueue__MaxConcurrent` | `8`-`10` | după ce măsori în *Admin → Performance* |

### În portal, nu în JSON
- **Always On** = On (altfel App Service adoarme procesul și worker-ele de fundal se opresc).
- **ARR affinity / sticky sessions** = Off.
- **Health check path** = `/healthz` (endpoint text `ok`, nu atinge SQL/Gemini/Blob).
- **Managed identity** = On, plus rolul **Storage Blob Data Contributor** pe contul de storage
  (așa merge Blob-ul fără niciun secret în configurație).
- **HTTPS Only** = On.

## 3. Ordinea la primul deploy

1. Creează Azure SQL + contul de Storage (containerele `cam` și `dataprotection`).
2. Publică aplicația (o singură instanță, `ScaleOut__Enabled` lipsă/false).
3. **`Update-Database` pe Azure SQL** din VS (Package Manager Console, cu connection string-ul
   de Azure) sau `dotnet ef database update`. Aplicația **nu** rulează migrări automat.
4. Pune App settings-urile obligatorii → Restart.
5. Verifică: `/healthz` → `ok`, apoi login și **Admin → Diagnostic infrastructură**.
6. O interpretare de test + un lot CAM mic de 2 fișiere.
7. Abia după ce merge stabil: `ScaleOut__Enabled = true`, apoi *Scale out* la 2-3 instanțe și
   `Gemini__RateLimit__InstanceCount` egal cu numărul de instanțe.

## 4. Capcane verificate în cod

- **Deploy/swap în timpul unui lot CAM**: claim-ul expiră în 3 minute, lotul devine `Failed` și
  operatorul îl relansează. Intenționat — reluarea automată ar retrimite emailuri pacienților.
- **`CamSettings:FilesRoot`** (`C:\MedicalApp_files`) nu are sens pe App Service; de aceea
  `Storage = Blob` e obligatoriu, altfel modulul CAM pică la prima citire de folder.
- **`LoincAutoStart`** pornește uvicorn doar pe Windows și e `false` pe Azure; serviciul Python
  are hostingul lui.
- **Bara de progres** a interpretării e în memoria instanței: cu mai multe instanțe poate îngheța
  cosmetic, dar pastila din dreapta sus (citită din SQL) rămâne corectă.
- **Prima gâtuire reală** nu e Gemini, ci serviciul LOINC cu un singur worker uvicorn
  (vezi `AZURE_SCALING.md`).

## 5. Rollback

`ASPNETCORE_ENVIRONMENT` → `Production` (sau șterge-l) și fișierul de Azure nu se mai citește;
`ScaleOut__Enabled = false` + o singură instanță readuce comportamentul de single-instance.
Tabelele `AppSessionCache` / `AppSingletonLease` rămân în bază, nefolosite.

## 6. Testare (iunie 2026)

`/app/memory/probes/AzureAppSettingsProbe.cs.txt` (proiect `/app/probe_cfg`) construiește
configurația **exact** cum o construiește aplicația (`WebApplication.CreateBuilder`) și verifică
afirmațiile din acest document — **17/17 PASS**:
- cu `ASPNETCORE_ENVIRONMENT = Azure` fișierul de Azure este citit (`Storage = Blob`,
  `LoincAutoStart = false`, `AttachDebugJson = false`, timeout LOINC 10 s);
- **este suprapunere, nu înlocuire**: modelele Gemini, prețurile, lista de admini,
  `FilesRoot`, serverul SMTP rămân din `appsettings.json`;
- secretele sunt goale în fișiere (`Gemini:ApiKey`, `EmailSettings:Password`,
  connection string-ul);
- `Gemini__RateLimit__InstanceCount = 3` din portal **bate** valoarea din fișier, iar cota se
  împarte la 20 RPM / 2 apeluri simultane pe instanță;
- pe `Development` fișierul de Azure este **ignorat** complet (rămân `LocalDisk`, uvicorn
  auto-start și connection string-ul de SQL Express);
- secțiunea `Logging` din suprascriere se leagă fără excepție.

Verificat și că `appsettings.Azure.json` ajunge în pachetul de publicare
(`dotnet publish` → fișierul e prezent lângă `appsettings.json`).
