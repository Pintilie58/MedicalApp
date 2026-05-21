"""
loinc_store.py
--------------
Loads the LOINC dictionary into memory ONCE at process startup:
  - the precomputed sentence-transformer embeddings (numpy array on disk),
  - the matching code/name metadata (a small JSON file).

We deliberately do NOT touch the SQL Server at runtime: matching is hot-path
and SQL round-trips would add latency. SQL Server is used only by
`seed_embeddings.py`, run once after the LoincDictionary table is populated.

The seed script produces two files in ./data/:
  - loinc_embeddings.npy    -> shape (N, 384), dtype float32
  - loinc_metadata.json     -> list of dicts with keys: loinc, name, component,
                              property, system, method, shortname

Both files are aligned: row i in embeddings matches entry i in metadata.
"""

from __future__ import annotations

import json
import logging
from pathlib import Path
from typing import List

import numpy as np

from config import EMBEDDINGS_FILE, METADATA_FILE

log = logging.getLogger("loinc.store")


class LoincStore:
    """In-memory holder for the LOINC matching corpus."""

    def __init__(self) -> None:
        self.embeddings: np.ndarray | None = None  # (N, dim) float32, L2-normalized
        self.metadata: List[dict] = []

    def load(self) -> None:
        if not EMBEDDINGS_FILE.exists() or not METADATA_FILE.exists():
            raise FileNotFoundError(
                "LOINC embeddings/metadata files are missing. "
                f"Expected:\n  - {EMBEDDINGS_FILE}\n  - {METADATA_FILE}\n"
                "Run seed_embeddings.py first to generate them from SQL Server."
            )

        log.info("Loading LOINC metadata from %s", METADATA_FILE)
        with open(METADATA_FILE, "r", encoding="utf-8") as f:
            self.metadata = json.load(f)

        log.info("Loading LOINC embeddings from %s", EMBEDDINGS_FILE)
        self.embeddings = np.load(EMBEDDINGS_FILE)

        if self.embeddings.shape[0] != len(self.metadata):
            raise RuntimeError(
                f"Embeddings/metadata length mismatch: "
                f"{self.embeddings.shape[0]} vs {len(self.metadata)}. "
                "Re-run seed_embeddings.py."
            )

        # Make sure the embeddings are L2-normalized so cosine similarity reduces
        # to a single matrix-vector dot product later.
        norms = np.linalg.norm(self.embeddings, axis=1, keepdims=True)
        norms[norms == 0] = 1.0
        self.embeddings = (self.embeddings / norms).astype(np.float32)

        log.info(
            "LoincStore loaded: %d entries, embedding dim=%d, ~%.1f MB.",
            len(self.metadata),
            self.embeddings.shape[1],
            self.embeddings.nbytes / 1_000_000,
        )

    @property
    def size(self) -> int:
        return len(self.metadata)


# Module-level singleton (LoincStore is read-only after load).
STORE = LoincStore()
