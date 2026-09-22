# MyMedicalApp în Docker — ghid pas cu pas

Scop: să rulăm întreaga aplicație (SQL Server + aplicația C# + serviciul Python
LoincMatcher) în containere, ca pregătire pentru Azure App Service for Containers.

## Ce s-a adăugat în repo

| Fișier | Rol |
|---|---|
| `MedicalApp/Dockerfile` | Imaginea aplicației C# (.NET 9, Linux). Instalează `libfontconfig1` + fonturi, de care QuestPDF are nevoie pe Linux. |
| `MedicalApp/.dockerignore` | Exclude `bin/`, `obj/` din build (altfel build-ul e lent și poate eșua). |
| `MedicalApp/appsettings.Docker.json` | Diferențele de configurare pentru container. Se încarcă doar când `ASPNETCORE_ENVIRONMENT=Docker`. |
| `loinc_service/Dockerfile` | Imaginea Python. Instalează PyTorch **CPU-only** (altfel imaginea ar avea +2.5 GB de CUDA) și include modelul `all-MiniLM-L6-v2` în imagine. |
| `docker-compose.yml` | Leagă cele 3 servicii: `sql`, `loinc`, `app`. |
| `.env.example` | Șablon pentru secrete. Se copiază ca `.env` (ignorat de git). |

## Modificări în cod (2, ambele cu comutator în config)

1. `Program.cs` — `Database:AutoMigrate` (default `false`): la pornire aplicația
   creează baza și aplică migrările EF Core. Activat **doar** în
   `appsettings.Docker.json`, unde SQL pornește gol. Local și pe Azure nimic nu
   se schimbă.
2. `Program.cs` — `Hosting:UseHttpsRedirection` (default `true`): în container
   aplicația ascultă doar HTTP pe 8080, deci redirectarea la HTTPS e dezactivată
   prin `appsettings.Docker.json`.

## Porturi

| Serviciu | În container | Pe Windows |
|---|---|---|
| app (C#) | 8080 | http://localhost:8080 |
| sql | 1433 | `localhost,14330` (ca să nu intre în conflict cu SQLEXPRESS local) |
| loinc (Python) | 8000 | neexpus (doar rețeaua internă Docker) |

## Cum se pornește (pe Windows, în `C:\Projects\MedicalApp-repo`)

```powershell
git pull
copy .env.example .env
notepad .env          # completează parola SA + cheile Gemini/Brevo/Stripe (test)
docker compose build  # prima dată durează 10-20 min (PyTorch + modelul)
docker compose up -d
docker compose ps
docker compose logs -f app
```

Apoi: http://localhost:8080

## Comenzi utile

```powershell
docker compose logs -f app        # log-urile aplicației C#
docker compose logs -f loinc      # log-urile serviciului Python
docker compose restart app        # repornire după modificarea .env
docker compose down               # oprește (păstrează datele)
docker compose down -v            # oprește ȘI șterge baza de date (reset total)
docker compose exec app ls /app/files   # fișierele CAM din volum
```

Conectare la baza din container cu SSMS / Azure Data Studio:
`Server: localhost,14330` · `Login: sa` · `Password: cea din .env` ·
bifează *Trust server certificate*.

## Ce NU e încă rezolvat (pentru Azure)

- **Fișierele CAM** stau în volumul Docker `mma-files` (`CamSettings:Storage=LocalDisk`).
  Pe Azure cu mai multe instanțe se comută pe `Blob` — vezi `CAM_BLOB_STORAGE.md`.
- **ScaleOut** e `false`: sesiunea și cheile Data Protection sunt în memoria/discul
  containerului. Pentru 2+ instanțe vezi `SCALE_OUT.md`.
- **Webhook-ul Stripe** nu poate ajunge la `localhost`. Creditele se acordă la
  revenirea pe `StripeSuccess`. Webhook-ul real se configurează după ce aplicația
  are domeniu public (Azure) — vezi `STRIPE_PAYMENTS.md`.
- **Dicționarul LOINC** din SQL rămâne gol (CSV-ul LOINC nu e în repo, ~80 MB).
  Serviciul Python nu depinde de el la runtime — folosește `data/*.npy` din repo.
