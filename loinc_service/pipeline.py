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
    loinc_class: Optional[str] = None
    # Provenance of the LOINC mapping. "anchor" => hard-curated anchor in
    # canonical_anchors.py (deterministic, score 1.0). "semantic" => result
    # of the embedding + fuzzy + rules pipeline (probabilistic). The UI uses
    # this to badge anchored parameters as "verified" and semantic ones as
    # "auto-suggested" — important for patient confidence on common analytes
    # (CBC, lipid panel, liver enzymes) where anchors give certainty.
    source: str = "semantic"

    def to_dict(self) -> dict:
        return {
            "loinc": self.loinc,
            "name": self.name,
            "component": self.component,
            "property": self.property,
            "system": self.system,
            "method": self.method,
            "score": float(self.score),
            "loinc_class": self.loinc_class,
            "loinc_source": self.source,
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


# -------------------------------------------------------------------------
# Unit-aware property inference (Issue: Gemini emits the "Mass/volume"
# LOINC name even when the reported unit is pmol/L — a Moles/volume unit —
# producing systematically wrong codes for paired analytes like FT3/FT4,
# Glucose, Cholesterol etc.). We post-correct the matcher result by
# detecting the property family implied by the unit string and swapping
# to the corresponding Mass↔Moles peer when there is a mismatch.
#
# Coverage is intentionally narrow: we only handle Mass/volume ↔
# Moles/volume because that's the only pair where the SAME analyte
# legitimately lives under two LOINC codes that the matcher can't
# disambiguate from the parameter name alone. Other property families
# (enzymatic activity, mass fraction, count/volume) have unambiguous
# unit-to-property mappings that the matcher already resolves correctly
# via the `_apply_rules` layer.
# -------------------------------------------------------------------------
# Map LOINC's `property` field values to our normalized family name.
# LOINC stores it as either the short form ("MCnc", "SCnc") or the long form
# ("Mass/volume", "Moles/volume"), depending on the source CSV. We accept
# both so the unit-swap logic works regardless of how the dictionary was
# seeded.
_MASS_PROPERTY_TOKENS = {"mcnc", "mass/volume", "mass concentration"}
_MOLES_PROPERTY_TOKENS = {"scnc", "moles/volume", "substance concentration"}


def _property_family(prop: Optional[str]) -> Optional[str]:
    if not prop: return None
    p = prop.strip().lower()
    if p in _MASS_PROPERTY_TOKENS: return "Mass/volume"
    if p in _MOLES_PROPERTY_TOKENS: return "Moles/volume"
    return None


_MOLES_UNIT_TOKENS = (
    "mol/l", "mmol/l", "umol/l", "µmol/l", "μmol/l", "nmol/l", "pmol/l",
    "mol/ml", "mmol/ml", "umol/ml", "nmol/ml", "pmol/ml",
)
_MASS_UNIT_TOKENS = (
    "g/l", "g/dl", "g/ml",
    "mg/l", "mg/dl", "mg/ml",
    "ug/l", "ug/dl", "ug/ml", "µg/l", "µg/dl", "µg/ml",
    "ng/l", "ng/dl", "ng/ml",
    "pg/l", "pg/dl", "pg/ml",
)


def _infer_property_from_unit(unit: Optional[str]) -> Optional[str]:
    """
    Returns "Moles/volume" or "Mass/volume" when the unit string clearly
    falls in one of those families; None otherwise. Matching is done on a
    lowercased, whitespace-stripped form so it tolerates the dozens of
    capitalization variants Gemini emits ("Pmol/L", "PMOL/L", "pmol / L",
    etc.).
    """
    if not unit:
        return None
    u = re.sub(r"\s+", "", unit.lower())
    # Order matters: check moles tokens FIRST since "mol" is a substring
    # used inside "umol", "nmol", etc. — but tokens are pre-disambiguated
    # by always including the denominator (/l or /ml).
    if any(tok in u for tok in _MOLES_UNIT_TOKENS):
        return "Moles/volume"
    if any(tok in u for tok in _MASS_UNIT_TOKENS):
        return "Mass/volume"
    return None


def _find_peer_with_property(
    component: Optional[str],
    system: Optional[str],
    method: Optional[str],
    target_property: str,
) -> Optional[dict]:
    """
    Scan STORE.metadata for a LOINC entry that shares the same
    (component, system, optional method) as the original match but with
    the desired property (Mass/volume or Moles/volume). Returns the
    metadata dict for the peer, or None when no peer exists in the
    loaded dictionary.

    We deliberately keep `method` loose — when the original match has a
    method like "IA" or "Spectrophotometry", an exact match would be too
    strict (the Moles/volume peer often has method=NULL). So we accept a
    peer with the same method OR an empty method.
    """
    if not component or not system or not target_property:
        return None
    comp_lc = component.strip().lower()
    sys_lc = system.strip().lower()
    method_lc = (method or "").strip().lower()

    best: Optional[dict] = None
    for entry in STORE.metadata:
        # Tolerant match on the property family (handles both LOINC's short
        # form "MCnc"/"SCnc" and the long form "Mass/volume"/"Moles/volume").
        if _property_family(entry.get("property")) != target_property:
            continue
        e_comp = (entry.get("component") or "").strip().lower()
        e_sys = (entry.get("system") or "").strip().lower()
        if e_comp != comp_lc or e_sys != sys_lc:
            continue
        e_method = (entry.get("method") or "").strip().lower()
        # Prefer same-method peer, fall back to any-method peer.
        if e_method == method_lc:
            return entry
        if best is None and (not e_method or not method_lc):
            best = entry
    return best


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
def find_loinc(
    test_name: str,
    unit: Optional[str] = None,
    raw_parameter_name: Optional[str] = None,
    panel_header_raw: Optional[str] = None,
    analyte_line_raw: Optional[str] = None,
) -> Optional[MatchResult]:
    """Resolve the best LOINC code for an English medical test name.

    When ``unit`` is provided we post-correct the match: if the unit
    indicates Moles/volume (e.g. pmol/L, nmol/L) but the chosen LOINC
    has property Mass/volume (or vice-versa), swap to the
    same-component peer LOINC that has the desired property. Fixes the
    systematic Gemini mistake of emitting "Triiodothyronine free
    [Mass/volume]" when the lab actually reported FT3 in pmol/L
    (correct LOINC = 14928-6, not 3051-0).

    Etapa Python-2/3 additions
    --------------------------
    ``raw_parameter_name`` (Python-2): the ORIGINAL analyte name printed in
        the PDF (e.g. 'Proteina C reactiva') before Gemini normalization.
        Used inside the fuzzy layer as an alternative comparison source
        against LOINC long_name / component. Robust against cases where
        Gemini's English normalization drifts semantically (e.g. emits
        'Blood cell count' for a row that actually says 'Leucocite') —
        the raw name still matches the correct candidate.
    ``panel_header_raw`` / ``analyte_line_raw`` (Python-3): verbatim
        source-context strings copied by Gemini from the PDF (panel
        header, per-row inline metadata). Reserved for the rules layer
        (Etapa Python-3): keyword extraction for method / specimen
        disambiguation across LOINC axes. Currently accepted here for
        API stability but NOT yet consumed inside ``_semantic_match``.
    """
    if STORE.embeddings is None or not STORE.metadata:
        raise RuntimeError("LoincStore is not loaded. Call STORE.load() first.")
    if not test_name or not test_name.strip():
        return None

    result = _semantic_match(
        test_name,
        raw_parameter_name=raw_parameter_name,
        panel_header_raw=panel_header_raw,
        analyte_line_raw=analyte_line_raw,
    )
    if result is None:
        return result

    # Unit-aware post-correction.
    desired_property = _infer_property_from_unit(unit)
    current_family = _property_family(result.property)
    if (desired_property
            and current_family
            and desired_property != current_family):
        peer = _find_peer_with_property(
            result.component, result.system, result.method, desired_property)
        if peer is not None:
            log.info(
                "UNIT-SWAP %r (unit=%r) %s [%s] -> %s [%s] (component=%r)",
                test_name, unit, result.loinc, result.property,
                peer.get("loinc"), peer.get("property"), result.component,
            )
            return MatchResult(
                loinc=peer["loinc"],
                name=peer.get("name") or "",
                component=peer.get("component"),
                property=peer.get("property"),
                system=peer.get("system"),
                method=peer.get("method"),
                # Keep the original score — we are not less confident in the
                # match, just correcting the property axis based on unit.
                score=result.score,
                loinc_class=peer.get("class"),
                source=result.source,
            )

    return result


def _semantic_match(
    test_name: str,
    *,
    raw_parameter_name: Optional[str] = None,
    panel_header_raw: Optional[str] = None,
    analyte_line_raw: Optional[str] = None,
) -> Optional[MatchResult]:
    """Anchor + embedding + fuzzy + rules pipeline, unit-agnostic.
    Caller (find_loinc) applies unit-aware post-correction on the result.

    ``raw_parameter_name`` (Python-2): used in the FUZZY layer as an
        alternative source alongside ``test_name``, guarding against Gemini
        normalization drift.
    ``panel_header_raw`` / ``analyte_line_raw`` (Python-3): reserved for the
        RULES layer keyword extraction; currently unused inside this function.
    """
    # Silence unused-arg linter until Python-3 consumes these.
    _ = (panel_header_raw, analyte_line_raw)
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
                loinc_class=meta.get("class"),
                source="anchor",
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
    # Etapa Python-2: pre-normalize the raw analyte name once for reuse
    # inside the per-candidate fuzzy loop below. None if not provided.
    raw_norm = _normalize(raw_parameter_name) if raw_parameter_name else None
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

        # Etapa Python-2: raw analyte name as an ALTERNATIVE fuzzy source.
        # When Gemini's normalized English drifts semantically (e.g. emits
        # a compound noun that doesn't match the LOINC long_name well), the
        # raw name printed on the PDF often still lexically matches. We add
        # it into the max — never lowers the score, only lifts candidates
        # whose long_name/component the raw name matches better than the
        # normalized string. Safe by construction (MAX over more sources).
        if raw_norm:
            fr_long = fuzz.token_set_ratio(raw_norm, long_name.lower()) / 100.0
            fr_comp = fuzz.token_set_ratio(raw_norm, comp.lower()) / 100.0
            fz = max(fz, fr_long, fr_comp)

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
                loinc_class=meta.get("class"),
            ),
        ))

    candidates.sort(key=lambda x: x[0], reverse=True)
    return candidates[0][1] if candidates else None
