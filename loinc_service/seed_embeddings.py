"""
seed_embeddings.py
------------------
ONE-TIME script (run again only when LOINC dictionary is updated).

Reads the LoincDictionary table from SQL Server (the one MedicalApp's C#
StartupSeed has already populated with ~97k entries), computes 384-dim
sentence-transformer embeddings for each row, and saves two aligned files:

  data/loinc_embeddings.npy  -> numpy array, shape (N, 384), float32
  data/loinc_metadata.json   -> list of dicts (loinc, name, ...)

These are the files that loinc_store.LoincStore.load() consumes at runtime.

Usage (PowerShell on Windows):
    cd loinc_service
    python -m venv .venv
    .venv\Scripts\Activate.ps1
    pip install -r requirements.txt
    python seed_embeddings.py

Expected runtime: 5-15 minutes on a modern CPU (97k rows, batch size 128).
Output size: ~145 MB embeddings + ~30 MB metadata.
"""

from __future__ import annotations

import argparse
import json
import logging
import sys
import time
from pathlib import Path
from typing import List

import numpy as np

from config import (
    DB_CONNECTION_STRING,
    EMBEDDING_MODEL_NAME,
    EMBEDDINGS_FILE,
    LOINC_TABLE,
    METADATA_FILE,
)

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s [seed] %(message)s",
)
log = logging.getLogger("seed")


# -------------------- LoincDictionary discovery --------------------
# The LoincDictionary table is created by C# StartupSeed (MedicalApp).
# It always has at least the fields LoincCode and LongCommonName. Some
# extended seeds also populate OrderObs (free text with class/component).
# We probe with a tolerant SELECT to work with both layouts.

KNOWN_COLUMNS = [
    "LoincCode", "LongCommonName", "OrderObs",
    "Component", "Property", "System", "MethodTyp", "Shortname",
    "Class",
]


def discover_columns(cursor) -> List[str]:
    cursor.execute(f"""
        SELECT COLUMN_NAME
        FROM INFORMATION_SCHEMA.COLUMNS
        WHERE TABLE_NAME = '{LOINC_TABLE}'
    """)
    cols = {r[0] for r in cursor.fetchall()}
    chosen = [c for c in KNOWN_COLUMNS if c in cols]
    if "LoincCode" not in chosen or "LongCommonName" not in chosen:
        raise RuntimeError(
            f"{LOINC_TABLE} is missing the required LoincCode / LongCommonName columns. "
            f"Got: {sorted(cols)}"
        )
    log.info("Using columns from %s: %s", LOINC_TABLE, chosen)
    return chosen


def load_loinc_rows(chosen_cols: List[str]) -> List[dict]:
    """Fetch all rows as a list of dicts, schema-agnostic over the available columns."""
    import pyodbc

    sel = ", ".join(chosen_cols)
    log.info("Connecting to SQL Server (driver via pyodbc)...")
    with pyodbc.connect(DB_CONNECTION_STRING, timeout=10) as conn:
        cursor = conn.cursor()
        cursor.execute(f"SELECT {sel} FROM {LOINC_TABLE}")
        rows = cursor.fetchall()
    log.info("Fetched %d rows from %s", len(rows), LOINC_TABLE)
    return [{c: r[i] for i, c in enumerate(chosen_cols)} for r in rows]


# -------------------- Text builder --------------------
def build_text(row: dict) -> str:
    """Concatenate the fields that carry medical-meaning signal, separated
    by ' | ' so the embedding model can attend to each one."""
    parts = []
    for field in ("LongCommonName", "Component", "Property",
                  "System", "MethodTyp", "Shortname", "OrderObs"):
        val = row.get(field)
        if val is not None and str(val).strip():
            parts.append(str(val).strip())
    return " | ".join(parts) if parts else ""


# -------------------- Embedding --------------------
def compute_embeddings(texts: List[str], model_name: str, batch_size: int = 128) -> np.ndarray:
    from sentence_transformers import SentenceTransformer

    log.info("Loading model %s ...", model_name)
    model = SentenceTransformer(model_name)
    log.info("Encoding %d texts (batch=%d) ...", len(texts), batch_size)

    t0 = time.time()
    embs = model.encode(
        texts,
        batch_size=batch_size,
        show_progress_bar=True,
        normalize_embeddings=True,   # store unit vectors for fast cosine sim later
        convert_to_numpy=True,
    ).astype(np.float32)
    log.info("Encoding done in %.1fs. shape=%s dtype=%s", time.time() - t0, embs.shape, embs.dtype)
    return embs


# -------------------- Output writers --------------------
def write_metadata(rows: List[dict], out_path: Path) -> None:
    """Persist a slim metadata file that aligns 1:1 with the embeddings array."""
    payload = [
        {
            "loinc": (r.get("LoincCode") or "").strip(),
            "name": (r.get("LongCommonName") or "").strip(),
            "component": (r.get("Component") or "").strip() or None,
            "property": (r.get("Property") or "").strip() or None,
            "system": (r.get("System") or "").strip() or None,
            "method": (r.get("MethodTyp") or "").strip() or None,
            "shortname": (r.get("Shortname") or "").strip() or None,
            "class": (r.get("Class") or "").strip() or None,
        }
        for r in rows
    ]
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(payload, f, ensure_ascii=False)
    log.info("Wrote metadata to %s (%d entries, %.1f MB)",
             out_path, len(payload), out_path.stat().st_size / 1_000_000)


def write_embeddings(embs: np.ndarray, out_path: Path) -> None:
    np.save(out_path, embs)
    log.info("Wrote embeddings to %s (%.1f MB)",
             out_path, out_path.stat().st_size / 1_000_000)


# -------------------- Main --------------------
def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--limit", type=int, default=0,
                        help="Only embed the first N rows (for quick local tests).")
    parser.add_argument("--batch-size", type=int, default=128)
    args = parser.parse_args()

    import pyodbc
    try:
        cursor = pyodbc.connect(DB_CONNECTION_STRING, timeout=10).cursor()
    except Exception as ex:
        log.error("Cannot connect to SQL Server. Connection string: %r", DB_CONNECTION_STRING)
        log.error("Error: %s", ex)
        return 2

    chosen = discover_columns(cursor)
    rows = load_loinc_rows(chosen)

    if args.limit > 0:
        rows = rows[: args.limit]
        log.info("Limiting to first %d rows (per --limit).", args.limit)

    rows = [r for r in rows if (r.get("LongCommonName") or "").strip()]
    log.info("After dropping rows with empty LongCommonName: %d rows", len(rows))

    texts = [build_text(r) for r in rows]
    embs = compute_embeddings(texts, EMBEDDING_MODEL_NAME, batch_size=args.batch_size)

    write_embeddings(embs, EMBEDDINGS_FILE)
    write_metadata(rows, METADATA_FILE)
    log.info("DONE. The microservice can now be started with: uvicorn main:app --port 8000")
    return 0


if __name__ == "__main__":
    sys.exit(main())
