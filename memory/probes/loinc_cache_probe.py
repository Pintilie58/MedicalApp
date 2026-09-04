"""Probe: in-process result cache of the LOINC matcher (Faza 0).

Verifies that the cache is a pure optimisation — same codes, faster — and that
it can never serve a stale answer after the dictionary is reloaded.

Run: cd /app/loinc_service && python3 /app/memory/probes/loinc_cache_probe.py
"""
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path("/app/loinc_service")))

from fastapi.testclient import TestClient  # noqa: E402

import pipeline  # noqa: E402
from loinc_store import STORE  # noqa: E402

fails = 0


def check(label, ok, detail=""):
    global fails
    print(("PASS  " if ok else "FAIL  ") + label + (f"  ->  {detail}" if detail else ""))
    if not ok:
        fails += 1


STORE.load()
pipeline.get_model().encode(["warmup"])

Q = "Glucose [Mass/volume] in Serum or Plasma"

# ---------------------------------------------------------------- 1. same answer, cached
pipeline.cache_clear()
first = pipeline.find_loinc(Q)
stats_after_first = pipeline.cache_stats()
second = pipeline.find_loinc(Q)
stats_after_second = pipeline.cache_stats()

check("1. first call is a miss and returns a code",
      first is not None and stats_after_first["size"] == 1,
      f"{first.loinc if first else 'None'} size={stats_after_first['size']}")
check("1b. second call is served from cache",
      stats_after_second["hits"] > stats_after_first["hits"],
      f"hits {stats_after_first['hits']} -> {stats_after_second['hits']}")
check("1c. cached answer is identical",
      second is not None and first is not None and second.loinc == first.loinc
      and abs(second.score - first.score) < 1e-12,
      f"{first.loinc} vs {second.loinc}")

# ---------------------------------------------------------------- 2. cache is not blind
check("2. different unit => different cache entry",
      pipeline.cache_key(Q, "mg/dL") != pipeline.cache_key(Q, "pmol/L"))
check("2b. different panel header => different cache entry",
      pipeline.cache_key(Q, None, None, "Hemoleucograma", None)
      != pipeline.cache_key(Q, None, None, "Biochimie", None))
check("2c. different raw name => different cache entry",
      pipeline.cache_key(Q, None, "Glicemie") != pipeline.cache_key(Q, None, "Leucocite"))
check("2d. only whitespace/case differs => same cache entry",
      pipeline.cache_key("  GLUCOSE  [Mass/volume]   in Serum or Plasma ")
      == pipeline.cache_key(Q))

# A name that differs only in case/space must give the same code with AND
# without the cache (proof the normalisation in the key is not too aggressive).
pipeline.cache_clear()
no_cache = pipeline.find_loinc("  GLUCOSE  [Mass/volume]   in Serum or Plasma ")
check("2e. case/space variant resolves to the same code computed cold",
      no_cache is not None and first is not None and no_cache.loinc == first.loinc,
      f"{no_cache.loinc if no_cache else 'None'} vs {first.loinc if first else 'None'}")

# ---------------------------------------------------------------- 3. no stale answers
pipeline.find_loinc(Q)
check("3. cache is populated before reload", pipeline.cache_stats()["size"] > 0)
STORE.load()
check("3b. reloading the dictionary empties the cache",
      pipeline.cache_stats()["size"] == 0, str(pipeline.cache_stats()))

# ---------------------------------------------------------------- 4. batch endpoint
from main import app  # noqa: E402  (imported after STORE.load to skip a second load)

with TestClient(app) as client:
    payload = [
        {"test_name": Q, "unit": "mg/dL"},
        {"test_name": "Hemoglobin [Mass/volume] in Blood", "unit": "g/dL"},
        {"test_name": "Cholesterol [Mass/volume] in Serum or Plasma", "unit": "mg/dL"},
        {"test_name": Q, "unit": "mg/dL"},  # deliberate repeat inside one report
        {"test_name": "zzzz nonexistent analyte zzzz"},
    ]

    pipeline.cache_clear()
    t0 = time.perf_counter()
    r1 = client.post("/loinc/match-batch", json=payload)
    t_cold = time.perf_counter() - t0

    t0 = time.perf_counter()
    r2 = client.post("/loinc/match-batch", json=payload)
    t_warm = time.perf_counter() - t0

    check("4. batch returns 200 both times", r1.status_code == 200 and r2.status_code == 200)
    a, b = r1.json(), r2.json()
    check("4b. result list stays positionally aligned", len(a) == len(payload) == len(b))
    check("4c. warm run returns EXACTLY the same codes",
          [x["loinc"] if x else None for x in a] == [x["loinc"] if x else None for x in b],
          str([x["loinc"] if x else None for x in a]))
    check("4d. repeated analyte inside the same report gives the same code",
          a[0] and a[3] and a[0]["loinc"] == a[3]["loinc"])
    check("4e. warm run is faster than the cold one",
          t_warm < t_cold, f"cold {t_cold*1000:.0f} ms vs warm {t_warm*1000:.0f} ms")
    check("4f. the repeated analyte was computed only once (4 distinct of 5)",
          pipeline.cache_stats()["size"] == 4, str(pipeline.cache_stats()))

    ready = client.get("/ready")
    check("5. /ready is 200 and reports the cache",
          ready.status_code == 200 and "cache" in ready.json(), ready.text[:120])
    info = client.get("/loinc/cache")
    check("5b. /loinc/cache exposes hit rate",
          info.status_code == 200 and "hit_rate_pct" in info.json(), info.text[:120])
    health = client.get("/health")
    check("5c. /health still 200", health.status_code == 200)

print("\nALL CHECKS PASSED" if fails == 0 else f"\n{fails} CHECK(S) FAILED")
sys.exit(0 if fails == 0 else 1)
