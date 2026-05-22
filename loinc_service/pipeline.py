"""
pipeline.py
-----------
The deterministic LOINC matcher.

Given a normalized English medical term emitted by Gemini (e.g.
"Glucose [Mass/volume] in Serum or Plasma"), it returns the best-matching
LOINC code from the local 97k-entry LoincDictionary using:

    final_score = SEM_WEIGHT * semantic + FUZZY_WEIGHT * fuzzy + RULES_WEIGHT * rules

Where:
  semantic  = cosine similarity between query embedding and LOINC embedding
  fuzzy     = rapidfuzz token_set_ratio between query and LONG_COMMON_NAME
  rules     = +1 boost for each rule the candidate satisfies (specimen,
              method, property), 0 otherwise; clamped to [0, 1]

We compute semantic over ALL 97k codes in one vectorized numpy operation
(~5-15 ms), take the top-K (default 25), then run fuzzy + rules on that
short list. This keeps total latency under 100 ms even on a laptop.
"""

from __future__ import annotations

import logging
import re
import threading
from dataclasses import dataclass
from typing import List, Optional

import numpy as np
from rapidfuzz import fuzz
from sentence_transformers import SentenceTransformer

from config import (
    EMBEDDING_MODEL_NAME,
    FUZZY_WEIGHT,
    RULES_WEIGHT,
    SEM_WEIGHT,
    TOP_K,
)
from canonical_anchors import lookup_anchor
from loinc_store import STORE

log = logging.getLogger("loinc.pipeline")


# -------------------------------------------------------------------------
# Embedding model — loaded once, shared across requests.
# -------------------------------------------------------------------------
_MODEL_LOCK = threading.Lock()
_MODEL: Optional[SentenceTransformer] = None


def get_model() -> SentenceTransformer:
    global _MODEL
    if _MODEL is None:
        with _MODEL_LOCK:
            if _MODEL is None:
                log.info("Loading embedding model: %s", EMBEDDING_MODEL_NAME)
                _MODEL = SentenceTransformer(EMBEDDING_MODEL_NAME)
                log.info("Embedding model ready.")
    return _MODEL


@dataclass
class MatchResult:
    loinc: str
    name: str
    component: Optional[str]
    property: Optional[str]
    system: Optional[str]
    method: Optional[str]
    score: float

    def to_dict(self) -> dict:
        return {
            "loinc": self.loinc,
            "name": self.name,
            "component": self.component,
            "property": self.property,
            "system": self.system,
            "method": self.method,
            "score": float(self.score),
        }


# -------------------------------------------------------------------------
# Rules engine — small, hand-curated list of "must-have / must-not-have"
# constraints derived from the query text. Adding a new rule is cheap.
# -------------------------------------------------------------------------
_SPECIMEN_KEYWORDS = {
    # query keyword -> set of LOINC SYSTEM strings that satisfy it
    "serum":       {"ser", "ser/plas", "serum", "plasma"},
    "plasma":      {"plas", "ser/plas", "serum", "plasma"},
    "ser/plas":    {"ser", "plas", "ser/plas", "serum", "plasma"},
    "blood":       {"bld", "blood"},
    "whole blood": {"bld", "blood"},
    "urine":       {"urine", "urine sediment", "urn"},
    "csf":         {"csf"},
    "stool":       {"stool", "feces"},
    "saliva":      {"saliva"},
}

_METHOD_KEYWORDS = {
    "test strip":  {"test strip", "dipstick"},
    "dipstick":    {"test strip", "dipstick"},
    "westergren":  {"westergren"},
    "microscopy":  {"microscopy"},
    "calculation": {"calculation", "calculated"},
    "direct":      {"direct"},
    "ifcc":        {"ifcc"},
}

_PROPERTY_KEYWORDS = {
    "mass/volume":      {"mcnc", "mass/volume"},
    "mass/time":        {"mrat"},
    "fraction":         {"mfr", "nfr", "fraction"},
    "rate":             {"rate"},
    "enzymatic":        {"ccnc", "catalytic activity/volume", "enzymatic activity/volume"},
    "presence":         {"prid", "ord", "presence"},
    "ratio":            {"ratio"},
}


def _normalize(s: str) -> str:
    return re.sub(r"\s+", " ", s.lower()).strip()


def _apply_rules(query_norm: str, candidate: dict) -> float:
    """Return rules score in [0, 1] — fraction of rules satisfied. We only
    apply rules that the query EXPLICITLY mentions (specimen/method/property
    keywords). Other candidates get rules=1.0 (neutral, no penalty)."""
    sys_val = (candidate.get("system") or "").lower()
    meth_val = (candidate.get("method") or "").lower()
    prop_val = (candidate.get("property") or "").lower()
    name_val = (candidate.get("name") or "").lower()

    checks_made = 0
    checks_passed = 0

    for kw, allowed in _SPECIMEN_KEYWORDS.items():
        if kw in query_norm:
            checks_made += 1
            if any(a in sys_val or a in name_val for a in allowed):
                checks_passed += 1

    for kw, allowed in _METHOD_KEYWORDS.items():
        if kw in query_norm:
            checks_made += 1
            if any(a in meth_val or a in name_val for a in allowed):
                checks_passed += 1

    for kw, allowed in _PROPERTY_KEYWORDS.items():
        if kw in query_norm:
            checks_made += 1
            if any(a in prop_val or a in name_val for a in allowed):
                checks_passed += 1

    if checks_made == 0:
        # No rule keywords in query — don't penalize, don't boost.
        return 1.0
    return checks_passed / checks_made


