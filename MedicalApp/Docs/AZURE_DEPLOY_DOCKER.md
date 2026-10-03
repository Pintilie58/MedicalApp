# Portarea pe Azure din Docker — ghid pas cu pas (începători)

> Decizie (iunie 2026): aplicația C# rulează ca **container Linux în App Service**, serviciul
> Python LOINC ca **Azure Container App**, baza în **Azure SQL**, fișierele CAM în **Blob Storage**,
> imaginile în **Azure Container Registry**. Nivel de pornire **economic** (B1 + SQL Basic),
> regiunea **West Europe**. Domeniu: `mymedicalapp.net` (GoDaddy).
>
> Toate comenzile se dau în **PowerShell** (nu CMD), din folderul repo-ului:
> `cd C:\Projects\MedicalApp-repo`. Liniile care încep cu `#` sunt comentarii, nu se copiază.
> Fiecare pas se termină cu o **verificare** — nu trece mai departe până nu trece.

Ghiduri legate: `AZURE_APP_SETTINGS.md` (toate variabilele), `CAM_BLOB_STORAGE.md`, `SCALE_OUT.md`,
`APPLICATION_INSIGHTS.md`, `STRIPE_PAYMENTS.md`.

---

## Pasul 0 — Variabile comune + Resource Group

Numele marcate **(unic global)** trebuie să fie unice în tot Azure: adaugă cifre dacă sunt luate.
Rulează blocul acesta **la începutul fiecărei sesiuni PowerShell** (variabilele se pierd la închidere).

```powershell
az login                                   # se deschide browserul; alege contul
az account set --subscription "f2e789d3-0227-4815-81c9-6dfbfca6a736"
az account show --query "{nume:name, id:id}" -o table

$RG      = "rg-mymedicalapp"
$LOC     = "westeurope"
$SQLSRV  = "sql-mymedicalapp"              # (unic global) doar litere mici, cifre, cratimă
$SQLDB   = "MedicalAppDB"
$STG     = "stmymedicalapp"                # (unic global) DOAR litere mici și cifre, 3-24 caractere
$ACR     = "acrmymedicalapp"               # (unic global) DOAR litere și cifre
$PLAN    = "plan-mymedicalapp"
$APP     = "mymedicalapp"                  # (unic global) devine https://mymedicalapp.azurewebsites.net
$CAE     = "cae-mymedicalapp"              # mediul Container Apps (Python)
$LOINC   = "loinc-matcher"
$AI      = "appi-mymedicalapp"

az group create -n $RG -l $LOC -o table
```
**Verificare:** `az group list -o table` → apare `rg-mymedicalapp` cu `Succeeded`.

> Dacă o comandă spune `MissingSubscriptionRegistration` / `The subscription is not registered to use namespace 'Microsoft.X'`:
> `az provider register -n Microsoft.X --wait` (ex. `Microsoft.Sql`, `Microsoft.Web`, `Microsoft.App`, `Microsoft.ContainerRegistry`, `Microsoft.Storage`, `Microsoft.OperationalInsights`, `Microsoft.Insights`) și repetă comanda.

---

## Pasul 1 — Azure SQL (baza de date)

```powershell
$SQLADMIN = "mmaadmin"
$SQLPASS  = 'Pune-O-Parola-Lunga-Fara-Ghilimele-123'   # ghilimele SIMPLE; fără ; ' " $ ` în parolă

az sql server create -n $SQLSRV -g $RG -l $LOC -u $SQLADMIN -p $SQLPASS -o table

