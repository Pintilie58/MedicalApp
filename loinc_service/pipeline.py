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
import unicodedata
from dataclasses import dataclass
from typing import List, Optional

import numpy as np
from rapidfuzz import fuzz
from sentence_transformers import SentenceTransformer

from config import (
    AXIS_WEIGHT,
    EMBEDDING_MODEL_NAME,
    FUZZY_WEIGHT,
    RULES_WEIGHT,
    SEM_WEIGHT,
    TOP_K,
)
from canonical_anchors import all_anchors, lookup_anchor, lookup_anchor_stripped
from loinc_store import STORE, parse_loinc_axes

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
    # Etapa "Verdict pe axe": human-readable per-axis breakdown of WHY this
    # code was chosen (component/property/system/method comparisons + decision
    # path). Flows through C# into the debug JSON e-mail attachment.
    axis_verdict: Optional[dict] = None

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
            "axis_verdict": self.axis_verdict,
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
    # -- Legacy (pre-Etapa Python-3) --
    "test strip":  {"test strip", "dipstick"},
    "dipstick":    {"test strip", "dipstick"},
    "westergren":  {"westergren"},
    "microscopy":  {"microscopy", "manual"},
    "calculation": {"calculation", "calculated"},
    "direct":      {"direct"},
    "ifcc":        {"ifcc"},

    # -- Etapa Python-3: multi-language method markers --------------------
    # Activated when the CONTEXT text (test_name + panel_header_raw +
    # analyte_line_raw + raw_parameter_name, diacritics-stripped) contains
    # the trigger phrase. Each entry pushes LOINC candidates whose ``method``
    # or ``name`` field contains any of the allowed English tokens up in the
    # ranking. Keys are stored in the ASCII form to match the diacritics-
    # stripped context (see _strip_diacritics). Only method markers with
    # well-established LOINC axis meaning are included — spectrophotometry
    # is intentionally excluded because it covers dozens of unrelated LOINC
    # axes and would trigger false positives.

    # --- Automated hematology: impedance + flow cytometry → "Automated count"
    "automated":        {"automated"},
    "automated count":  {"automated"},
    "impedance":        {"automated"},
    "impedanta":        {"automated"},           # RO ("impedanță")
    "impedancia":       {"automated"},           # ES / PT
    "impedanz":         {"automated"},           # DE
    "flow cytometry":   {"flow cytometry", "automated"},
    "cytometry":        {"flow cytometry", "automated"},
    "citometrie":       {"flow cytometry", "automated"},  # RO
    "citometria":       {"flow cytometry", "automated"},  # ES / PT / IT
    "cytometrie en flux": {"flow cytometry", "automated"},  # FR (diacritics stripped)
    "durchflusszytometrie": {"flow cytometry", "automated"},  # DE

    # --- Manual hematology: optical microscopy → "Manual count" / "Microscopy"
    "manual count":     {"manual", "microscopy"},
    "microscopie":      {"microscopy", "manual"},  # RO / FR
    "microscopia":      {"microscopy", "manual"},  # ES / PT / IT
    "mikroskopie":      {"microscopy", "manual"},  # DE
    "mikroskopia":      {"microscopy", "manual"},  # PL

    # --- Turbidimetry (CRP, immunoglobulins, ferritin)
    "turbidimetry":     {"turbidimetric", "turbidimetry"},
    "turbidimetric":    {"turbidimetric", "turbidimetry"},
    "turbidimetrie":    {"turbidimetric", "turbidimetry"},  # RO / FR / DE
    "turbidimetria":    {"turbidimetric", "turbidimetry"},  # ES / PT / IT

    # --- Nephelometry
    "nephelometry":     {"nephelometric", "nephelometry"},
    "nephelometric":    {"nephelometric", "nephelometry"},
    "nefelometrie":     {"nephelometric", "nephelometry"},  # RO / FR
    "nefelometria":     {"nephelometric", "nephelometry"},  # ES / PT / IT

    # --- ELISA / EIA (enzyme immunoassay)
    "elisa":            {"elisa", "immunoassay", "eia"},
    "eia":              {"eia", "immunoassay"},

    # --- ECLIA / chemiluminescence family (thyroid, tumor markers, hormones)
    "eclia":                    {"eclia", "chemiluminescence", "immunoassay"},
    "electrochemiluminescence": {"eclia", "chemiluminescence", "immunoassay"},
    "electrochemiluminescenta": {"eclia", "chemiluminescence", "immunoassay"},  # RO
    "chemiluminescence":        {"chemiluminescence", "immunoassay", "icma", "cmia"},
    "chemiluminescenta":        {"chemiluminescence", "immunoassay", "icma", "cmia"},  # RO
    "chemiluminiscenta":        {"chemiluminescence", "immunoassay", "icma", "cmia"},  # RO alt spelling
    "chimiluminescence":        {"chemiluminescence", "immunoassay"},  # FR variant
    "icma":                     {"chemiluminescence", "immunoassay", "icma"},
    "cmia":                     {"chemiluminescence", "immunoassay", "cmia"},

    # --- HPLC (chromatography)
    "hplc":             {"hplc", "high performance liquid chromatography", "chromatography"},

    # --- Coagulometric (fibrinogen, clotting factors)
    "coagulometric":    {"coagulometric", "clot", "clauss", "coagulation"},
    "coagulometrie":    {"coagulometric", "clot", "clauss", "coagulation"},  # RO / FR
    "coagulometria":    {"coagulometric", "clot", "clauss", "coagulation"},  # ES / PT / IT
    "clauss":           {"clauss", "coagulometric"},
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


