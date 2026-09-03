# CAM pe Azure Blob Storage — ghid de activare

> Codul e scris și testat (iunie 2026), dar **NU e activ**. Pe calculatorul tău
> și în Docker cu volum montat rulează exact ca înainte, pe disc.
> Comutarea se face din **o singură setare**.

## 1. Ce s-a schimbat în cod

`ICamFileStore` a fost rescris din „dă-mi calea folderului” în **operațiuni**:
`ListAsync`, `ReadAsync`, `WriteAsync`, `MoveAsync`, `DeleteAsync`, `ExistsAsync`,
`EnsureClinicFoldersAsync`, `GetDisplayLocation` (doar pentru afișare în UI).

Două implementări, aceeași interfață:

| Implementare | Unde | Ce face |
|---|---|---|
| `LocalDiskCamFileStore` | dezvoltare, Docker cu volum | `{FilesRoot}/{clinica}/{Original,Sends,Sumar,Errors}` |
| `BlobCamFileStore` | Azure | container `cam`, blob `{clinica}/{Original|Sends|Sumar|Errors}/{fișier}` |

Consumatori adaptați (niciunul nu mai atinge discul):
`CheckPdfsController`, `BatchController`, `CAM/DashboardController`,
`CamBatchService`, `CamRetentionService`, `CamBatchSumarWriter`, `CreditsController`.

`CamBatchSumarWriter.Write(...)` → `CamBatchSumarWriter.Build(...)` care întoarce
`(numeFișier, text)`; scrierea o face apelantul prin store.

## 2. Cum activez la hostare (pas cu pas)

1. **Creează un cont de storage** în Azure (Standard, LRS, „Hot”). Numele trebuie
   să fie unic global, ex. `mymedicalappfiles`.
2. **Nu crea containerul manual** — aplicația îl creează singură la prima folosire
   (`Container: "cam"`).
3. **Dă identitate aplicației**: în App Service → *Identity* → *System assigned* → **On**.
4. **Dă-i drepturi**: în contul de storage → *Access control (IAM)* → *Add role
   assignment* → rolul **Storage Blob Data Contributor** → membru = identitatea
   App Service-ului. (Rolul de „Owner” pe abonament NU e suficient pentru date.)
5. **Setează configurația** (App Service → Configuration → Application settings):
   ```
   CamSettings__Storage            = Blob
   CamSettings__Blob__AccountUrl   = https://mymedicalappfiles.blob.core.windows.net
   CamSettings__Blob__Container    = cam
   ```
   Lasă `CamSettings__Blob__ConnectionString` **gol** — așa se folosește identitatea
   administrată și nu ai nicio parolă în configurație.
6. **Repornește** aplicația. Gata — CAM lucrează în cloud.
7. **Mută fișierele existente** (o singură dată), de pe calculatorul tău:
   ```
   azcopy login
   azcopy copy "C:\MedicalApp_files\*" ^
     "https://mymedicalappfiles.blob.core.windows.net/cam" --recursive
   ```
   Structura de foldere devine automat prefixele de blob corecte.
8. **Retenția automată** (opțional, recomandat): în contul de storage →
   *Lifecycle management* → regulă „șterge blob-urile din `cam/*/Sends/` mai vechi
   de 90 de zile”. Înlocuiește curățenia făcută de aplicație și nu costă nimic.

### Revenire (rollback)
Pune `CamSettings__Storage = LocalDisk` și repornește. Nimic nu se pierde: fișierele
rămân în Blob, iar în Azure nu se șterge nimic la comutare.

## 3. Testare locală cu Docker + Azurite

`Azurite` este emulatorul oficial de Azure Storage. Cu el poți testa **exact**
codul de producție, fără cont Azure și fără costuri.

```yaml
# docker-compose.yml (fragment)
services:
  azurite:
    image: mcr.microsoft.com/azure-storage/azurite
    command: "azurite-blob --blobHost 0.0.0.0 --skipApiVersionCheck"
    ports: ["10000:10000"]
```

Configurația aplicației pentru acest scenariu:
```
CamSettings__Storage                   = Blob
CamSettings__Blob__ConnectionString    = UseDevelopmentStorage=true
```

⚠️ `--skipApiVersionCheck` este **necesar**: SDK-ul Azure e mai nou decât Azurite
și altfel primești `InvalidHeaderValue / API version not supported`. Am dat exact
peste eroarea asta la testare.

## 4. Ce am testat (iunie 2026)

Probă: `/app/memory/probes/CamFileStoreContractProbe.cs.txt` — **același** set de
25 verificări rulat pe ambele implementări (disc local și Azurite), 50 în total,
toate PASS:

- pregătirea spațiului clinicii, listare cu filtru pe extensie, dimensiuni corecte;
- scrierea nu suprascrie niciodată un fișier existent (redenumire cu marcaj de timp);
- citire identică la octet cu ce s-a scris; `null` pentru fișier inexistent;
- mutarea Original → Sends păstrează conținutul și nu suprascrie destinația;
- fluxul Errors + `.reasons.txt`;
- ștergerea raportează corect existent/inexistent;
- **izolarea între clinici** (o clinică nu vede fișierele alteia);
- neutralizarea numelor cu `../../` (path traversal).

## 5. Ce NU e încă rezolvat (conștient)

- **Concurență la scale-out**: cu mai multe instanțe, două loturi ar putea lua
  același PDF. Azi limita e implicit 1 lot per clinică. La scalare reală se adaugă
  *lease* pe blob (rezervare de 60s) — Blob suportă nativ, e ~20 linii.
- **Pornirea automată la încărcare** (Event Grid → coadă) — opțional, ulterior.
- Fișierele urcă prin aplicație; pentru volume mari se poate adăuga upload direct
  în Blob cu link SAS temporar.
