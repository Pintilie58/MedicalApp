# LOINC Matcher — microserviciu Python pentru MedicalApp

Acesta este motorul deterministic de mapare LOINC. Înlocuiește pașii vechi
unde Gemini emitea direct coduri LOINC (cu halucinări frecvente).

## Cum funcționează (pe scurt)

```
PDF analiză
   ↓
Gemini extrage parametrii + numele lor în engleză standardizat
   (parameter_normalized_en — fără cod LOINC)
   ↓
ASP.NET MedicalApp trimite numele la acest microserviciu (HTTP)
   ↓
Python rulează: semantic search (embeddings) + fuzzy match + reguli
   ↓
Întoarce codul LOINC oficial cu scor de încredere
   ↓
ASP.NET salvează codul în DB și îl folosește pentru Compare + grafice
```

## Cerințe Windows

- Python 3.10 sau 3.11 (descarcă de la [python.org](https://www.python.org/downloads/) — alege „Add Python to PATH" la instalare)
- Microsoft ODBC Driver for SQL Server 17 sau 18 ([download gratuit](https://learn.microsoft.com/en-us/sql/connect/odbc/download-odbc-driver-for-sql-server))
- SQL Server local cu baza `MedicalAppDB` și tabela `LoincDictionary` deja populată
  (asta o face C# StartupSeed la primul build al aplicației)

## Setup local — o singură dată

Deschide **PowerShell** în folderul `loinc_service/` și rulează:

```powershell
# 1. Creează un mediu virtual Python (izolat de restul PC-ului)
python -m venv .venv

# 2. Activează mediul
.\.venv\Scripts\Activate.ps1

# 3. Instalează dependențele
pip install -r requirements.txt

# 4. Generează embeddings — durează 5-15 minute, ulează O DATĂ
python seed_embeddings.py
```

> 💡 **Pentru un test rapid** (1000 de coduri, ~30 secunde):
> `python seed_embeddings.py --limit 1000`

După seeding, în folderul `data/` vei avea:
- `loinc_embeddings.npy` (~145 MB) — vectorii numerici
- `loinc_metadata.json` (~30 MB) — codul + numele LOINC

## Pornește microserviciul

```powershell
# Cu mediul activat (.venv)
uvicorn main:app --host 127.0.0.1 --port 8000
```

Verifică în browser:
- <http://127.0.0.1:8000/health> → `{"status":"ok"}`
- <http://127.0.0.1:8000/ready> → arată câte coduri sunt încărcate
- <http://127.0.0.1:8000/docs> → UI interactiv FastAPI (poți testa direct)

## Testează un cod LOINC

```powershell
# PowerShell:
$body = '{"test_name":"Glucose [Mass/volume] in Serum or Plasma"}'
Invoke-RestMethod -Uri http://127.0.0.1:8000/loinc/match -Method Post -Body $body -ContentType "application/json"
```

Răspuns așteptat:
```json
{
  "loinc": "2345-7",
  "name": "Glucose [Mass/volume] in Serum or Plasma",
  "component": "Glucose",
  "property": "MCnc",
  "system": "Ser/Plas",
  "method": null,
  "score": 0.97
}
```

## Configurare MedicalApp pentru a folosi microserviciul

În `MedicalApp/appsettings.json` adaugă (deja făcut):

```json
"LoincMatcher": {
    "BaseUrl": "http://localhost:8000",
    "Enabled": true,
    "TimeoutSeconds": 5,
    "MinScore": 0.55
}
```

Pornește MedicalApp în VS2022 ca de obicei. La fiecare interpretare nouă,
C# va apela acest microserviciu pentru fiecare parametru.

## Pornire automată la fiecare boot Windows (opțional)

Cel mai simplu: creează un fișier `start_loinc.bat` pe Desktop cu:
```bat
@echo off
cd /d C:\Projects\MedicalApp-repo\loinc_service
call .venv\Scripts\activate.bat
uvicorn main:app --host 127.0.0.1 --port 8000
```
Și pune scurtătura la **Startup** (Win+R → `shell:startup`).

## Mutarea pe VPS în viitor

Codul este 100% portabil. Singurele schimbări la mutarea pe Linux/cloud:
1. Conexiunea SQL Server din `config.py` (variabila `LOINC_DB_CONNSTR`)
2. Sau și mai bine: rulezi `seed_embeddings.py` pe Windows o singură dată,
   copiezi cele 2 fișiere din `data/` pe server, și serverul nu mai are
   nevoie deloc de acces la SQL Server.

## Troubleshooting

**"Module not found" → ai uitat să activezi .venv** înainte să rulezi.

**"ODBC Driver not found"** → instalează [ODBC Driver 17](https://learn.microsoft.com/en-us/sql/connect/odbc/download-odbc-driver-for-sql-server).

**"Cannot connect to SQL Server"** → verifică în `config.py` (linia `DB_CONNECTION_STRING`)
că serverul / DB-ul / autentificarea Windows sunt corecte pentru PC-ul tău.

**Embeddings file missing** → rulează `python seed_embeddings.py` o dată.

**MedicalApp dă timeout pe /loinc/match** → verifică `/ready` în browser; dacă
arată „not_ready", microserviciul încă încarcă embeddings (~5 secunde la pornire).