def _strip_diacritics(s: str) -> str:
    """Strip Unicode combining marks (diacritics) from ``s``.

    Used EXCLUSIVELY when building the rules-layer context text so that the
    hand-curated keyword dictionaries (which store the ASCII form:
    ``impedanta``, ``cytometrie``, ``serique``) substring-match input written
    with native orthography in any of the ~30 supported languages:

        ``impedanță`` (RO)   → ``impedanta``
        ``cytométrie`` (FR)  → ``cytometrie``
        ``sérique`` (FR)     → ``serique``
        ``turbidimétrie``    → ``turbidimetrie``
        ``nefelometría`` (ES)→ ``nefelometria``

    NOT applied to:
      * anchor lookup keys (all English canonical strings, ASCII)
      * LOINC dictionary metadata (LOINC ships English text)
      * the semantic embedding input (SentenceTransformer handles Unicode)
      * the fuzzy layer (rapidfuzz's token_set_ratio is robust enough)
    """
    s = unicodedata.normalize("NFD", s)
    return "".join(c for c in s if unicodedata.category(c) != "Mn")


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


# -------------------------------------------------------------------------
# Deterministic-layer guards (Fix 2 + Fix 5).
# -------------------------------------------------------------------------
# LOINC property values that denote a RATIO/FRACTION — physically
# incompatible with a concentration unit like g/dL or mmol/L. Covers both
# LOINC's short forms (VFr, MFr, NFr…) and the long forms.
_RATIO_LIKE_PROPERTY_TOKENS = {
    "ratio", "relrto", "vfr", "nfr", "mfr", "sfr", "cfr",
    "volume fraction", "mass fraction", "number fraction",
    "substance fraction", "catalytic fraction",
}


def _unit_contradicts_property(
    unit: Optional[str], prop: Optional[str], name: Optional[str] = None
) -> bool:
    """True when the reported unit proves a CONCENTRATION (Mass/volume or
    Moles/volume) but the candidate LOINC is a RATIO/FRACTION — a physical
    impossibility (a g/dL value can never be a Hct/Hgb ratio). Mass↔Moles
    mismatches are NOT flagged here; those are legitimately auto-corrected
    by the peer-swap logic in ``find_loinc``."""
    if _infer_property_from_unit(unit) is None:
        return False
    p = (prop or "").strip().lower()
    if p in _RATIO_LIKE_PROPERTY_TOKENS:
        return True
    return "[ratio]" in (name or "").lower()