# Firewall 1: serviciile Azure (App Service) au voie
az sql server firewall-rule create -g $RG -s $SQLSRV -n AllowAzureServices --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0 -o table
# Firewall 2: calculatorul tău (pentru Update-Database și SSMS)
$MYIP = (Invoke-RestMethod https://api.ipify.org)
az sql server firewall-rule create -g $RG -s $SQLSRV -n MyPC --start-ip-address $MYIP --end-ip-address $MYIP -o table

# Baza: Basic = 5 DTU, max 2 GB, ~5 €/lună. Upgrade oricând (vezi mai jos).
az sql db create -g $RG -s $SQLSRV -n $SQLDB --edition Basic --capacity 5 --backup-storage-redundancy Local -o table
```

Connection string-ul (îl folosești la Update-Database și în App Service):
```powershell
$SQLCONN = "Server=tcp:$SQLSRV.database.windows.net,1433;Database=$SQLDB;User ID=$SQLADMIN;Password=$SQLPASS;Encrypt=True;TrustServerCertificate=False;Connect Timeout=30;Max Pool Size=50;Min Pool Size=2"
$SQLCONN            # îl afișează — copiază-l într-un loc sigur (ex. KeePass), NU în repo
```

**Crearea tabelelor (migrări EF Core)** — în Visual Studio, *Tools → NuGet Package Manager → Package Manager Console*:
```powershell
Update-Database -Connection "Server=tcp:sql-mymedicalapp.database.windows.net,1433;Database=MedicalAppDB;User ID=mmaadmin;Password=PAROLA;Encrypt=True;TrustServerCertificate=False;Connect Timeout=30"
```
(sau din PowerShell în folderul `MedicalApp`: `dotnet ef database update --connection "$SQLCONN"`).

**Verificare:** în VS → *View → SQL Server Object Explorer → Add SQL Server* → `sql-mymedicalapp.database.windows.net`, SQL auth, user/parola → baza `MedicalAppDB` → *Tables* conține `Users`, `PromotionSettings`, `__EFMigrationsHistory`.
Sau: `az sql db show -g $RG -s $SQLSRV -n $SQLDB --query status -o tsv` → `Online`.

> Upgrade când baza se apropie de 2 GB sau e lentă: `az sql db update -g $RG -s $SQLSRV -n $SQLDB --edition Standard --service-objective S0` (10 DTU, 250 GB, ~14 €). Fără downtime.
> IP-ul de acasă se schimbă (ISP dinamic): dacă VS nu se mai conectează, repetă comanda „Firewall 2" cu IP-ul nou.

---

## Pasul 2 — Storage account (fișiere CAM + chei Data Protection)

```powershell
az storage account create -n $STG -g $RG -l $LOC --sku Standard_LRS --kind StorageV2 --min-tls-version TLS1_2 --allow-blob-public-access false -o table
az storage container create -n cam            --account-name $STG --auth-mode login -o table
az storage container create -n dataprotection --account-name $STG --auth-mode login -o table
```
Dacă `--auth-mode login` dă `AuthorizationPermissionMismatch`, dă-ți ție rolul (durează ~2 min să se propage):
```powershell
$ME = az ad signed-in-user show --query id -o tsv
$STGID = az storage account show -n $STG -g $RG --query id -o tsv
az role assignment create --assignee $ME --role "Storage Blob Data Contributor" --scope $STGID -o table
```
**Verificare:** `az storage container list --account-name $STG --auth-mode login -o table` → `cam`, `dataprotection`.

---

## Pasul 3 — Container Registry + construirea imaginilor ÎN Azure

`az acr build` urcă doar codul sursă (câțiva MB) și construiește imaginea pe serverele Azure — nu urci GB de acasă.

```powershell
az acr create -n $ACR -g $RG -l $LOC --sku Basic -o table

# Aplicația C# (~3-5 min)
az acr build -r $ACR -t mymedicalapp/app:v1 -t mymedicalapp/app:latest ./MedicalApp
# Serviciul Python (~10-15 min: PyTorch CPU + modelul)
az acr build -r $ACR -t mymedicalapp/loinc:v1 -t mymedicalapp/loinc:latest ./loinc_service
```
**Verificare:** `az acr repository list -n $ACR -o table` → `mymedicalapp/app`, `mymedicalapp/loinc`.

> La fiecare update de cod: `git pull` → `az acr build -r $ACR -t mymedicalapp/app:v2 -t mymedicalapp/app:latest ./MedicalApp`
> → apoi pasul 5.6 (App Service trage `:latest` la restart). Păstrează etichete `v1, v2, …` ca să poți reveni.

---

## Pasul 4 — Serviciul Python (LOINC) → Azure Container Apps

```powershell
az extension add -n containerapp --upgrade
az containerapp env create -n $CAE -g $RG -l $LOC -o table

$ACRSRV = "$ACR.azurecr.io"
az containerapp create -n $LOINC -g $RG --environment $CAE `
  --image "$ACRSRV/mymedicalapp/loinc:latest" `
  --registry-server $ACRSRV --registry-identity system `
  --target-port 8000 --ingress external `
  --cpu 1.0 --memory 2.0Gi --min-replicas 1 --max-replicas 1 -o table

$LOINCURL = "https://" + (az containerapp show -n $LOINC -g $RG --query properties.configuration.ingress.fqdn -o tsv)
$LOINCURL
```
**Verificare (așteaptă ~2 min, modelul se încarcă):** `Invoke-RestMethod "$LOINCURL/health"` → răspuns JSON cu starea.

> `--min-replicas 1` = nu „adoarme" (altfel prima potrivire LOINC după pauză durează 60-90 s). Cost ≈ 15-25 €/lună.
> Serviciul e public (fără autentificare). După pasul 5, restrânge accesul la IP-urile App Service:
> `$OUTIPS = az webapp show -n $APP -g $RG --query possibleOutboundIpAddresses -o tsv` apoi, pentru fiecare IP din listă (separate prin virgulă):
> `az containerapp ingress access-restriction set -n $LOINC -g $RG --rule-name app1 --ip-address <IP>/32 --action Allow`.

---

## Pasul 5 — Aplicația C# → App Service for Containers

### 5.1 Plan + Web App
```powershell
az appservice plan create -n $PLAN -g $RG -l $LOC --is-linux --sku B1 -o table
az webapp create -n $APP -g $RG -p $PLAN --container-image-name "$ACRSRV/mymedicalapp/app:latest" -o table
```
Dacă `--container-image-name` nu e recunoscut (CLI mai vechi): folosește `--deployment-container-image-name`.

### 5.2 Identitate + drepturi (fără parole în configurație)
```powershell
$PRINCIPAL = az webapp identity assign -n $APP -g $RG --query principalId -o tsv
$ACRID = az acr show -n $ACR -g $RG --query id -o tsv
az role assignment create --assignee $PRINCIPAL --role AcrPull --scope $ACRID -o table
az role assignment create --assignee $PRINCIPAL --role "Storage Blob Data Contributor" --scope $STGID -o table
az webapp config set -n $APP -g $RG --generic-configurations '{\"acrUseManagedIdentityCreds\": true}' -o none
```

### 5.3 Application Insights
```powershell
az monitor app-insights component create --app $AI -g $RG -l $LOC --application-type web -o table
$AICONN = az monitor app-insights component show --app $AI -g $RG --query connectionString -o tsv
```
(Dacă cere extensia: `az extension add -n application-insights`.)

### 5.4 Variabilele aplicației (toate deodată)
Completează înainte: cheia Gemini, parola Brevo, cheile Stripe (**test** până la pasul 6).
```powershell
$GEMINI = "AIza..."
$BREVO  = "..."
$STRIPE_SK = "sk_test_..."
$STRIPE_PK = "pk_test_..."

az webapp config appsettings set -n $APP -g $RG -o none --settings `
  ASPNETCORE_ENVIRONMENT=Azure `
  WEBSITES_PORT=8080 `
  WEBSITE_TIME_ZONE="Europe/Bucharest" `
  "ConnectionStrings__DefaultConnection=$SQLCONN" `
  "Gemini__ApiKey=$GEMINI" `
  "EmailSettings__Password=$BREVO" `
  "Payments__Stripe__SecretKey=$STRIPE_SK" `
  "Payments__Stripe__PublishableKey=$STRIPE_PK" `
  "CamSettings__Blob__AccountUrl=https://$STG.blob.core.windows.net" `
  "LoincMatcher__BaseUrl=$LOINCURL" `
  "APPLICATIONINSIGHTS_CONNECTION_STRING=$AICONN"
```
> `Payments__Stripe__WebhookSecret` se adaugă la pasul 6, după ce există domeniul.
> Pe Linux fusul orar e `Europe/Bucharest` (nu `GTB Standard Time`, care e numele Windows).

### 5.5 Setările planului
```powershell
az webapp config set -n $APP -g $RG --always-on true --http20-enabled true --ftps-state Disabled -o none
az webapp config set -n $APP -g $RG --generic-configurations '{\"healthCheckPath\": \"/healthz\"}' -o none
az webapp update -n $APP -g $RG --https-only true --client-affinity-enabled false -o none
az webapp log config -n $APP -g $RG --docker-container-logging filesystem -o none
```

### 5.6 Pornire + verificare
```powershell
az webapp restart -n $APP -g $RG
az webapp log tail -n $APP -g $RG          # Ctrl+C pentru ieșire
```
În log cauți `Now listening on: http://[::]:8080`. Apoi:
1. `Invoke-RestMethod "https://$APP.azurewebsites.net/healthz"` → `ok`
2. Browser: `https://mymedicalapp.azurewebsites.net` → login cu contul admin.
3. **Admin → Diagnostic infrastructură** → SQL, Blob, LOINC, Gemini pe verde.
4. O interpretare B2C de test + un lot CAM de 2 fișiere.
5. Application Insights → *Live metrics* → `Servers: 1`.

> Erori tipice: `Login failed for user` → connection string greșit (verifică `$SQLPASS`); `403` la Blob → rolul din 5.2 încă nu s-a propagat (așteaptă 5 min, restart); container nu pornește → `az webapp log tail` + verifică `WEBSITES_PORT=8080`.

---

## Pasul 6 — Domeniu, HTTPS, Stripe LIVE, protecții

### 6.1 Domeniul `mymedicalapp.net` (GoDaddy)
```powershell
$VERIFYID = az webapp show -n $APP -g $RG --query customDomainVerificationId -o tsv
$APPIP    = az webapp show -n $APP -g $RG --query inboundIpAddress -o tsv
$VERIFYID; $APPIP
```
În **GoDaddy → My Products → Domains → mymedicalapp.net → DNS → Manage DNS**: șterge înregistrările vechi `A` (@) și `CNAME` (www) care arătau spre vechiul conținut, apoi adaugă:

| Type | Name | Value | TTL |
|---|---|---|---|
| `A` | `@` | `$APPIP` | 1 h |
| `TXT` | `asuid` | `$VERIFYID` | 1 h |
| `CNAME` | `www` | `mymedicalapp.azurewebsites.net` | 1 h |
| `TXT` | `asuid.www` | `$VERIFYID` | 1 h |

Așteaptă 10-30 min (verifică cu `nslookup mymedicalapp.net`), apoi:
```powershell
az webapp config hostname add -n $APP -g $RG --hostname mymedicalapp.net -o table
az webapp config hostname add -n $APP -g $RG --hostname www.mymedicalapp.net -o table
# Certificate gratuite, gestionate de Azure (se reînnoiesc singure)
az webapp config ssl create -n $APP -g $RG --hostname mymedicalapp.net -o table
az webapp config ssl create -n $APP -g $RG --hostname www.mymedicalapp.net -o table
```
Dacă `ssl create` nu există în versiunea ta de CLI: Portal → App Service → *Custom domains* → lângă fiecare domeniu **Add binding** → *Create App Service Managed Certificate* → SNI SSL.

**Verificare:** `https://mymedicalapp.net/healthz` → `ok`, lacăt verde în browser.

### 6.2 Stripe LIVE + webhook (abia acum, cu domeniul funcțional)
1. Stripe Dashboard → treci pe **Live mode** → *Developers → API keys* → copiază `sk_live_…` și `pk_live_…`.
2. *Developers → Webhooks → Add endpoint*: URL `https://mymedicalapp.net/Credits/StripeWebhook`, evenimente `checkout.session.completed`, `checkout.session.async_payment_succeeded`, `checkout.session.async_payment_failed`, `checkout.session.expired` → copiază **Signing secret** `whsec_…`.
```powershell
az webapp config appsettings set -n $APP -g $RG -o none --settings `
  "Payments__Stripe__SecretKey=sk_live_..." "Payments__Stripe__PublishableKey=pk_live_..." "Payments__Stripe__WebhookSecret=whsec_..."
```
3. Plată reală de test cu pachetul cel mai mic → creditele apar; în Stripe → Webhooks → evenimentul are `200`.

### 6.3 Protecții
- Application Insights → **Daily cap 0,5 GB** + action group email + alertele #1, #2, #4 (`APPLICATION_INSIGHTS.md` §8-9).
- Azure SQL → *Backups*: PITR implicit 7 zile (poți pune 35). Portal → baza → *Data management → Backups → Retention policies*.
- **Cost Management → Budgets**: buget lunar (ex. 80 €) cu alertă la 80 % — un mail înainte de surprize.
- Restrânge serviciul Python la IP-urile App Service (nota de la pasul 4).

---

## Pasul 7 — Când crești (după câteva zile stabile)
1. `az webapp config appsettings set -n $APP -g $RG --settings ScaleOut__Enabled=true Gemini__RateLimit__InstanceCount=2 -o none`
2. `az appservice plan update -n $PLAN -g $RG --sku P1v3` (B1 nu suportă scale-out automat și e lent la PDF-uri mari)
3. `az appservice plan update -n $PLAN -g $RG --number-of-workers 2`
Detalii și capcane: `SCALE_OUT.md`, `AZURE_SCALING.md`.

---

## Update de cod (rutina după fiecare „Save to GitHub")
```powershell
cd C:\Projects\MedicalApp-repo; git pull
az acr build -r $ACR -t mymedicalapp/app:v2 -t mymedicalapp/app:latest ./MedicalApp   # v2, v3, ...
az webapp restart -n $APP -g $RG                                                       # trage :latest
az webapp log tail -n $APP -g $RG
```
Migrări EF noi: `Update-Database -Connection "..."` din VS **înainte** de restart (pe Azure `AutoMigrate` e oprit intenționat).
Rollback: `az webapp config container set -n $APP -g $RG --container-image-name "$ACRSRV/mymedicalapp/app:v1"` + restart.

## Costuri estimate (economic, West Europe)
| Resursă | SKU | €/lună |
|---|---|---|
| App Service | B1 Linux | ≈ 12 |
| Azure SQL | Basic 5 DTU | ≈ 5 |
| Container Apps (Python, 1 vCPU/2 GB mereu pornit) | consumption | ≈ 15-25 |
| Container Registry | Basic | ≈ 5 |
| Storage | LRS, câțiva GB | ≈ 1 |
| Application Insights | < 5 GB | 0 |
| **Total** | | **≈ 40-50 €** |
