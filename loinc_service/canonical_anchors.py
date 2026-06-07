"""
canonical_anchors.py
--------------------
HARD-ACCEPT layer placed BEFORE the semantic+fuzzy matcher.

Motivation
~~~~~~~~~~
The 384-dim MiniLM embedding model used for semantic matching cannot reliably
distinguish between extremely close LOINC neighbours that share most of their
vocabulary (e.g. "Erythrocyte mean corpuscular volume" vs "Erythrocyte
distribution width", "Hemoglobin in Reticulocytes" vs "Erythrocyte mean
corpuscular hemoglobin", "White blood cells" vs "Other cells"). On long lab
reports this produced systematic mismaps for some of the most common
parameters (MCV, MCH, MCHC, RDW, WBC, ...).

Strategy
~~~~~~~~
We curate a small, AUDITABLE dictionary of canonical-English-term -> LOINC
code pairs for the parameters most frequently extracted from Romanian lab
reports. The matcher checks this map FIRST; if the normalized Gemini-emitted
term hits an anchor we short-circuit the pipeline and return the hard-coded
LOINC with `score=1.0` and `confidence="exact"`. Otherwise we fall through to
the regular semantic + fuzzy + rules engine.

Keys are NORMALIZED with `pipeline._normalize` (lowercase + collapsed
whitespace) so equivalent spellings hit the same entry. To inspect the
dictionary at runtime call the FastAPI endpoint `GET /loinc/anchors`.

Codes here are checked against the LOINC table at runtime (see
`STORE.code_index`). An anchor whose code is NOT present in the loaded
LoincDictionary is silently ignored (anchors_unresolved counter is bumped)
so a partial DB seed never breaks the pipeline.
"""

from __future__ import annotations

import re
from typing import Optional