# -------------------------------------------------------------------------
# Hard disambiguation penalties — applied AFTER soft rules.
# -------------------------------------------------------------------------
# These cover cases where two LOINC codes are extremely close in embedding
# space ("Erythrocyte mean corpuscular VOLUME" vs "...DIAMETER" vs
# "...HEMOGLOBIN") and the semantic + fuzzy step alone cannot tell them apart.
# Each entry says: if the query mentions FORBIDDEN_KEYWORD but the candidate's
# long_name mentions any of REJECT_TOKENS, divide the candidate score by 4
# (effectively pushing it off the top of the list). This is intentionally
# narrow and targeted — only six entries — so it cannot cause false rejects
# elsewhere in the LOINC space.
_HARD_REJECT_RULES: list[tuple[str, set[str], set[str]]] = [
    # (label, query_keywords, candidate_long_name_tokens_to_reject)
    ("MCV-not-diameter",
     {"volume", "mcv"},
     {"diameter"}),
    ("MCH-not-diameter",
     {"hemoglobin", "mch"},
     {"diameter"}),
    ("MCHC-not-diameter",
     {"concentration", "mchc"},
     {"diameter"}),
    ("erythrocyte-volume-not-diameter",
     {"erythrocyte mean corpuscular volume"},
     {"diameter"}),
    ("erythrocyte-hemoglobin-not-diameter",
     {"erythrocyte mean corpuscular hemoglobin"},
     {"diameter"}),
]


def _hard_reject_penalty(query_norm: str, candidate_name: str) -> float:
    """Return a multiplier in (0, 1] to apply to the final score. 1.0 means
    no penalty. Anything less aggressively pushes the candidate down the
    ranking. Currently only severe (0.25x) penalty when one of the narrow
    disambiguation rules above fires."""
    cand_lower = candidate_name.lower()
    for _label, q_keywords, reject_tokens in _HARD_REJECT_RULES:
        if any(kw in query_norm for kw in q_keywords):
            if any(rt in cand_lower for rt in reject_tokens):
                return 0.25
    return 1.0


# -------------------------------------------------------------------------
# Public API
# -------------------------------------------------------------------------
def find_loinc(test_name: str) -> Optional[MatchResult]:
    """Resolve the best LOINC code for an English medical test name."""
    if STORE.embeddings is None or not STORE.metadata:
        raise RuntimeError("LoincStore is not loaded. Call STORE.load() first.")
    if not test_name or not test_name.strip():
        return None

    # 0. HARD-ACCEPT LAYER — canonical anchors short-circuit the matcher
    # when Gemini emits one of the curated standardized English terms (see
    # canonical_anchors.py). This eliminates the systematic mis-mappings of
    # MCV/MCH/MCHC/RDW/WBC that the embedding model can't disambiguate.
    anchor_code = lookup_anchor(test_name)
    if anchor_code is not None:
        meta = STORE.get_by_code(anchor_code)
        if meta is not None:
            log.info(
                "ANCHOR hit for %r -> %s %r (score=1.000, confidence=exact).",
                test_name, anchor_code, meta.get("name") or "",
            )
            return MatchResult(
                loinc=meta["loinc"],
                name=meta.get("name") or "",
                component=meta.get("component"),
                property=meta.get("property"),
                system=meta.get("system"),
                method=meta.get("method"),
                score=1.0,
            )
        # Anchor code not present in the loaded LoincDictionary — log a
        # warning ONCE and fall through to the semantic matcher so we
        # never serve a 404 just because of a stale seed.
        log.warning(
            "ANCHOR for %r maps to code %s but that code is missing from "
            "the loaded LoincStore (partial seed?). Falling back to semantic match.",
            test_name, anchor_code,
        )

    query_norm = _normalize(test_name)
    model = get_model()

    # 1. Semantic similarity (vectorized over all LOINC rows).
    q_emb = model.encode([test_name], normalize_embeddings=True)[0].astype(np.float32)
    # Embeddings in STORE are already L2-normalized -> dot product = cosine sim.
    sims: np.ndarray = STORE.embeddings @ q_emb  # shape (N,)

    # Pick top-K candidates. argpartition requires k < N; when our corpus is
    # smaller (sample tests, partial seeds) just take all candidates.
    k = min(TOP_K, sims.shape[0] - 1)
    if k <= 0:
        top_idx = np.argsort(-sims)
    else:
        top_idx = np.argpartition(-sims, k)[: k + 1]
        top_idx = top_idx[np.argsort(-sims[top_idx])]

    # 2. For each top-K candidate, compute fuzzy and rules scores.
    candidates: List[tuple[float, MatchResult]] = []
    for i in top_idx:
        meta = STORE.metadata[int(i)]
        sem = float(sims[int(i)])
        long_name = meta.get("name") or ""
        comp = meta.get("component") or ""

        # Token-set ratio is robust against word reordering ("LDL cholesterol" vs
        # "Cholesterol in LDL"). We compare against BOTH long_name and component
        # and keep the best of the two.
        f_long = fuzz.token_set_ratio(query_norm, long_name.lower()) / 100.0
        f_comp = fuzz.token_set_ratio(query_norm, comp.lower()) / 100.0
        fz = max(f_long, f_comp)

        rl = _apply_rules(query_norm, meta)

        final = SEM_WEIGHT * sem + FUZZY_WEIGHT * fz + RULES_WEIGHT * rl

        # Apply narrow hard-rejection penalties for known close-neighbor
        # ambiguities (e.g. MCV / MCH / MCHC vs Erythrocyte diameter).
        final *= _hard_reject_penalty(query_norm, long_name)

        candidates.append((
            final,
            MatchResult(
                loinc=meta["loinc"],
                name=meta.get("name") or "",
                component=meta.get("component"),
                property=meta.get("property"),
                system=meta.get("system"),
                method=meta.get("method"),
                score=final,
            ),
        ))

    candidates.sort(key=lambda x: x[0], reverse=True)
    return candidates[0][1] if candidates else None
