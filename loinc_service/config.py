"""
config.py
---------
Centralized configuration for the LOINC matcher microservice.

All knobs are exposed as environment variables (no .env files in the repo;
deployment scripts inject them). Defaults match the typical Romanian
developer workstation: SQL Server Express on the same Windows host,
LoincDictionary table already populated, embeddings stored as .npy files
in `./data/` next to this script.
"""

from __future__ import annotations
import os
from pathlib import Path


# -------------------- Paths --------------------
BASE_DIR = Path(__file__).resolve().parent
DATA_DIR = BASE_DIR / "data"
DATA_DIR.mkdir(parents=True, exist_ok=True)

EMBEDDINGS_FILE = DATA_DIR / "loinc_embeddings.npy"
METADATA_FILE = DATA_DIR / "loinc_metadata.json"

# -------------------- Model --------------------
# Small, fast (~22M params), great quality for medical short-text matching.
# 384-dim embeddings × 97k LOINC rows ≈ 145 MB in RAM. Fine.
EMBEDDING_MODEL_NAME = os.environ.get(
    "LOINC_EMBEDDING_MODEL",
    "sentence-transformers/all-MiniLM-L6-v2",
)

# -------------------- SQL Server --------------------
# pyodbc connection string. Defaults match what's in appsettings.json on the
# user's Windows machine. Override with LOINC_DB_CONNSTR env var if needed.
DB_CONNECTION_STRING = os.environ.get(
    "LOINC_DB_CONNSTR",
    "DRIVER={ODBC Driver 17 for SQL Server};"
    "SERVER=PINTILIE\\SQLEXPRESS;"
    "DATABASE=MedicalAppDB;"
    "Trusted_Connection=yes;"
    "TrustServerCertificate=yes;",
)

# The table is created by the C# StartupSeed (MedicalApp). We only READ it.
LOINC_TABLE = os.environ.get("LOINC_TABLE", "LoincDictionary")

# -------------------- Matching weights --------------------
# Final score = SEM_WEIGHT * semantic + FUZZY_WEIGHT * fuzzy + RULES_WEIGHT * rules
SEM_WEIGHT = float(os.environ.get("LOINC_SEM_WEIGHT", "0.65"))
FUZZY_WEIGHT = float(os.environ.get("LOINC_FUZZY_WEIGHT", "0.30"))
RULES_WEIGHT = float(os.environ.get("LOINC_RULES_WEIGHT", "0.05"))

# Number of top semantic candidates to keep for the fuzzy+rules re-rank stage.
TOP_K = int(os.environ.get("LOINC_TOP_K", "25"))

# -------------------- Server --------------------
HOST = os.environ.get("LOINC_HOST", "127.0.0.1")
PORT = int(os.environ.get("LOINC_PORT", "8000"))