# Cache of (loinc_code, component_lowercase) for every RESOLVED anchor code.
# Built lazily on first use (STORE must be loaded). Used by the raw-name
# anti-hallucination guard to detect that the PDF's own analyte name matches
# a DIFFERENT well-known analyte than the one Gemini normalized to.
_ANCHOR_COMPONENTS_CACHE: Optional[list] = None


def _anchor_components() -> list:
    global _ANCHOR_COMPONENTS_CACHE
    if _ANCHOR_COMPONENTS_CACHE is None:
        pairs = []
        seen: set[str] = set()
        for _term, code in all_anchors().items():
            if code in seen:
                continue
            seen.add(code)
            meta = STORE.get_by_code(code)
            if meta is None:
                continue
            comp = (meta.get("component") or "").strip().lower()
            if comp:
                pairs.append((code, comp))
        _ANCHOR_COMPONENTS_CACHE = pairs
    return _ANCHOR_COMPONENTS_CACHE


def _raw_name_contradicts(raw_norm: Optional[str], meta: dict) -> bool:
    """Anti-hallucination guard for the deterministic layers.

    Fires ONLY on strong POSITIVE evidence that the raw PDF analyte name
    belongs to a DIFFERENT analyte than the chosen code: the raw name must
    (a) match the chosen component poorly (<0.70), AND (b) match some OTHER
    anchored component almost perfectly (>=0.85), AND (c) with a clear gap
    (>=0.20). Absence of similarity alone (e.g. Romanian "VSH" vs English
    "Erythrocyte sedimentation rate") NEVER fires the guard — otherwise
    every legitimately-translated raw name would be rejected."""
    if not raw_norm:
        return False
    comp = (meta.get("component") or meta.get("name") or "").strip().lower()
    chosen_sim = fuzz.token_set_ratio(raw_norm, comp) / 100.0
    if chosen_sim >= 0.70:
        return False
    best_other, best_comp = 0.0, None
    for code, other_comp in _anchor_components():
        if code == meta.get("loinc") or other_comp == comp:
            continue
        s = fuzz.token_set_ratio(raw_norm, other_comp) / 100.0
        if s > best_other:
            best_other, best_comp = s, other_comp
    if best_other >= 0.85 and (best_other - chosen_sim) >= 0.20:
        log.warning(
            "GUARD raw name %r contradicts deterministic pick %s (component=%r "
            "sim=%.2f) — raw name is much closer to %r (sim=%.2f). "
            "Falling back to the semantic pipeline.",
            raw_norm, meta.get("loinc"), comp, chosen_sim, best_comp, best_other,
        )
        return True
    return False


def _method_contradicts(source_context_norm: Optional[str], meta: dict, quiet: bool = False) -> bool:
    """True when the PDF's own words fire method keywords (impedanta,
    citometrie…) that the candidate's EXPLICIT method contradicts. Candidates
    with NO method are never contradicted (LOINC often has only a methodless
    code for an analyte — e.g. Hemoglobin 718-7)."""
    if not source_context_norm:
        return False
    meth_val = (meta.get("method") or "").strip().lower()
    if not meth_val:
        return False
    name_val = (meta.get("name") or "").lower()
    fired = [kw for kw in _METHOD_KEYWORDS if kw in source_context_norm]
    if not fired:
        return False
    for kw in fired:
        if any(a in meth_val or a in name_val for a in _METHOD_KEYWORDS[kw]):
            return False
    if not quiet:
        log.warning(
            "GUARD source context fired method keywords %r but candidate %s has "
            "contradicting method %r. Rejecting deterministic pick.",
            fired, meta.get("loinc"), meta.get("method"),
        )
    return True


# -------------------------------------------------------------------------
# Etapa 2 (RELMA): axis-by-axis matching.
# The Gemini emission is parsed into the LOINC axes (component / property /
# system / method) with the SAME grammar parser used to enrich the store, and
# each axis is compared against the candidate's axis independently. Cosmetic
# text drift ("in/of", word order, suffixes) becomes structurally irrelevant,
# and an error on one axis (wrong component) can no longer be compensated by
# similarity on another (same method suffix).
# -------------------------------------------------------------------------

