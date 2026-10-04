# Portarea pe Azure din Docker — ghid pas cu pas (începători)

> Decizie (iunie 2026): aplicația C# rulează ca **container Linux în App Service**, serviciul
> Python LOINC ca **Azure Container App**, baza în **Azure SQL**, fișierele CAM în **Blob Storage**,
> imaginile în **Azure Container Registry**. Nivel de pornire **economic** (B1 + SQL Basic),
> regiunea **Germany West Central** (West Europe a refuzat abonamentul nou: „region not accepting new customers"). Domeniu: `mymedicalapp.net` (GoDaddy).
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
$LOC     = "germanywestcentral"      # West Europe refuza abonamentele noi (iunie 2026)
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

# O SINGURĂ DATĂ pe abonament nou: activează serviciile (altfel: MissingSubscriptionRegistration)
az provider register -n Microsoft.Sql --wait
az provider register -n Microsoft.Storage --wait
az provider register -n Microsoft.ContainerRegistry --wait
az provider register -n Microsoft.Web --wait
az provider register -n Microsoft.App --wait
az provider register -n Microsoft.OperationalInsights --wait
az provider register -n Microsoft.Insights --wait
```
**Verificare:** `az group list -o table` → apare `rg-mymedicalapp` cu `Succeeded`; `az provider show -n Microsoft.Sql --query registrationState -o tsv` → `Registered`.

> `RequestDisallowedByAzure: The selected region is currently not accepting new customers` = regiunea e plină pentru abonamente noi.
> Schimbă `$LOC` (în ordine: `germanywestcentral`, `northeurope`, `swedencentral`, `francecentral`) și repetă comanda. Resource group-ul poate rămâne.

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

## Pasul 3 — Container Registry + urcarea imaginilor

> `az acr build` (construire în Azure) este **blocat pe abonamentele noi/free trial** (`TasksOperationsNotAllowed`).
> Construim local cu Docker Desktop (deja testat) și facem `push`. Închide Visual Studio înainte de build (blochează `.vs\`).

```powershell
az acr create -n $ACR -g $RG -l $LOC --sku Basic -o table
$ACRSRV = "$ACR.azurecr.io"

docker compose build app                       # imaginea C# (include appsettings.Azure.json)
az acr login -n $ACR                           # Login Succeeded (valabil 3 h)

docker tag mymedicalapp-app "$ACRSRV/mymedicalapp/app:v1"
docker tag mymedicalapp-app "$ACRSRV/mymedicalapp/app:latest"
docker push "$ACRSRV/mymedicalapp/app:v1"
docker push "$ACRSRV/mymedicalapp/app:latest"

# Python (imaginea locală există deja; ~2-3 GB, 10-30 min la primul push)
docker tag mymedicalapp-loinc "$ACRSRV/mymedicalapp/loinc:v1"
docker tag mymedicalapp-loinc "$ACRSRV/mymedicalapp/loinc:latest"
docker push "$ACRSRV/mymedicalapp/loinc:v1"
docker push "$ACRSRV/mymedicalapp/loinc:latest"
```
Dacă `docker tag` spune `No such image`: `docker images` și folosește numele din coloana REPOSITORY.

**Verificare:** `az acr repository list -n $ACR -o table` → `mymedicalapp/app`, `mymedicalapp/loinc`.

> La fiecare update de cod: `git pull` → `docker compose build app` → `docker tag` cu `:v2` și `:latest` → `docker push` ambele → pasul 5.6.
> Păstrează etichete `v1, v2, …` ca să poți reveni. (Dacă ulterior deblochezi ACR Tasks printr-un tichet la suport,
> `az acr build -r $ACR -t mymedicalapp/app:vN -t mymedicalapp/app:latest ./MedicalApp` înlocuiește build+tag+push.)

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

## Pasul 5 — Aplicația C# → Azure Container Apps (varianta aplicată)

> App Service B1 a fost refuzat de cota free-trial (`Current Limit (B1 VMs): 0`, portalul nu permite cereri de cotă în primele
> ore după upgrade). Aplicația C# rulează deci în **același mediu Container Apps** ca Python-ul. Secțiunea App Service din
> `AZURE_APP_SETTINGS.md` rămâne valabilă pentru NUMELE variabilelor; comenzile sunt cele de aici.

```powershell
# 5.1 Application Insights
az extension add -n application-insights
az monitor app-insights component create --app $AI -g $RG -l $LOC --application-type web -o table
$AICONN = az monitor app-insights component show --app $AI -g $RG --query connectionString -o tsv

# 5.2 cheile din .env (Docker) -> variabile PowerShell
Get-Content .env | Where-Object { $_ -match '^\s*([^#=]+)=(.*)$' } | ForEach-Object { Set-Variable -Name $matches[1].Trim() -Value $matches[2].Trim() }

# 5.3 aplicația (secretele intră în seiful Container Apps)
az containerapp create -n $APP -g $RG --environment $CAE `
  --image "$ACRSRV/mymedicalapp/app:latest" `
  --registry-server $ACRSRV --registry-identity system `
  --target-port 8080 --ingress external `
  --cpu 1.0 --memory 2.0Gi --min-replicas 1 --max-replicas 1 `
  --secrets "sqlconn=$SQLCONN" "gemini=$GEMINI_API_KEY" "brevo=$BREVO_SMTP_PASSWORD" "stripesk=$STRIPE_SECRET_KEY" "aiconn=$AICONN" `
  --env-vars ASPNETCORE_ENVIRONMENT=Azure TZ=Europe/Bucharest ASPNETCORE_FORWARDEDHEADERS_ENABLED=true `
    ConnectionStrings__DefaultConnection=secretref:sqlconn Gemini__ApiKey=secretref:gemini `
    EmailSettings__Password=secretref:brevo Payments__Stripe__SecretKey=secretref:stripesk `
    "Payments__Stripe__PublishableKey=$STRIPE_PUBLISHABLE_KEY" `
    "CamSettings__Blob__AccountUrl=https://$STG.blob.core.windows.net" `
    "LoincMatcher__BaseUrl=$LOINCURL" APPLICATIONINSIGHTS_CONNECTION_STRING=secretref:aiconn -o table

# 5.4 Blob + adresa
$PRINCIPAL = az containerapp identity show -n $APP -g $RG --query principalId -o tsv
az role assignment create --assignee $PRINCIPAL --role "Storage Blob Data Contributor" --scope $STGID -o table
$APPURL = "https://" + (az containerapp show -n $APP -g $RG --query properties.configuration.ingress.fqdn -o tsv)
Invoke-RestMethod "$APPURL/healthz"      # ok

# 5.8 OBLIGATORIU pe Container Apps (disc temporar): sesiuni în SQL + chei Data Protection în Blob
az containerapp update -n $APP -g $RG --set-env-vars ScaleOut__Enabled=true Gemini__RateLimit__InstanceCount=1 -o none
```
Verificări: login → `az containerapp revision restart` → rămâi logat; tabelele `AppSessionCache`, `AppSingletonLease`; `dataprotection/keys.xml`.
Loguri: `az containerapp logs show -n $APP -g $RG --tail 60`. Secret nou/modificat: `az containerapp secret set -n $APP -g $RG --secrets "nume=valoare"` apoi restart revizie.

---

## Pasul 6 — Domeniu, HTTPS, Stripe LIVE, protecții

### 6.1 Domeniul `mymedicalapp.net` (GoDaddy) pe Container Apps
```powershell
$VERIFYID = az containerapp show -n $APP -g $RG --query properties.customDomainVerificationId -o tsv
$ENVIP    = az containerapp env show -n $CAE -g $RG --query properties.staticIp -o tsv
$FQDN     = az containerapp show -n $APP -g $RG --query properties.configuration.ingress.fqdn -o tsv
```
GoDaddy → DNS → șterge `A @` / `CNAME www` vechi, adaugă: `A @ = $ENVIP`, `TXT asuid = $VERIFYID`, `CNAME www = $FQDN`, `TXT asuid.www = $VERIFYID`.
După propagare (`nslookup mymedicalapp.net`):
```powershell
az containerapp hostname add  -n $APP -g $RG --hostname mymedicalapp.net -o table
az containerapp hostname bind -n $APP -g $RG --hostname mymedicalapp.net --environment $CAE --validation-method HTTP -o table
az containerapp hostname add  -n $APP -g $RG --hostname www.mymedicalapp.net -o table
az containerapp hostname bind -n $APP -g $RG --hostname www.mymedicalapp.net --environment $CAE --validation-method CNAME -o table
```
Certificat gestionat gratuit (5-15 min). **Verificare:** `https://mymedicalapp.net/healthz` → `ok`.

### 6.2 Stripe LIVE + webhook (abia acum, cu domeniul funcțional)
1. Stripe Dashboard → treci pe **Live mode** → *Developers → API keys* → copiază `sk_live_…` și `pk_live_…`.
2. *Developers → Webhooks → Add endpoint*: URL `https://mymedicalapp.net/Credits/StripeWebhook`, evenimente `checkout.session.completed`, `checkout.session.async_payment_succeeded`, `checkout.session.async_payment_failed`, `checkout.session.expired` → copiază **Signing secret** `whsec_…`.
```powershell
az containerapp secret set -n $APP -g $RG --secrets "stripesk=sk_live_..." "stripewh=whsec_..." -o none
az containerapp update -n $APP -g $RG --set-env-vars "Payments__Stripe__PublishableKey=pk_live_..." Payments__Stripe__WebhookSecret=secretref:stripewh -o none
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
docker compose build app
az acr login -n $ACR
docker tag mymedicalapp-app "$ACRSRV/mymedicalapp/app:v2"; docker tag mymedicalapp-app "$ACRSRV/mymedicalapp/app:latest"   # v2, v3, ...
docker push "$ACRSRV/mymedicalapp/app:v2"; docker push "$ACRSRV/mymedicalapp/app:latest"
az containerapp update -n $APP -g $RG --image "$ACRSRV/mymedicalapp/app:v2" -o none      # eticheta fixă => revizie nouă, rollback ușor
az containerapp logs show -n $APP -g $RG --tail 40
```
Migrări EF noi: `dotnet ef database update --connection "$SQLCONN"` **înainte** de update (pe Azure `AutoMigrate` e oprit intenționat).
Rollback: `az containerapp update -n $APP -g $RG --image "$ACRSRV/mymedicalapp/app:v1"`.

## Costuri estimate (economic, West Europe)
| Resursă | SKU | €/lună |
|---|---|---|
| Container Apps (C#, 1 vCPU/2 GB mereu pornit, majoritar idle) | consumption | ≈ 15-25 |
| Azure SQL | Basic 5 DTU | ≈ 5 |
| Container Apps (Python, 1 vCPU/2 GB mereu pornit) | consumption | ≈ 15-25 |
| Container Registry | Basic | ≈ 5 |
| Storage | LRS, câțiva GB | ≈ 1 |
| Application Insights | < 5 GB | 0 |
| **Total** | | **≈ 40-50 €** |
