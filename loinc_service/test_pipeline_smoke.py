"""
test_pipeline_smoke.py
----------------------
Smoke + GOLDEN regression test (sandbox-only, no SQL Server needed): seeds a
tiny LOINC corpus with hand-picked codes covering the parameters the user has
been debugging, runs the matcher and asserts the right code comes back.

Two suites:
  * TESTS  — legacy smoke checks (exact canonical Gemini emissions).
  * GOLDEN — every historical mis-mapping bug, with full source context
             (unit, raw name, panel header), so any future tuning of the
             matcher is measurable instead of guessed. Run BEFORE and AFTER
             every algorithm change.

This is NOT a unit test for production — the real validation runs on the
user's Windows machine against the full 97k LOINC corpus.
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

    # --- Added for the GOLDEN suite (Hemoglobin / Hct confusion cluster) ---
    ("16931-8", "Hematocrit/Hemoglobin [Ratio] of Blood by Automated count",
                "Hematocrit/Hemoglobin", "Ratio", "Bld", "Automated count"),
    ("48703-3", "Hematocrit [Volume Fraction] of Blood by Estimated",
                "Hematocrit", "VFr", "Bld", "Estimated"),
    ("789-8",   "Erythrocytes [#/volume] in Blood by Automated count",
                "Erythrocytes", "NCnc", "Bld", "Automated count"),
    # --- FT3 mass/moles pair (unit-swap regression) ---
    ("3051-0",  "Triiodothyronine (T3) Free [Mass/volume] in Serum or Plasma",
                "Triiodothyronine.free", "MCnc", "Ser/Plas", None),
    ("14928-6", "Triiodothyronine (T3) Free [Moles/volume] in Serum or Plasma",
                "Triiodothyronine.free", "SCnc", "Ser/Plas", None),
    # --- MCV / MCH / MCHC anchors ---
    ("787-2",   "Erythrocyte mean corpuscular volume [Entitic volume] by Automated count",
                "Erythrocyte mean corpuscular volume", "EntVol", "RBC", "Automated count"),
    ("785-6",   "Erythrocyte mean corpuscular hemoglobin [Entitic mass] by Automated count",
                "Erythrocyte mean corpuscular hemoglobin", "EntMass", "RBC", "Automated count"),
    ("786-4",   "Erythrocyte mean corpuscular hemoglobin concentration [Mass/volume] by Automated count",
                "Erythrocyte mean corpuscular hemoglobin concentration", "MCnc", "RBC", "Automated count"),
    # --- Batch 2 (session 2026-06): urea / fibrinogen / PSA / MPV / HbA1c ---
    ("3091-6",  "Urea [Mass/volume] in Serum or Plasma",               "Urea",           "MCnc", "Ser/Plas", None),
    ("22664-7", "Urea [Moles/volume] in Serum or Plasma",              "Urea",           "SCnc", "Ser/Plas", None),
    ("3094-0",  "Urea nitrogen [Mass/volume] in Serum or Plasma",      "Urea nitrogen",  "MCnc", "Ser/Plas", None),
    ("3255-7",  "Fibrinogen [Mass/volume] in Platelet poor plasma by Coagulation assay",
                "Fibrinogen", "MCnc", "PPP", "Coagulation assay"),
    ("48664-7", "Fibrinogen [Mass/volume] in Platelet poor plasma by Coagulation.derived",
                "Fibrinogen", "MCnc", "PPP", "Coagulation.derived"),
    ("2857-1",  "Prostate specific Ag [Mass/volume] in Serum or Plasma",
                "Prostate specific Ag", "MCnc", "Ser/Plas", None),
    ("83112-3", "Prostate specific Ag [Mass/volume] in Serum or Plasma by Immunoassay",
                "Prostate specific Ag", "MCnc", "Ser/Plas", "Immunoassay"),
    ("28542-9", "Platelet [Entitic mean volume] in Blood",             "Platelet mean volume", "EntVol", "Bld", None),
    ("4548-4",  "Hemoglobin A1c/Hemoglobin.total in Blood",            "Hemoglobin A1c/Hemoglobin.total", "MFr", "Bld", None),
    ("71875-9", "Hemoglobin A1c/Hemoglobin.total [Pure mass fraction] in Blood",
                "Hemoglobin A1c/Hemoglobin.total", "MFr", "Bld", None),
    # --- HDL mass/moles pair (unit-swap regression, bug 2026-06) ---
    ("14646-4", "Cholesterol in HDL [Moles/volume] in Serum or Plasma",
                "Cholesterol in HDL", "SCnc", "Ser/Plas", None),
    # --- Batch 4: VSH + Procente de protrombina (numele exact din dicționarul userului) ---
    ("30341-2", "Erythrocyte [Sedimentation Rate] in Blood",           "ESR", "Rate", "Bld", None),
    ("82477-1", "Erythrocyte [Sedimentation Rate] in Blood by Photometric method",
                "ESR", "Rate", "Bld", "Photometric method"),
    ("40457-4", "Prothrombin Ab [Units/volume] in Serum or Plasma",
                "Prothrombin Ab", "ACnc", "Ser/Plas", None),
    ("77161-8", "Prothrombin activity [Units/volume] in Platelet poor plasma by Coagulation assay --immediately after addition of factor II depleted plasma",
                "Prothrombin activity", "ACnc", "PPP", "Coagulation assay"),
    ("3289-6",  "Prothrombin activity actual/normal in Platelet poor plasma by Coagulation assay",
                "Prothrombin activity actual/normal", "RelTime", "PPP", "Coagulation assay"),
    # --- Batch 5: INR (bug real: forma expandata scotea 6301-6 din top-K semantic) ---
    ("6301-6",  "INR in Platelet poor plasma by Coagulation assay",
                "INR", "RelTime", "PPP", "Coagulation assay"),
    ("3200-3",  "Coagulation factor VII activity actual/normal [Molar ratio] in Platelet poor plasma by Coagulation assay",
                "Coagulation factor VII activity actual/normal", "ArVRat", "PPP", "Coagulation assay"),
]


# Legacy smoke suite: canonical Gemini emissions -> expected LOINC.
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
    # 30341-2 = codul-umbrelă generic ESR (politica RO de consistență).
    ("Erythrocyte sedimentation rate",                                        "30341-2"),
    ("Cholesterol in HDL [Mass/volume] in Serum or Plasma",                   "2085-9"),
    ("Hematocrit [Volume Fraction] of Blood",                                 "4544-3"),
]


_PENTRA_PANEL = ("Hemoleucograma completa - Sange - Spectroscopie de impedanta, "
                 "spectrofotometrie, citometrie in flux (PENTRA ES 60)")

# GOLDEN regression suite: every historical mis-mapping, with source context.
# Fields: query, unit, raw (raw_parameter_name), panel (panel_header_raw),
#         expected (LOINC or None), expect_source ("anchor"/"semantic"/None).
GOLDEN = [
    dict(note="Hgb canonical + full context -> anchor",
         query="Hemoglobin [Mass/volume] in Blood",
         unit="g/dL", raw="Hemoglobina", panel=_PENTRA_PANEL,
         expected="718-7", expect_source="anchor"),

    dict(note="BUG 2026-06: Hgb + 'by Automated count' suffix must NOT map to Hct/Hgb Ratio 16931-8",
         query="Hemoglobin [Mass/volume] in Blood by Automated count",
         unit="g/dL", raw="Hemoglobina", panel=_PENTRA_PANEL,
         expected="718-7", expect_source="anchor"),

    dict(note="A GENUINE Hct/Hgb ratio row must still map to 16931-8 (exact-name layer)",
         query="Hematocrit/Hemoglobin [Ratio] of Blood by Automated count",
         unit=None, raw="Raport Hematocrit/Hemoglobina", panel=_PENTRA_PANEL,
         expected="16931-8", expect_source="anchor"),

    dict(note="Python-3 case: Gemini says 'by Estimated' but PDF says impedance -> Automated 4544-3",
         query="Hematocrit [Volume Fraction] of Blood by Estimated",
         unit="%", raw="Hematocrit", panel=_PENTRA_PANEL,
         expected="4544-3", expect_source="anchor"),

    dict(note="No method context: 'by Estimated' exact LOINC name is trusted -> 48703-3",
         query="Hematocrit [Volume Fraction] of Blood by Estimated",
         unit="%", raw=None, panel=None,
         expected="48703-3", expect_source="anchor"),

    dict(note="Anti-hallucination: Gemini says Hemoglobin but PDF row says Hematocrit -> anchor must NOT fire",
         query="Hemoglobin [Mass/volume] in Blood",
         unit="%", raw="Hematocrit", panel=_PENTRA_PANEL,
         expected=None, expect_source="semantic"),

    dict(note="FT3 unit swap: Mass/volume anchor + pmol/L -> Moles/volume peer 14928-6",
         query="Triiodothyronine free [Mass/volume] in Serum or Plasma",
         unit="pmol/L", raw="FT3", panel=None,
         expected="14928-6", expect_source="anchor"),

    dict(note="MCH canonical anchor",
         query="Erythrocyte mean corpuscular hemoglobin [Entitic mass] by Automated count",
         unit="pg", raw="MCH (Hemoglobina eritrocitara medie)", panel=_PENTRA_PANEL,
         expected="785-6", expect_source="anchor"),

    dict(note="MCHC canonical anchor (g/dL is MCnc, must not be unit-rejected)",
         query="Erythrocyte mean corpuscular hemoglobin concentration [Mass/volume] by Automated count",
         unit="g/dL", raw="MCHC (Concentratia medie de hemoglobina)", panel=_PENTRA_PANEL,
         expected="786-4", expect_source="anchor"),

    dict(note="LDL by calculation: exact LOINC name wins over generic 2089-1 anchor",
         query="Cholesterol in LDL [Mass/volume] in Serum or Plasma by calculation",
         unit="mg/dL", raw="LDL - colesterol", panel=None,
         expected="13457-7", expect_source="anchor"),

    dict(note="Romanian raw name 'VSH' must NOT trigger the anti-hallucination guard",
         query="Erythrocyte sedimentation rate",
         unit="mm/h", raw="VSH", panel=None,
         expected="30341-2", expect_source="anchor"),

    dict(note="Erythrocytes canonical + suffix already anchored",
         query="Erythrocytes [#/volume] in Blood by Automated count",
         unit="10^6/mm3", raw="Numar total de eritrocite", panel=_PENTRA_PANEL,
         expected="789-8", expect_source="anchor"),

    # ---- Batch 2 (real Gemini emissions captured from user's debug JSON) ----
    dict(note="BUG Hct: 'in Blood' (prepozitie) fara sufix NU mai are voie sa dea 48703-3 Estimated",
         query="Hematocrit [Volume fraction] in Blood",
         unit="%", raw="Hematocrit", panel=_PENTRA_PANEL,
         expected="4544-3", expect_source="anchor"),

    dict(note="Hct: 'in Blood by Automated count' -> strip sufix + prepozitie -> 4544-3",
         query="Hematocrit [Volume Fraction] in Blood by Automated count",
         unit="%", raw="Hematocrit", panel=_PENTRA_PANEL,
         expected="4544-3", expect_source="anchor"),

    dict(note="BUG MPV: emisia fara metoda nu mai are voie sa dea 28542-9 (methodless)",
         query="Platelet mean volume [Entitic volume] in Blood",
         unit="um^3", raw="MPV (Volum trombocitar mediu)", panel=_PENTRA_PANEL,
         expected="32623-1", expect_source="anchor"),

    dict(note="MPV: emisia fara specimen ('[Entitic volume] by Automated count') -> 32623-1",
         query="Platelet mean volume [Entitic volume] by Automated count",
         unit="um^3", raw="MPV (Volum trombocitar mediu)", panel=_PENTRA_PANEL,
         expected="32623-1", expect_source="anchor"),

    dict(note="BUG Fibrinogen: 'in Plasma' nu mai are voie sa dea 48664-7 (derived)",
         query="Fibrinogen [Mass/volume] in Plasma",
         unit="mg/dL", raw="Fibrinogenemie", panel="COAGULARE",
         line="-Plasma - Coagulometrie (BFT II)",
         expected="3255-7", expect_source="anchor"),

    dict(note="Fibrinogen: 'in Plasma by Coagulometry' -> strip -> 3255-7",
         query="Fibrinogen [Mass/volume] in Plasma by Coagulometry",
         unit="mg/dL", raw="Fibrinogenemie", panel="COAGULARE",
         line="-Plasma - Coagulometrie (BFT II)",
         expected="3255-7", expect_source="anchor"),

    dict(note="BUG PSA: 'antigen' (cuvant intreg) nu mai are voie sa dea 83112-3",
         query="Prostate specific antigen [Mass/volume] in Serum or Plasma",
         unit="ng/mL", raw="PSA", panel="IMUNOLOGIE",
         line="-Ser - chemiluminiscenta (ADVIA CENTAUR CP)",
         expected="2857-1", expect_source="anchor"),

    dict(note="PSA: varianta '.total' -> 2857-1",
         query="Prostate specific antigen.total [Mass/volume] in Serum or Plasma",
         unit="ng/mL", raw="PSA", panel="IMUNOLOGIE",
         line="-Ser - chemiluminiscenta (ADVIA CENTAUR CP)",
         expected="2857-1", expect_source="anchor"),

    dict(note="BUG HbA1c: '[Mass Fraction] in Blood' nu mai are voie sa dea 71875-9 (IFCC)",
         query="Hemoglobin A1c [Mass Fraction] in Blood",
         unit="%", raw="Hemoglobina glicata (HbA1c)", panel=None,
         line="-Sange - (BA200)",
         expected="4548-4", expect_source="anchor"),

    dict(note="HbA1c: 'A1c/Total Hemoglobin in Blood' -> 4548-4",
         query="Hemoglobin A1c/Total Hemoglobin in Blood",
         unit="%", raw="Hemoglobina glicata (HbA1c)", panel="BIOCHIMIE SERICA",
         line="-Ser - Turbidimetrie (ABX PENTRA C400 ISE)",
         expected="4548-4", expect_source="anchor"),

    dict(note="BUGFIX ancora Uree: cheia [Mass/volume] -> 3091-6 (nu 22664-7 = moles)",
         query="Urea [Mass/volume] in Serum or Plasma",
         unit="mg/dL", raw="Uree serica", panel="BIOCHIMIE SERICA",
         line="-Ser - Spectrofotometrie (ABX PENTRA C400 ISE)",
         expected="3091-6", expect_source="anchor"),

    dict(note="Urea nitrogen (emisie BUN): ramane 3094-0 (unificarea o face Etapa 3)",
         query="Urea nitrogen [Mass/volume] in Serum or Plasma",
         unit="mg/dL", raw="Uree serica", panel="BIOCHIMIE SERICA",
         expected="3094-0", expect_source="anchor"),

    # ---- Batch 3: HDL unit-swap (bug real: mmol/L primea codul de Mass 2085-9) ----
    dict(note="BUG HDL: mmol/L trebuie sa dea codul Moles 14646-4, nu 2085-9",
         query="Cholesterol HDL [Mass/volume] in Serum or Plasma",
         unit="mmol/L", raw="Colesterol HDL", panel="Profil lipidic",
         line="Ser / Metoda spectrofotometrie",
         expected="14646-4"),

    dict(note="HDL mg/dL ramane pe codul Mass 2085-9",
         query="Cholesterol HDL [Mass/volume] in Serum or Plasma by Enzymatic method",
         unit="mg/dL", raw="Colesterol HDL", panel="Biochimie | Profil lipidic",
         line="Ser/metoda enzimatica / spectrofotometrie",
         expected="2085-9"),

    # ---- Batch 4 (emisii reale: VSH + Procente de protrombina) ----
    dict(note="BUG VSH: 'in Blood' fara metoda -> umbrela generica 30341-2",
         query="Erythrocyte sedimentation rate in Blood",
         unit="mm/h", raw="VSH", panel="Hematologie",
         line="Sange EDTA / Metoda fotometrica",
         expected="30341-2", expect_source="anchor"),

    dict(note="BUG VSH: varianta 'by Microphotometric method' se unifica tot pe 30341-2",
         query="Erythrocyte sedimentation rate in Blood by Microphotometric method",
         unit="mm/h", raw="VSH", panel="VSH",
         line="Sange EDTA/metoda microfotometrica capilara",
         expected="30341-2", expect_source="anchor"),

    dict(note="VSH Westergren explicit ramane 4537-7 (politica pastrata)",
         query="Erythrocyte sedimentation rate by Westergren method",
         unit="mm/h", raw="VSH", panel=None,
         expected="4537-7", expect_source="anchor"),

    dict(note="BUG PT%: '[Units/volume] in Coagulation plasma' NU mai are voie sa dea 40457-4 (anticorp!)",
         query="Prothrombin [Units/volume] in Coagulation plasma",
         unit="%", raw="Procente de protrombina", panel="Hematologie | Timp de protrombina QUICK",
         line="Plasma citrat / metoda coagulometrica",
         expected="3289-6", expect_source="anchor"),

    dict(note="BUG PT%: '[Units/volume] in Platelet poor plasma' -> 3289-6 (nu 77161-8 factor II)",
         query="Prothrombin [Units/volume] in Platelet poor plasma",
         unit="%", raw="Procente de protrombina", panel="Timp de protrombina QUICK | Plasma citrat / Metoda: coagulometrica",
         expected="3289-6", expect_source="anchor"),

    dict(note="PT%: '[Ratio] in Coagulation plasma by Coagulation assay' -> strip -> 3289-6",
         query="Prothrombin [Ratio] in Coagulation plasma by Coagulation assay",
         unit="%", raw="Procente de protrombina", panel="Timp de protrombina QUICK | Plasma citrat / Metoda: coagulometrica",
         expected="3289-6", expect_source="anchor"),

    dict(note="Un test GENUIN de anticorpi anti-protrombina ramane pe 40457-4 (exact-name)",
         query="Prothrombin Ab [Units/volume] in Serum or Plasma",
         unit="U/mL", raw="Anticorpi anti-protrombina", panel="IMUNOLOGIE",
         expected="40457-4", expect_source="anchor"),

    dict(note="PT actual/normal (emisia canonica veche) ramane 5894-1",
         query="Prothrombin time (PT) actual/normal",
         unit="%", raw="Timp Quick", panel=None,
         expected="5894-1", expect_source="anchor"),

    # ---- Batch 5 (emisii reale: INR) ----
    dict(note="BUG INR: forma expandata 'International normalized ratio (INR)' NU mai are voie sa dea 3200-3 (factor VII)",
         query="International normalized ratio (INR) in Platelet poor plasma",
         unit=None, raw="INR", panel="Timp de protrombina QUICK | Plasma citrat / Metoda: coagulometrica",
         expected="6301-6", expect_source="anchor"),

    dict(note="INR: emisia scurta 'INR in Coagulation plasma' -> 6301-6",
         query="INR in Coagulation plasma",
         unit=None, raw="INR", panel="Hematologie | Timp de protrombina QUICK",
         line="Plasma citrat / metoda coagulometrica",
         expected="6301-6"),

    dict(note="INR: forma expandata fara paranteze, sistem generic -> 6301-6",
         query="International normalized ratio in Plasma",
         unit=None, raw="INR", panel="Timp de protrombina QUICK",
         line="Plasma citrat / metoda coagulometrica",
         expected="6301-6"),
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
    import pipeline
    from loinc_store import STORE
    from pipeline import find_loinc

    # Reset the anchor-components cache (rebuilt from the freshly loaded STORE)
    pipeline._ANCHOR_COMPONENTS_CACHE = None
    STORE.load()
    print(f"Loaded {STORE.size} entries.\n")

    failed = []

    # ---------------- Legacy smoke suite ----------------
    print("LEGACY SMOKE SUITE")
    print(f"{'INPUT':<70} {'EXPECTED':<10} {'GOT':<10} {'SCORE':<6} {'OK'}")
    print("-" * 110)
    passed = 0
    for query, expected in TESTS:
        result = find_loinc(query)
        got = result.loinc if result else "—"
        score = f"{result.score:.2f}" if result else ""
        ok = got == expected
        passed += 1 if ok else 0
        if not ok:
            failed.append((query, expected, got, score))
        print(f"{query[:68]:<70} {expected:<10} {got:<10} {score:<6} {'✅' if ok else '❌'}")
    print(f"Legacy: {passed}/{len(TESTS)} passed.\n")

    # ---------------- GOLDEN regression suite ----------------
    print("GOLDEN REGRESSION SUITE")
    print("-" * 110)
    gpassed = 0
    for case in GOLDEN:
        result = find_loinc(
            case["query"],
            unit=case.get("unit"),
            raw_parameter_name=case.get("raw"),
            panel_header_raw=case.get("panel"),
            analyte_line_raw=case.get("line"),
        )
        got = result.loinc if result else "—"
        src = result.source if result else "—"
        score = f"{result.score:.2f}" if result else ""
        ok = True
        reasons = []
        if case.get("expected") is not None and got != case["expected"]:
            ok = False
            reasons.append(f"code: expected {case['expected']} got {got}")
        if case.get("expect_source") is not None and src != case["expect_source"]:
            ok = False
            reasons.append(f"source: expected {case['expect_source']} got {src}")
        gpassed += 1 if ok else 0
        if not ok:
            failed.append((case["note"], case.get("expected"), got, score))
        print(f"{'✅' if ok else '❌'}  {case['note']}")
        print(f"      -> got {got} (source={src}, score={score})"
              + (f"   [{'; '.join(reasons)}]" if reasons else ""))
    print(f"\nGolden: {gpassed}/{len(GOLDEN)} passed.")

    total = passed + gpassed
    total_all = len(TESTS) + len(GOLDEN)
    print(f"\nTOTAL: {total}/{total_all} passed.")
    if failed:
        print("\nFailed cases:")
        for q, e, g, s in failed:
            print(f"  ❌  {q}")
            print(f"      expected={e}  got={g}  score={s}")
    return total == total_all


def strip_axes_from_metadata() -> str:
    """Simulate the user's production seed, where the SQL LoincDictionary has
    NO Component/Property/System/Method columns — every axis is None and the
    pipeline must rely on parse_loinc_axes() enrichment. Returns a JSON backup
    of the full metadata so it can be restored afterwards."""
    with open(METADATA_FILE, "r", encoding="utf-8") as f:
        meta = json.load(f)
    backup = json.dumps(meta, ensure_ascii=False)
    for m in meta:
        m["component"] = None
        m["property"] = None
        m["system"] = None
        m["method"] = None
    with open(METADATA_FILE, "w", encoding="utf-8") as f:
        json.dump(meta, f, ensure_ascii=False)
    return backup


def restore_metadata(backup: str) -> None:
    with open(METADATA_FILE, "w", encoding="utf-8") as f:
        f.write(backup)


if __name__ == "__main__":
    if "--no-seed" not in sys.argv:
        seed_sample()

    print("\n" + "=" * 80)
    print("MOD 1: seed COMPLET (cu axele Component/Property/System/Method)")
    print("=" * 80)
    ok_full = run_tests()

    print("\n" + "=" * 80)
    print("MOD 2: seed SARAC (fara axe — ca pe masina utilizatorului; enrichment din nume)")
    print("=" * 80)
    backup = strip_axes_from_metadata()
    try:
        ok_poor = run_tests()
    finally:
        restore_metadata(backup)

    print(f"\nREZULTAT FINAL: seed complet={'PASS' if ok_full else 'FAIL'}  "
          f"seed sarac={'PASS' if ok_poor else 'FAIL'}")
    sys.exit(0 if (ok_full and ok_poor) else 1)