# Property text/short-form -> family token. Unknown values stay neutral.
_AXIS_PROP_FAMILY = {
    "mcnc": "mass_conc", "mass/volume": "mass_conc", "mass concentration": "mass_conc",
    "scnc": "subst_conc", "moles/volume": "subst_conc", "substance concentration": "subst_conc",
    "ccnc": "cat_conc", "enzymatic activity/volume": "cat_conc", "catalytic concentration": "cat_conc",
    "acnc": "arb_conc", "units/volume": "arb_conc", "arbitrary concentration": "arb_conc",
    "ncnc": "num_conc", "#/volume": "num_conc", "number concentration": "num_conc",
    "naric": "num_area", "#/area": "num_area",
    "vfr": "vol_fr", "volume fraction": "vol_fr",
    "mfr": "mass_fr", "mass fraction": "mass_fr", "pure mass fraction": "mass_fr",
    "nfr": "num_fr", "number fraction": "num_fr",
    "entvol": "ent_vol", "entitic volume": "ent_vol", "entitic mean volume": "ent_vol",
    "entmass": "ent_mass", "entitic mass": "ent_mass",
    "ratio": "ratio", "relrto": "ratio",
    "reltime": "rel_time",
    "prthr": "presence", "presence": "presence", "ord": "presence",
    "rate": "rate", "vrat": "vol_rate", "volume rate/area": "vol_rate",
    "logcnc": "log_conc",
    "mrto": "mass_ratio", "mass ratio": "mass_ratio",
    "titr": "titer", "titer": "titer",
}

# Specimen text/short-form -> canonical token + coarse group for partial credit.
_AXIS_SYSTEM_CANON = {
    "bld": ("blood", "blood"), "blood": ("blood", "blood"), "whole blood": ("blood", "blood"),
    "bld.a": ("arterial_blood", "blood"), "arterial blood": ("arterial_blood", "blood"),
    "bld.v": ("venous_blood", "blood"), "venous blood": ("venous_blood", "blood"),
    "ser/plas": ("ser_plas", "ser"), "serum or plasma": ("ser_plas", "ser"),
    "ser": ("serum", "ser"), "serum": ("serum", "ser"),
    "plas": ("plasma", "ser"), "plasma": ("plasma", "ser"),
    "ppp": ("ppp", "ser"), "platelet poor plasma": ("ppp", "ser"),
    "ser/plas/bld": ("ser_plas_bld", "ser"), "serum, plasma or blood": ("ser_plas_bld", "ser"),
    "urine": ("urine", "urine"),
    "urine sed": ("urine_sed", "urine"), "urine sediment": ("urine_sed", "urine"),
    "rbc": ("rbc", "blood"), "red blood cells": ("rbc", "blood"),
}

# Method equivalence groups (Gemini wording vs LOINC MethodTyp wording).
_AXIS_METHOD_GROUPS = (
    {"automated", "automated count", "impedance", "flow cytometry"},
    {"test strip", "strip", "dipstick"},
    {"coagulation assay", "coagulation", "coagulometric", "coagulometry", "clauss"},
    {"immunoassay", "ia", "chemiluminescence", "cmia", "icma", "eclia", "clia", "immune"},
    {"microscopy", "light microscopy", "manual", "manual count", "microscopy high power field"},
    {"calculation", "calculated"},
    {"estimated"},
    {"hplc", "chromatography"},
    {"electrophoresis"},
)


def _axis_component_sim(q_comp: Optional[str], meta: dict) -> float:
    if not q_comp:
        return 0.5
    qn = q_comp.replace(".", " ").lower().strip()
    best = 0.0
    for cand in (meta.get("component"), meta.get("shortname")):
        if cand:
            best = max(best, fuzz.token_set_ratio(qn, cand.replace(".", " ").lower()) / 100.0)
    return best if best > 0 else 0.5


def _axis_property_sim(q_prop: Optional[str], c_prop: Optional[str]) -> float:
    if not q_prop or not c_prop:
        return 0.5
    qf = _AXIS_PROP_FAMILY.get(q_prop.strip().lower())
    cf = _AXIS_PROP_FAMILY.get(c_prop.strip().lower())
    if qf is None or cf is None:
        return 1.0 if q_prop.strip().lower() == c_prop.strip().lower() else 0.5
    return 1.0 if qf == cf else 0.0


