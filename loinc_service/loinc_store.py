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
import re
from pathlib import Path
from typing import List

import numpy as np

from canonical_anchors import canon_key
from config import EMBEDDINGS_FILE, METADATA_FILE


_BRACKET_RE = re.compile(r"\[([^\]]+)\]")


def parse_loinc_axes(name: str) -> dict:
    """Derive (component, property, system, method) from a LOINC long common
    name, which follows the strict grammar::

        <Component> [<Property>] in|of <System> by <Method>

    Used as a FALLBACK when the SQL seed lacks the axis columns (the C#
    ``LoincDictionary`` table only ships LoincCode + LongCommonName + Class),
    which otherwise silently disables the unit-swap / peer-search / guard
    logic in the pipeline. Examples::

        "Cholesterol in HDL [Mass/volume] in Serum or Plasma"
            -> component="Cholesterol in HDL", property="Mass/volume",
               system="Serum or Plasma", method=None
        "Hematocrit [Volume Fraction] of Blood by Automated count"
            -> component="Hematocrit", property="Volume Fraction",
               system="Blood", method="Automated count"
        "pH of Urine by Test strip"
            -> component="pH", system="Urine", method="Test strip"
    """
    if not name:
        return {}
    n = re.sub(r"\s+", " ", name).strip()

    method = None
    core = n
    idx_by = n.rfind(" by ")
    if idx_by > 0:
        method = n[idx_by + 4:].strip() or None
        core = n[:idx_by].strip()

    prop = None
    component = core
    system = None
    m_br = _BRACKET_RE.search(core)
    if m_br:
        prop = m_br.group(1).strip() or None
        component = core[:m_br.start()].strip()
        rest = core[m_br.end():].strip()
        if rest.startswith("in ") or rest.startswith("of "):
            system = rest[3:].strip()
        elif rest:
            system = rest
    else:
        # No bracket. Split on the LAST " in "/" of " so components that
        # legitimately contain "in" ("Cholesterol in HDL") stay intact.
        idx_sys = max(core.rfind(" in "), core.rfind(" of "))
        if idx_sys > 0:
            component = core[:idx_sys].strip()
            system = core[idx_sys + 4:].strip()

    return {
        "component": component or None,
        "property": prop,
        "system": system or None,
        "method": method,
    }

log = logging.getLogger("loinc.store")


class LoincStore:
    """In-memory holder for the LOINC matching corpus."""

    def __init__(self) -> None:
        self.embeddings: np.ndarray | None = None  # (N, dim) float32, L2-normalized
        self.metadata: List[dict] = []
        # Fast lookup: LOINC code -> index into self.metadata. Built once at
        # `load()` time so the canonical-anchors layer can resolve a code in
        # O(1) without scanning the 97k-row metadata list.
        self.code_index: dict[str, int] = {}
        # Fast lookup: normalized LONG_COMMON_NAME -> index. Lets the matcher
        # deterministically accept a query that IS a verbatim LOINC name
        # (e.g. "Cholesterol in LDL [Mass/volume] in Serum or Plasma by
        # calculation") without going through the probabilistic pipeline.
        self.name_index: dict[str, int] = {}
        self.names_norm: list[str] = []

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

        # AXIS ENRICHMENT: when the SQL seed lacks Component/Property/System/
        # Method columns (the C# LoincDictionary table only ships LoincCode +
        # LongCommonName + Class), derive the axes from the long name so the
        # unit-swap, peer-search and guard logic stay functional.
        enriched = 0
        for m in self.metadata:
            if m.get("component") and m.get("property") and m.get("system"):
                continue
            axes = parse_loinc_axes(m.get("name") or "")
            filled = False
            for k in ("component", "property", "system", "method"):
                if not m.get(k) and axes.get(k):
                    m[k] = axes[k]
                    filled = True
            if filled:
                enriched += 1
        if enriched:
            log.info(
                "Axis enrichment: derived component/property/system/method from "
                "the long name for %d of %d entries (SQL seed lacks axis columns).",
                enriched, len(self.metadata),
            )

        # Lexical index for candidate-pool injection (see pipeline): plain
        # lowercased long names, aligned with self.metadata indices.
        self.names_norm = [(m.get("name") or "").lower() for m in self.metadata]

        # Build the code -> index map. We keep the FIRST occurrence of a code
        # (LOINC codes are unique by spec, but if the seed file accidentally
        # contains duplicates we prefer the earlier row).
        self.code_index = {}
        self.name_index = {}
        for i, m in enumerate(self.metadata):
            code = (m.get("loinc") or "").strip()
            if code and code not in self.code_index:
                self.code_index[code] = i
            name = canon_key(m.get("name") or "")
            if name and name not in self.name_index:
                self.name_index[name] = i

        log.info(
            "LoincStore loaded: %d entries, embedding dim=%d, ~%.1f MB.",
            len(self.metadata),
            self.embeddings.shape[1],
            self.embeddings.nbytes / 1_000_000,
        )

    def get_by_code(self, loinc: str) -> dict | None:
        """Return the metadata dict for ``loinc`` (or None if not in the store)."""
        if not loinc:
            return None
        idx = self.code_index.get(loinc.strip())
        if idx is None:
            return None
        return self.metadata[idx]

    def get_by_name(self, name: str) -> dict | None:
        """Return the metadata dict whose LONG_COMMON_NAME equals ``name``
        after anchor-style canonicalization (case/whitespace/preposition/
        Ag-Ab-insensitive), or None."""
        if not name:
            return None
        idx = self.name_index.get(canon_key(name))
        if idx is None:
            return None
        return self.metadata[idx]

    @property
    def size(self) -> int:
        return len(self.metadata)


# Module-level singleton (LoincStore is read-only after load).
STORE = LoincStore()
