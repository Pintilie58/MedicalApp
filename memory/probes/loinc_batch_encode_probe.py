"""Verifies the batched-embedding optimization of the LOINC matcher:
identical results, measurably faster."""
import time
import sys

sys.path.insert(0, "/app/loinc_service")

from loinc_store import STORE  # noqa: E402
from pipeline import encode_queries, find_loinc  # noqa: E402

STORE.load()

NAMES = [
    "Cholesterol [Mass/volume] in Serum or Plasma",
    "Triglyceride [Mass/volume] in Serum or Plasma",
    "Hemoglobin [Mass/volume] in Blood",
    "Amylase [Enzymatic activity/volume] in Serum or Plasma",
    "Ferritin [Mass/volume] in Serum or Plasma",
    "Thyrotropin [Units/volume] in Serum or Plasma",
    "Glucose [Mass/volume] in Serum or Plasma",
    "Creatinine [Mass/volume] in Serum or Plasma",
    "Alanine aminotransferase [Enzymatic activity/volume] in Serum or Plasma",
    "INR in Platelet poor plasma by Coagulation assay",
    "Leukocytes [#/volume] in Blood",
    "Fibrinogen [Mass/volume] in Platelet poor plasma",
]

fails = 0


def check(label, ok, detail=""):
    global fails
    print(("PASS  " if ok else "FAIL  ") + label + (("  ->  " + detail) if detail else ""))
    if not ok:
        fails += 1


# --- 1. one-by-one (old behaviour)
t0 = time.perf_counter()
old_results = [find_loinc(n) for n in NAMES]
t_old = (time.perf_counter() - t0) * 1000

# --- 2. batched embedding (new behaviour)
t1 = time.perf_counter()
embs = encode_queries(NAMES)
new_results = [find_loinc(n, query_embedding=embs[i]) for i, n in enumerate(NAMES)]
t_new = (time.perf_counter() - t1) * 1000

check("batch: one embedding per query returned", len(embs) == len(NAMES), f"{len(embs)}")
check("batch: embeddings are float32 and normalized",
      all(e.dtype.name == "float32" and abs(float((e * e).sum()) - 1.0) < 1e-3 for e in embs))

same_code = all(
    (a is None and b is None) or (a is not None and b is not None and a.loinc == b.loinc)
    for a, b in zip(old_results, new_results)
)
check("results IDENTICAL to the per-name encoding (codes)", same_code,
      ",".join(f"{(a.loinc if a else 'None')}->{(b.loinc if b else 'None')}"
               for a, b in zip(old_results, new_results) if (a.loinc if a else None) != (b.loinc if b else None))
      or "all equal")

same_score = all(
    (a is None and b is None) or (a is not None and b is not None and abs((a.score or 0) - (b.score or 0)) < 1e-6)
    for a, b in zip(old_results, new_results)
)
check("scores identical too (same vectors, not an approximation)", same_score)

check("empty input is a safe no-op", encode_queries([]) == [])
check("query_embedding=None still works (VISION / fallback path)",
      find_loinc(NAMES[0], query_embedding=None) is not None)

print(f"\nper-name encoding: {t_old:.0f} ms   |   batched: {t_new:.0f} ms   "
      f"|   speed-up x{(t_old / t_new if t_new else 0):.1f} on {len(NAMES)} analytes")

print("\nALL CHECKS PASSED" if fails == 0 else f"\n{fails} CHECK(S) FAILED")
sys.exit(0 if fails == 0 else 1)