def _axis_system_sim(q_sys: Optional[str], c_sys: Optional[str]) -> float:
    if not q_sys or not c_sys:
        return 0.5
    q = _AXIS_SYSTEM_CANON.get(q_sys.strip().lower())
    c = _AXIS_SYSTEM_CANON.get(c_sys.strip().lower())
    if q is None or c is None:
        return 1.0 if q_sys.strip().lower() == c_sys.strip().lower() else 0.5
    if q[0] == c[0]:
        return 1.0
    if q[1] == c[1]:
        return 0.75      # same coarse group (e.g. Serum vs Serum or Plasma)
    return 0.0


def _axis_method_sim(q_meth: Optional[str], c_meth: Optional[str]) -> float:
    q = (q_meth or "").strip().lower()
    c = (c_meth or "").strip().lower()
    if not q and not c:
        return 1.0
    if not q or not c:
        return 0.5       # absence is not a contradiction (LOINC-methodless codes)
    if q in c or c in q:
        return 1.0
    if fuzz.token_set_ratio(q, c) >= 60:
        return 1.0
    for group in _AXIS_METHOD_GROUPS:
        if any(g in q for g in group) and any(g in c for g in group):
            return 1.0
    return 0.0


def _axis_score(q_axes: dict, meta: dict) -> float:
    return (
        0.50 * _axis_component_sim(q_axes.get("component"), meta)
        + 0.20 * _axis_property_sim(q_axes.get("property"), meta.get("property"))
        + 0.15 * _axis_system_sim(q_axes.get("system"), meta.get("system"))
        + 0.15 * _axis_method_sim(q_axes.get("method"), meta.get("method"))
    )


def _build_axis_verdict(q_axes: Optional[dict], meta: dict, decision: str) -> dict:
    """Human-readable per-axis breakdown: 'query ↔ candidate = sim'. Strings
    only (C# deserializes it as Dictionary<string,string>)."""
    v: dict = {"decision": decision}
    if q_axes:
        def fmt(q, c, s):
            return f"{q or '—'} ↔ {c or '—'} = {s:.2f}"
        v["component"] = fmt(q_axes.get("component"), meta.get("component"),
                             _axis_component_sim(q_axes.get("component"), meta))
        v["property"] = fmt(q_axes.get("property"), meta.get("property"),
                            _axis_property_sim(q_axes.get("property"), meta.get("property")))
        v["system"] = fmt(q_axes.get("system"), meta.get("system"),
                          _axis_system_sim(q_axes.get("system"), meta.get("system")))
        v["method"] = fmt(q_axes.get("method"), meta.get("method"),
                          _axis_method_sim(q_axes.get("method"), meta.get("method")))
        v["axis_score"] = f"{_axis_score(q_axes, meta):.3f}"
    return v


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


