"""
test_pipeline_smoke.py
----------------------
Smoke test (sandbox-only, no SQL Server needed): seeds a tiny LOINC corpus
with ~25 hand-picked codes that cover the parameters the user has been
debugging, runs the matcher against the EXACT English terms the new Gemini
prompt is supposed to emit, and asserts the matcher returns the right code.

This is NOT a unit test for production — it's a sanity check that the
FastAPI + sentence-transformers + rapidfuzz + numpy + rules pipeline glues
together correctly. The real validation runs on the user's Windows machine
against the full 97k LOINC corpus.
"""

from __future__ import annotations

import json
import os
import sys
from pathlib import Path

import numpy as np

# Make sibling modules importable when running as a script.
sys.path.insert(0, str(Path(__file__).resolve().parent))

from config import EMBEDDINGS_FILE, METADATA_FILE, EMBEDDING_MODEL_NAME  # noqa
from sentence_transformers import SentenceTransformer  # noqa


# Hand-picked corpus covering the parameters the user has tested.
LOINC_SAMPLE = [
    # (loinc, name, component, property, system, method)
    ("2345-7",  "Glucose [Mass/volume] in Serum or Plasma",          "Glucose",          "MCnc", "Ser/Plas", None),
    ("5792-7",  "Glucose [Mass/volume] in Urine by Test strip",      "Glucose",          "MCnc", "Urine",    "Test strip"),
    ("2542-3",  "Glucose [Mass/volume] in Blood",                    "Glucose",          "MCnc", "Bld",      None),
    ("718-7",   "Hemoglobin [Mass/volume] in Blood",                 "Hemoglobin",       "MCnc", "Bld",      None),
    ("14804-9", "Lactate dehydrogenase [Enzymatic activity/volume] in Serum or Plasma by Lactate to pyruvate reaction",
                "Lactate dehydrogenase", "CCnc", "Ser/Plas", "Lactate to pyruvate"),
    ("62238-1", "Glomerular filtration rate/1.73 sq M.predicted [Volume Rate/Area] in Serum, Plasma or Blood by Creatinine-based formula",
                "Glomerular filtration rate", "VRat", "Ser/Plas/Bld", "Creatinine-based"),
    ("2965-2",  "Specific gravity of Urine",                          "Specific gravity", "Ratio", "Urine",  None),
    ("43396-1", "Cholesterol non HDL [Mass/volume] in Serum or Plasma",
                "Cholesterol non HDL", "MCnc", "Ser/Plas", None),
    ("13457-7", "Cholesterol in LDL [Mass/volume] in Serum or Plasma by calculation",
                "Cholesterol in LDL", "MCnc", "Ser/Plas", "Calculation"),
    ("18262-6", "Cholesterol in LDL [Mass/volume] in Serum or Plasma by Direct assay",
                "Cholesterol in LDL", "MCnc", "Ser/Plas", "Direct"),
    ("5894-1",  "Prothrombin time (PT) actual/normal in Platelet poor plasma by Coagulation assay",
                "Prothrombin time", "RelTime", "PPP",   "Coagulation"),
    ("8098-6",  "Thyroglobulin Ab [Units/volume] in Serum",            "Thyroglobulin Ab","ACnc", "Ser",     None),
    ("1992-7",  "Calcitonin [Mass/volume] in Serum or Plasma",         "Calcitonin",      "MCnc", "Ser/Plas",None),
    ("5803-2",  "pH of Urine by Test strip",                           "pH",              "LogCnc", "Urine", "Test strip"),
    ("5787-7",  "Epithelial cells [#/area] in Urine sediment by Microscopy high power field",
                "Epithelial cells", "Naric", "Urine sed", "Microscopy"),
    ("20405-7", "Urobilinogen [Mass/volume] in Urine by Test strip",   "Urobilinogen",    "MCnc", "Urine",   "Test strip"),
    ("3016-3",  "Thyrotropin [Units/volume] in Serum or Plasma",       "Thyrotropin",     "ACnc", "Ser/Plas",None),
    ("1742-6",  "Alanine aminotransferase [Enzymatic activity/volume] in Serum or Plasma",
                "Alanine aminotransferase", "CCnc", "Ser/Plas", None),
    ("1920-8",  "Aspartate aminotransferase [Enzymatic activity/volume] in Serum or Plasma",
                "Aspartate aminotransferase", "CCnc", "Ser/Plas", None),
    ("2324-2",  "Gamma glutamyl transferase [Enzymatic activity/volume] in Serum or Plasma",
                "Gamma glutamyl transferase", "CCnc", "Ser/Plas", None),
    ("4537-7",  "Erythrocyte sedimentation rate",                      "ESR",             "Rate", "Bld",     None),
    ("2085-9",  "Cholesterol in HDL [Mass/volume] in Serum or Plasma", "Cholesterol in HDL","MCnc","Ser/Plas",None),
    ("2089-1",  "Cholesterol in LDL [Mass/volume] in Serum or Plasma", "Cholesterol in LDL","MCnc","Ser/Plas",None),
    ("4544-3",  "Hematocrit [Volume Fraction] of Blood by Automated count",
                "Hematocrit", "VFr", "Bld",     "Automated count"),
    ("32623-1", "Platelet mean volume [Entitic volume] in Blood by Automated count",
                "Platelet mean volume", "EntVol", "Bld", "Automated count"),
]