# Raw anchor table — written in CANONICAL human-readable English. The lookup
# helper normalizes both sides (lowercase + collapsed whitespace) so manual
# entries stay readable while still matching robustly.
#
# RULE: every code listed here MUST be the OFFICIAL LOINC code for the term
# on the left (verifiable at https://loinc.org). When in doubt, leave the
# entry out and let the semantic matcher handle it.
_RAW_ANCHORS: dict[str, str] = {
    # ----- CBC / Hematology (core) -----
    "Erythrocyte mean corpuscular volume [Entitic volume] by Automated count": "787-2",
    "Erythrocyte mean corpuscular hemoglobin [Entitic mass] by Automated count": "785-6",
    "Erythrocyte mean corpuscular hemoglobin concentration [Mass/volume] by Automated count": "786-4",
    "Erythrocyte distribution width [Ratio] by Automated count": "788-0",
    "Hemoglobin [Mass/volume] in Blood": "718-7",
    "Hematocrit [Volume Fraction] of Blood": "4544-3",
    "Erythrocytes [#/volume] in Blood": "789-8",
    "Erythrocytes [#/volume] in Blood by Automated count": "789-8",
    "Leukocytes [#/volume] in Blood": "6690-2",
    "Leukocytes [#/volume] in Blood by Automated count": "6690-2",
    "White blood cells [#/volume] in Blood": "6690-2",
    "Platelets [#/volume] in Blood by Automated count": "777-3",
    "Platelets [#/volume] in Blood": "777-3",
    "Platelet mean volume [Entitic volume] in Blood by Automated count": "32623-1",
    "Platelet distribution width [Ratio] in Blood": "32207-3",
    "Plateletcrit [Volume Fraction] in Blood": "51637-7",

    # ----- WBC differential — ABSOLUTE counts -----
    "Neutrophils [#/volume] in Blood": "751-8",
    "Lymphocytes [#/volume] in Blood": "731-0",
    "Monocytes [#/volume] in Blood": "742-7",
    "Eosinophils [#/volume] in Blood": "711-2",
    "Basophils [#/volume] in Blood": "704-7",

    # ----- WBC differential — PERCENT (fraction over 100 leukocytes) -----
    "Neutrophils/100 leukocytes in Blood": "770-8",
    "Lymphocytes/100 leukocytes in Blood": "736-9",
    "Monocytes/100 leukocytes in Blood": "5905-5",
    "Eosinophils/100 leukocytes in Blood": "713-8",
    "Basophils/100 leukocytes in Blood": "706-2",

    # ----- ESR -----
    "Erythrocyte sedimentation rate": "30341-2",
    "Erythrocyte sedimentation rate by Westergren method": "4537-7",

    # ----- Coagulation -----
    "Prothrombin time (PT)": "5902-2",
    # Variants Gemini emits for the Romanian "Timp" / "Timp de protrombina QUICK".
    # Without these anchors the matcher sometimes picks 42638-7 ("PT Reference
    # value") which is just the lab-normal range, NOT the measured patient
    # value — they then appear as TWO separate rows in Compare even though
    # they are the SAME analyte from two interpretations.
    "Prothrombin time": "5902-2",
    "Prothrombin time [Time]": "5902-2",
    "Prothrombin time [Time] in Platelet poor plasma by Coagulation assay": "5902-2",
    "Prothrombin time in Platelet poor plasma by Coagulation assay": "5902-2",
    # "Timp de protrombina QUICK" — Romanian labs sometimes ship this with the
    # method suffix already in English; force the official PT code regardless
    # of the suffix variant the model emits.
    "Prothrombin time (PT) in Platelet poor plasma by Coagulation assay": "5902-2",
    "Prothrombin time (PT) in Plasma by Coagulation assay": "5902-2",
    "Prothrombin time (PT) actual/normal": "5894-1",
    "INR in Platelet poor plasma by Coagulation assay": "6301-6",

    # ----- Lipid panel -----
    "Cholesterol [Mass/volume] in Serum or Plasma": "2093-3",
    "Cholesterol in HDL [Mass/volume] in Serum or Plasma": "2085-9",
    "Cholesterol in LDL [Mass/volume] in Serum or Plasma": "2089-1",
    "Cholesterol non HDL [Mass/volume] in Serum or Plasma": "43396-1",
    "Cholesterol in VLDL [Mass/volume] in Serum or Plasma by calculation": "13458-5",
    # Romanian labs always REPORT VLDL as the calculated value (Friedewald
    # formula or similar). Some Gemini emissions drop the "by calculation"
    # suffix, which would otherwise be matched to LOINC 2091-7 (a direct VLDL
    # measurement — a different lab method). Force both variants to the
    # calculated code so the Compare view groups them on a single row.
    "Cholesterol in VLDL [Mass/volume] in Serum or Plasma": "13458-5",
    "Triglyceride [Mass/volume] in Serum or Plasma": "2571-8",
    # Plural form — Gemini occasionally emits "Triglycerides" instead of the
    # singular canonical form. Map both to the same official code so the
    # parameter shows "verificat" regardless of the spelling choice.
    "Triglycerides [Mass/volume] in Serum or Plasma": "2571-8",
    # Total lipids panel — Romanian labs commonly include a "Lipide totale"
    # line. LOINC 2569-2 = "Lipids [Mass/volume] in Serum".
    "Lipids [Mass/volume] in Serum": "2569-2",
    "Lipids [Mass/volume] in Serum or Plasma": "2569-2",
    "Total lipids [Mass/volume] in Serum": "2569-2",
    "Total lipids [Mass/volume] in Serum or Plasma": "2569-2",
    # Gemini occasionally inverts the modifier: "Lipids total" instead of
    # the canonical "Total lipids". Captured here so the badge stays green.
    "Lipids total [Mass/volume] in Serum": "2569-2",
    "Lipids total [Mass/volume] in Serum or Plasma": "2569-2",

    # ----- Liver enzymes -----
    "Alanine aminotransferase [Enzymatic activity/volume] in Serum or Plasma": "1742-6",
    "Aspartate aminotransferase [Enzymatic activity/volume] in Serum or Plasma": "1920-8",
    "Gamma glutamyl transferase [Enzymatic activity/volume] in Serum or Plasma": "2324-2",
    "Alkaline phosphatase [Enzymatic activity/volume] in Serum or Plasma": "6768-6",
    "Bilirubin.total [Mass/volume] in Serum or Plasma": "1975-2",
    "Bilirubin.direct [Mass/volume] in Serum or Plasma": "1968-7",
    "Bilirubin.indirect [Mass/volume] in Serum or Plasma": "1971-1",

    # ----- Renal panel -----
    "Creatinine [Mass/volume] in Serum or Plasma": "2160-0",
    "Urea [Mass/volume] in Serum or Plasma": "22664-7",
    "Urea nitrogen [Mass/volume] in Serum or Plasma": "3094-0",
    "Urate [Mass/volume] in Serum or Plasma": "3084-1",

    # ----- Glucose / diabetes -----
    "Glucose [Mass/volume] in Serum or Plasma": "2345-7",
    "Hemoglobin A1c/Hemoglobin.total in Blood": "4548-4",
    # Insulin — multiple LOINC codes exist for different units; we cover the
    # two most common (Units/volume and Mass/volume) and they all map to the
    # standard fasting insulin code.
    "Insulin [Units/volume] in Serum or Plasma": "20448-7",
    "Insulin [Mass/volume] in Serum or Plasma": "1558-6",
    "Fasting insulin [Units/volume] in Serum or Plasma": "1554-5",
    # HOMA-IR — insulin resistance score. LOINC 92845-7 is the Quest
    # CardioIQ panel calc; some labs use 92843-2 (basic HOMA). We point both
    # spellings to 92845-7 (used by most Romanian labs based on user logs).
    # Gemini occasionally drops the ".CardioIQ" suffix, expands to "by
    # calculation" or uses the Romanian/medical shortcut "HOMA-IR" — all
    # variants must hit the same code so the badge stays green.
    "Insulin resistance score in Serum by Calculated.CardioIQ": "92845-7",
    "Insulin resistance score [Score] in Serum by Calculated.CardioIQ": "92845-7",
    "Insulin resistance score in Serum or Plasma by Calculated.CardioIQ": "92845-7",
    "Insulin resistance score in Serum by Calculated": "92845-7",
    "Insulin resistance score in Serum by calculation": "92845-7",
    "Insulin resistance score [Score] in Serum by Calculated": "92845-7",
    "HOMA-IR": "92845-7",
    # Free-form descriptive variants Gemini occasionally emits when the lab
    # report uses the Romanian shortcut "Indice HOMA" instead of a fully
    # qualified LOINC-style title.
    "HOMA-IR insulin resistance index": "92845-7",
    "HOMA-IR index": "92845-7",
    "Insulin resistance index": "92845-7",
    "HOMA-IR in Serum or Plasma by Calculated": "92845-7",
    "HOMA-IR [Score] in Serum or Plasma by Calculated": "92845-7",
    "HOMA-IR in Serum by Calculated": "92845-7",
    "Homeostatic model assessment of insulin resistance": "92845-7",

    # ----- Electrolytes -----
    "Sodium [Moles/volume] in Serum or Plasma": "2951-2",
    "Potassium [Moles/volume] in Serum or Plasma": "2823-3",
    "Chloride [Moles/volume] in Serum or Plasma": "2075-0",
    "Magnesium [Mass/volume] in Serum or Plasma": "19123-9",
    "Calcium [Mass/volume] in Serum or Plasma": "17861-6",
    # Ionized calcium — DIFFERENT analyte from total calcium (only the free
    # fraction). LOINC 17863-2 is mass/volume in serum or plasma; 1995-0 is
    # the moles/volume variant; both map to the same Romanian "Calciu ionic".
    # We carry MULTIPLE spelling variants because Gemini's output drifts
    # between the official LOINC form (with dot: "Calcium.ionized") and the
    # natural-English form (with space: "Calcium ionized") and the inverted
    # form ("Ionized calcium"). All five variants must hit the same code.
    "Calcium.ionized [Mass/volume] in Serum or Plasma": "17863-2",
    "Calcium ionized [Mass/volume] in Serum or Plasma": "17863-2",
    "Calcium.ionized [Moles/volume] in Serum or Plasma": "1995-0",
    "Calcium ionized [Moles/volume] in Serum or Plasma": "1995-0",
    "Ionized calcium [Mass/volume] in Serum or Plasma": "17863-2",
    "Ionized calcium [Moles/volume] in Serum or Plasma": "1995-0",
    "Phosphate [Mass/volume] in Serum or Plasma": "2777-1",

    # ----- Serum proteins -----
    # "Proteine totale serice" — official LOINC 2885-2.
    "Protein [Mass/volume] in Serum or Plasma": "2885-2",
    "Total protein [Mass/volume] in Serum or Plasma": "2885-2",
    # Gemini sometimes emits "Protein total" (inverted modifier, mirroring
    # the "Lipids total" pattern). Same code.
    "Protein total [Mass/volume] in Serum or Plasma": "2885-2",
    "Protein total [Mass/volume] in Serum": "2885-2",
    "Total protein [Mass/volume] in Serum": "2885-2",
    "Albumin [Mass/volume] in Serum or Plasma": "1751-7",
    "Globulin [Mass/volume] in Serum or Plasma by calculation": "10834-0",

    # ----- Inflammation / acute phase -----
    "C-reactive protein [Mass/volume] in Serum or Plasma": "1988-5",
    "C reactive protein [Mass/volume] in Serum or Plasma": "1988-5",

    # ----- Thyroid -----
    "Thyrotropin [Units/volume] in Serum or Plasma": "3016-3",
    "Thyroxine free [Mass/volume] in Serum or Plasma": "3024-7",
    "Thyroxine free [Moles/volume] in Serum or Plasma": "14920-3",
    "Triiodothyronine free [Mass/volume] in Serum or Plasma": "3051-0",
    "Triiodothyronine free [Moles/volume] in Serum or Plasma": "14928-6",
    "Thyroperoxidase Ab [Units/volume] in Serum": "8099-4",
    "Thyroperoxidase Ab [Units/volume] in Serum or Plasma": "8099-4",
    "Thyroglobulin Ab [Units/volume] in Serum": "8098-6",

    # ----- Iron panel -----
    "Iron [Mass/volume] in Serum or Plasma": "2498-4",
    "Ferritin [Mass/volume] in Serum or Plasma": "2276-4",
    "Transferrin [Mass/volume] in Serum or Plasma": "3034-6",

    # ----- Vitamins -----
    "Cobalamin (Vitamin B12) [Mass/volume] in Serum or Plasma": "2132-9",
    "Folate [Mass/volume] in Serum or Plasma": "2284-8",
    "25-hydroxyvitamin D3+25-hydroxyvitamin D2 [Mass/volume] in Serum or Plasma": "62292-8",

    # ----- Tumor markers -----
    "Prostate specific Ag [Mass/volume] in Serum or Plasma": "2857-1",
    "Cancer Ag 19-9 [Units/volume] in Serum or Plasma": "24108-3",
    "Cancer Ag 125 [Units/volume] in Serum or Plasma": "10334-1",
    "Cancer Ag 15-3 [Units/volume] in Serum or Plasma": "17842-6",
    "Carcinoembryonic Ag [Mass/volume] in Serum or Plasma": "2039-6",
    "Alpha-1-Fetoprotein [Mass/volume] in Serum or Plasma": "1834-1",

    # ----- Other common enzymes -----
    "Amylase [Enzymatic activity/volume] in Serum or Plasma": "1798-8",
    "Lipase [Enzymatic activity/volume] in Serum or Plasma": "3040-3",
    "Creatine kinase [Enzymatic activity/volume] in Serum or Plasma": "2157-6",
    "Lactate dehydrogenase [Enzymatic activity/volume] in Serum or Plasma by Lactate to pyruvate reaction": "14804-9",

    # ----- eGFR (CKD-EPI is the modern adult formula) -----
    "Glomerular filtration rate/1.73 sq M.predicted [Volume Rate/Area] in Serum, Plasma or Blood by Creatinine-based formula (CKD-EPI 2021)": "98979-8",
    "Glomerular filtration rate/1.73 sq M.predicted in Serum, Plasma or Blood by Creatinine-based formula": "62238-1",

    # ----- Urine sediment (microscopy) -----
    # Erythrocytes/RBCs in urine sediment can semantically match either
    # 13945-1 (#/area, count per HPF) or 20409-9 (Presence). When the
    # reference range is "[0-5]" (a count) we want the deterministic
    # 13945-1; otherwise the semantic matcher flip-flops between the two
    # on every interpretation, producing different LOINCs in the Compare
    # PDF for the same patient. Both natural-English spellings emitted by
    # Gemini are anchored here so the result is stable.
    "Erythrocytes [#/area] in Urine sediment by Light microscopy": "13945-1",
    "Erythrocytes in Urine sediment by Light microscopy": "13945-1",
    "Red blood cells [#/area] in Urine sediment by Light microscopy": "13945-1",
}


def _normalize_key(s: str) -> str:
    """Same normalization used by pipeline._normalize — kept here as a tiny
    private copy so this module has no circular import."""
    return re.sub(r"\s+", " ", s.lower()).strip()


# Pre-normalized lookup map — built once at import time. Keys are guaranteed
# lowercase + single-spaced; values are LOINC codes (untouched).
_ANCHOR_LOOKUP: dict[str, str] = {
    _normalize_key(term): code for term, code in _RAW_ANCHORS.items()
}


def lookup_anchor(test_name: str) -> Optional[str]:
    """Return the LOINC code for ``test_name`` iff an exact (normalized)
    anchor exists. Otherwise return None and the caller falls back to the
    semantic+fuzzy matcher."""
    if not test_name:
        return None
    return _ANCHOR_LOOKUP.get(_normalize_key(test_name))


def all_anchors() -> dict[str, str]:
    """Return a COPY of the raw anchor table (canonical-name -> LOINC code)
    for the ``GET /loinc/anchors`` inspection endpoint."""
    return dict(_RAW_ANCHORS)


def anchor_count() -> int:
    return len(_RAW_ANCHORS)