def _apply_rules(context_norm: str, candidate: dict, *, source_context_norm: Optional[str] = None) -> float:
    """Return rules score in [0, 1] — fraction of rules satisfied.

    We only apply rules whose trigger keyword appears in the CONTEXT text
    (test_name + raw_parameter_name + panel_header_raw + analyte_line_raw,
    diacritics-stripped by the caller — see ``_semantic_match``). Candidates
    with no rule keywords in context get rules=1.0 (neutral, no penalty).

    Method-rule priority (Python-3)
    -------------------------------
    Gemini's ``parameter_normalized_en`` can drift on the LOINC METHOD axis
    (e.g. emits ``by Estimated`` for a Hematocrit measured with impedance,
    or ``by Automated count`` for a differential done with optical
    microscopy). To prevent Gemini's guess from contradicting the ground
    truth printed in the PDF, method rules are resolved with a priority:

      1. If ANY method keyword fires in ``source_context_norm``
         (panel_header + analyte_line + raw_parameter_name only —
         the PDF's own words), method rules use ONLY that source context.
         Gemini's test_name is ignored for method disambiguation.
      2. Otherwise, method rules fall back to the full ``context_norm``,
         preserving the legacy behavior for cases where the method marker
         only appears in Gemini's normalized text (e.g. lab printed just
         ``VSH`` but Gemini emitted ``... by Westergren``).

    Specimen + property rules always use the full ``context_norm`` — those
    axes are captured reliably by Gemini's normalization.
    """
    sys_val = (candidate.get("system") or "").lower()
    meth_val = (candidate.get("method") or "").lower()
    prop_val = (candidate.get("property") or "").lower()
    name_val = (candidate.get("name") or "").lower()

    checks_made = 0
    checks_passed = 0

    # SPECIMEN rules — full context (Gemini reliable for "in Serum"/"in Blood"/etc.)
    for kw, allowed in _SPECIMEN_KEYWORDS.items():
        if kw in context_norm:
            checks_made += 1
            if any(a in sys_val or a in name_val for a in allowed):
                checks_passed += 1

    # METHOD rules — source-first, full-fallback (Python-3 priority resolution)
    method_ctx = context_norm
    if source_context_norm is not None:
        if any(kw in source_context_norm for kw in _METHOD_KEYWORDS):
            method_ctx = source_context_norm
    for kw, allowed in _METHOD_KEYWORDS.items():
        if kw in method_ctx:
            checks_made += 1
            if any(a in meth_val or a in name_val for a in allowed):
                checks_passed += 1
            elif not meth_val:
                # METHODLESS candidate: not a contradiction, LOINC often has
                # only a methodless code for the analyte (e.g. Hemoglobin
                # 718-7 — there IS no "by Automated count" variant). Half
                # credit: below an explicit method match, above an explicit
                # method contradiction.
                checks_passed += 0.5

    # PROPERTY rules — full context (Gemini reliable for "[Mass/volume]", "[Volume Fraction]", etc.)
    for kw, allowed in _PROPERTY_KEYWORDS.items():
        if kw in context_norm:
            checks_made += 1
            if any(a in prop_val or a in name_val for a in allowed):
                checks_passed += 1

    if checks_made == 0:
        # No rule keywords in context — don't penalize, don't boost.
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
        unit=unit,
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
                axis_verdict={
                    **(result.axis_verdict or {}),
                    "unit_swap": (
                        f"{result.loinc} [{result.property}] → {peer['loinc']} "
                        f"[{peer.get('property')}] — unitatea '{unit}' cere {desired_property}"
                    ),
                },
            )

    return result


def _make_deterministic_result(meta: dict, test_name: str, via: str) -> MatchResult:
    q_axes = parse_loinc_axes(test_name)
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
        axis_verdict=_build_axis_verdict(
            q_axes if q_axes.get("component") else None, meta,
            decision=f"determinist: {via} (scor 1.0)"),
    )