# Queries the new Gemini prompt is supposed to emit (parameter_normalized_en),
# paired with the EXPECTED LOINC code.
TESTS = [
    ("Glucose [Mass/volume] in Serum or Plasma",                              "2345-7"),
    ("Glucose [Mass/volume] in Urine by Test strip",                          "5792-7"),
    ("Hemoglobin [Mass/volume] in Blood",                                     "718-7"),
    ("Lactate dehydrogenase [Enzymatic activity/volume] in Serum or Plasma",  "14804-9"),
    ("Glomerular filtration rate/1.73 sq M.predicted in Serum, Plasma or Blood by Creatinine-based formula", "62238-1"),
    ("Specific gravity of Urine",                                             "2965-2"),
    ("Cholesterol non HDL [Mass/volume] in Serum or Plasma",                  "43396-1"),
    ("Prothrombin time (PT) actual/normal",                                   "5894-1"),
    ("Thyroglobulin Ab [Units/volume] in Serum",                              "8098-6"),
    ("Calcitonin [Mass/volume] in Serum or Plasma",                           "1992-7"),
    ("pH of Urine by Test strip",                                             "5803-2"),
    ("Epithelial cells [#/area] in Urine sediment by Microscopy high power field", "5787-7"),
    ("Urobilinogen [Mass/volume] in Urine by Test strip",                     "20405-7"),
    ("Thyrotropin [Units/volume] in Serum or Plasma",                         "3016-3"),
    ("Alanine aminotransferase [Enzymatic activity/volume] in Serum or Plasma","1742-6"),
    ("Gamma glutamyl transferase [Enzymatic activity/volume] in Serum or Plasma", "2324-2"),
    ("Erythrocyte sedimentation rate",                                        "4537-7"),
    ("Cholesterol in HDL [Mass/volume] in Serum or Plasma",                   "2085-9"),
    ("Hematocrit [Volume Fraction] of Blood",                                 "4544-3"),
]


def seed_sample():
    print(f"Seeding {len(LOINC_SAMPLE)} sample LOINC entries...")
    model = SentenceTransformer(EMBEDDING_MODEL_NAME)

    metadata = []
    texts = []
    for loinc, name, comp, prop, sys_, meth in LOINC_SAMPLE:
        # Same text-builder logic as seed_embeddings.build_text
        parts = [name, comp, prop, sys_, meth]
        parts = [p for p in parts if p]
        texts.append(" | ".join(parts))
        metadata.append({
            "loinc": loinc, "name": name,
            "component": comp, "property": prop,
            "system": sys_, "method": meth, "shortname": None,
        })

    embs = model.encode(texts, normalize_embeddings=True, convert_to_numpy=True).astype(np.float32)

    EMBEDDINGS_FILE.parent.mkdir(parents=True, exist_ok=True)
    np.save(EMBEDDINGS_FILE, embs)
    with open(METADATA_FILE, "w", encoding="utf-8") as f:
        json.dump(metadata, f, ensure_ascii=False)
    print(f"Wrote {EMBEDDINGS_FILE} and {METADATA_FILE}.")


def run_tests():
    # Import AFTER seed file exists so STORE can be loaded.
    from loinc_store import STORE
    from pipeline import find_loinc

    STORE.load()
    print(f"Loaded {STORE.size} entries.\n")

    print(f"{'INPUT':<70} {'EXPECTED':<10} {'GOT':<10} {'SCORE':<6} {'OK'}")
    print("-" * 110)
    passed = 0
    failed = []
    for query, expected in TESTS:
        result = find_loinc(query)
        got = result.loinc if result else "—"
        score = f"{result.score:.2f}" if result else ""
        ok = got == expected
        passed += 1 if ok else 0
        if not ok:
            failed.append((query, expected, got, score))
        print(f"{query[:68]:<70} {expected:<10} {got:<10} {score:<6} {'✅' if ok else '❌'}")

    print(f"\nResult: {passed}/{len(TESTS)} passed.")
    if failed:
        print("\nFailed cases:")
        for q, e, g, s in failed:
            print(f"  ❌  {q}")
            print(f"      expected={e}  got={g}  score={s}")
    return passed == len(TESTS)


if __name__ == "__main__":
    if "--no-seed" not in sys.argv:
        seed_sample()
    ok = run_tests()
    sys.exit(0 if ok else 1)