def _deterministic_lookup(
    test_name: str,
    *,
    unit: Optional[str],
    raw_norm: Optional[str],
    source_context_norm: Optional[str],
) -> Optional[MatchResult]:
    """Three deterministic resolution layers, tried in order. Each hit is
    validated by cheap guards before being accepted; a rejected hit falls
    through to the next layer and ultimately to the semantic pipeline.

      1. EXACT anchor (legacy behavior, unchanged) + raw-name/unit guards.
      2. EXACT LOINC long-name in the loaded dictionary — Gemini emitted a
         verbatim LOINC name; trust it unless the PDF's own method keywords
         contradict the name's method axis.
      3. Suffix-stripped anchor — Gemini appended a stochastic ``by <method>``
         suffix to an otherwise canonical anchored term. Stripping can only
         land on the SAME analyte's anchor, never on a different analyte.
    """
    # ---- Layer 1: exact anchor -------------------------------------------
    anchor_code = lookup_anchor(test_name)
    if anchor_code is not None:
        meta = STORE.get_by_code(anchor_code)
        if meta is not None:
            if not _raw_name_contradicts(raw_norm, meta) and not _unit_contradicts_property(
                unit, meta.get("property"), meta.get("name")
            ):
                log.info(
                    "ANCHOR hit for %r -> %s %r (score=1.000, confidence=exact).",
                    test_name, anchor_code, meta.get("name") or "",
                )
                return _make_deterministic_result(meta, test_name, "ancoră exactă")
            log.warning(
                "ANCHOR hit for %r -> %s rejected by guards; using semantic pipeline.",
                test_name, anchor_code,
            )
        else:
            log.warning(
                "ANCHOR for %r maps to code %s but that code is missing from "
                "the loaded LoincStore (partial seed?). Falling back.",
                test_name, anchor_code,
            )

    # ---- Layer 2: exact LOINC long-name ----------------------------------
    meta = STORE.get_by_name(test_name)
    if meta is not None:
        if (not _method_contradicts(source_context_norm, meta)
                and not _raw_name_contradicts(raw_norm, meta)
                and not _unit_contradicts_property(unit, meta.get("property"), meta.get("name"))):
            log.info(
                "EXACT-NAME hit for %r -> %s (score=1.000, confidence=exact).",
                test_name, meta.get("loinc"),
            )
            return _make_deterministic_result(meta, test_name, "nume LOINC exact în dicționar")
        log.info("EXACT-NAME hit for %r rejected by guards; continuing.", test_name)

    # ---- Layer 3: method-suffix-stripped anchor --------------------------
    stripped_code = lookup_anchor_stripped(test_name)
    if stripped_code is not None:
        meta = STORE.get_by_code(stripped_code)
        if meta is not None:
            if (not _method_contradicts(source_context_norm, meta)
                    and not _raw_name_contradicts(raw_norm, meta)
                    and not _unit_contradicts_property(unit, meta.get("property"), meta.get("name"))):
                log.info(
                    "ANCHOR-STRIPPED hit for %r -> %s %r (method suffix removed; "
                    "score=1.000, confidence=exact).",
                    test_name, stripped_code, meta.get("name") or "",
                )
                return _make_deterministic_result(meta, test_name, "ancoră după tăierea sufixului de metodă")
            log.info("ANCHOR-STRIPPED hit for %r rejected by guards; continuing.", test_name)

    return None


def _semantic_match(
    test_name: str,
    *,
    unit: Optional[str] = None,
    raw_parameter_name: Optional[str] = None,
    panel_header_raw: Optional[str] = None,
    analyte_line_raw: Optional[str] = None,
) -> Optional[MatchResult]:
    """Anchor + embedding + fuzzy + rules pipeline, unit-agnostic.
    Caller (find_loinc) applies unit-aware post-correction on the result.

    ``raw_parameter_name`` (Python-2): used in the FUZZY layer as an
        alternative source alongside ``test_name``, guarding against Gemini
        normalization drift.
    ``panel_header_raw`` / ``analyte_line_raw`` (Python-3): consumed in the
        RULES layer via a unified diacritics-stripped ``context_norm`` that
        concatenates all four raw sources. Keyword rules search this richer
        text so specimen/method hints printed in the PDF (impedanță,
        citometrie in flux, microscopie optică, turbidimetrie, ECLIA…)
        can boost the LOINC candidate whose axes agree, regardless of any
        Gemini normalization drift on ``parameter_normalized_en``.
    """
    # 0. Normalize inputs + build the rules-layer context FIRST — the
    # deterministic layers below need raw_norm / source_context_norm for
    # their validation guards.
    query_norm = _normalize(test_name)
    # Etapa Python-2: pre-normalize the raw analyte name once for reuse
    # inside the per-candidate fuzzy loop below. None if not provided.
    raw_norm = _normalize(raw_parameter_name) if raw_parameter_name else None

    # Etapa Python-3: build a UNIFIED, diacritics-stripped context text used
    # ONLY by the rules layer (_apply_rules). Concatenates every raw source
    # Gemini gave us — the normalized English term, the raw analyte name in
    # the PDF's native language, the panel/section header and the per-row
    # inline metadata — so that specimen/method/property keywords printed
    # anywhere in the source PDF can boost the LOINC candidate whose axes
    # agree. Diacritics stripping lets the ASCII keyword dictionaries
    # (impedanta, cytometrie, serique, turbidimetrie…) match the native
    # orthography of ~30 supported languages.
    #
    # We build TWO variants:
    #   * ``full_context_norm``   — includes ``test_name`` (Gemini's English
    #                                normalization). Used for specimen/
    #                                property rules and as method-rule
    #                                fallback when the PDF source alone
    #                                does not carry any method marker.
    #   * ``source_context_norm`` — panel_header + analyte_line + raw name
    #                                only (the PDF's own words). Takes
    #                                priority for METHOD rules to prevent
    #                                a wrong ``by Automated`` / ``by
    #                                Estimated`` guess in Gemini's
    #                                normalization from contradicting the
    #                                lab's actual printed method.
    _context_parts = [test_name]
    _source_parts: List[str] = []
    if raw_parameter_name:
        _context_parts.append(raw_parameter_name)
        _source_parts.append(raw_parameter_name)
    if panel_header_raw:
        _context_parts.append(panel_header_raw)
        _source_parts.append(panel_header_raw)
    if analyte_line_raw:
        _context_parts.append(analyte_line_raw)
        _source_parts.append(analyte_line_raw)
    context_norm = _strip_diacritics(_normalize(" ".join(_context_parts)))
    source_context_norm = (
        _strip_diacritics(_normalize(" ".join(_source_parts))) if _source_parts else None
    )

    # 1. HARD-ACCEPT LAYERS — exact anchor / exact LOINC name / suffix-
    # stripped anchor, each validated by cheap guards. See
    # ``_deterministic_lookup`` for the full strategy.
    det = _deterministic_lookup(
        test_name,
        unit=unit,
        raw_norm=raw_norm,
        source_context_norm=source_context_norm,
    )
    if det is not None:
        return det

    model = get_model()

    # Etapa 2 (RELMA): parse the Gemini emission into LOINC axes ONCE. The
    # axis layer only engages when the emission is structurally parseable
    # (component + at least one more axis); free-text emissions fall back to
    # the legacy blend. AXIS_WEIGHT=0 disables the layer entirely.
    q_axes: Optional[dict] = None
    if AXIS_WEIGHT > 0:
        parsed = parse_loinc_axes(test_name)
        if parsed.get("component") and (parsed.get("property") or parsed.get("system")):
            q_axes = parsed

    # 2. Semantic similarity (vectorized over all LOINC rows).
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

        rl = _apply_rules(context_norm, meta, source_context_norm=source_context_norm)

        final = SEM_WEIGHT * sem + FUZZY_WEIGHT * fz + RULES_WEIGHT * rl

        # Etapa 2 (RELMA): blend in the axis-by-axis score. Axis errors on
        # component/property can no longer be compensated by surface-text
        # similarity on method/system suffixes.
        if q_axes is not None:
            final = AXIS_WEIGHT * _axis_score(q_axes, meta) + (1.0 - AXIS_WEIGHT) * final

        # Apply narrow hard-rejection penalties for known close-neighbor
        # ambiguities (e.g. MCV / MCH / MCHC vs Erythrocyte diameter).
        final *= _hard_reject_penalty(query_norm, long_name)

        # Unit guard (Fix 2): a concentration unit (g/dL, mmol/L…) can never
        # belong to a RATIO/FRACTION code — push such candidates off the top.
        if _unit_contradicts_property(unit, meta.get("property"), long_name):
            final *= 0.25

        # Method guard (semantic layer): an EXPLICIT method contradicting the
        # PDF's own method keywords halves the score, so text similarity can
        # no longer promote a wrong-method sibling (e.g. Hct "by Estimated"
        # under an impedance panel). Methodless candidates are unaffected.
        if _method_contradicts(source_context_norm, meta, quiet=True):
            final *= 0.5

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
    if not candidates:
        return None

    best = candidates[0][1]
    best_meta = {
        "component": best.component, "property": best.property,
        "system": best.system, "method": best.method,
    }
    decision = (
        f"semantic (axe active, pondere {AXIS_WEIGHT})"
        if q_axes is not None
        else "semantic (emisie neparsabilă pe axe — formula legacy)"
    )
    best.axis_verdict = _build_axis_verdict(q_axes, best_meta, decision)
    return best
