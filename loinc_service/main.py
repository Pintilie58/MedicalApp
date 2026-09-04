"""
main.py
-------
FastAPI entry point for the LOINC matcher microservice.

Endpoints
---------
GET  /health       -> liveness probe (no LOINC data needed)
GET  /ready        -> readiness probe (returns 503 until LoincStore is loaded)
POST /loinc/match  -> resolve a LOINC code for an English medical term
POST /loinc/match-batch -> resolve a whole lab report in ONE call (parallel)

Run (development):
    uvicorn main:app --host 127.0.0.1 --port 8000 --reload

Run (production-like, single worker):
    uvicorn main:app --host 0.0.0.0 --port 8000 --workers 1
"""

from __future__ import annotations

import logging
import os
import time
from concurrent.futures import ThreadPoolExecutor
from contextlib import asynccontextmanager

from fastapi import FastAPI, HTTPException, status
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field

from loinc_store import STORE
from canonical_anchors import all_anchors, anchor_count
from pipeline import cache_key, cache_lookup, cache_stats, encode_queries, find_loinc, get_model

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s [%(name)s] %(message)s",
)
log = logging.getLogger("loinc.api")


@asynccontextmanager
async def lifespan(app: FastAPI):
    log.info("Starting LOINC matcher: loading store + model...")
    try:
        STORE.load()
        # Warm the embedding model so the first request is fast.
        get_model().encode(["warmup"])
        log.info("LOINC matcher READY (entries=%d).", STORE.size)
    except Exception as ex:
        log.exception("Startup failed: %s", ex)
        # Keep the app running so /health stays green and the operator can see
        # the readiness probe fail and learn why.
    yield
    log.info("LOINC matcher shutting down.")


app = FastAPI(
    title="MedicalApp LOINC Matcher",
    description="Semantic + fuzzy + rules LOINC code resolver. "
                "Consumed by the MedicalApp ASP.NET Core app.",
    version="1.0.0",
    lifespan=lifespan,
)


# -------------------- Request / Response models --------------------
class LoincRequest(BaseModel):
    test_name: str = Field(..., min_length=1, max_length=500,
                           description="Standardized English medical term for the analyte.")
    unit: str | None = Field(
        default=None, max_length=64,
        description=(
            "Reported unit of measure (e.g. 'pmol/L', 'mg/dL', 'U/L'). When "
            "provided, the matcher swaps the chosen LOINC to the same-component "
            "Mass/volume↔Moles/volume peer if the unit indicates a different "
            "property than the best match. Fixes Gemini's systematic miscall "
            "of FT3/FT4 in pmol/L to the Mass/volume LOINC codes."))

    # ---- Etapa Python-1: source context fields (Type A/B/C PDF layouts) ----
    # Optional raw-source fields copied verbatim by Gemini from the PDF. In
    # this stage they are only ACCEPTED and LOGGED — the matching pipeline
    # still runs on ``test_name`` alone. Etapa Python-2/3 will consume them
    # inside the fuzzy + rules layers to disambiguate LOINC axes (Method,
    # Specimen) when Gemini's normalization is ambiguous or wrong.
    raw_parameter_name: str | None = Field(
        default=None, max_length=500,
        description=(
            "Original raw analyte name as printed in the PDF (before Gemini "
            "translation/normalization). E.g. 'Proteina C reactiva'. Used as "
            "an alternative fuzzy source when 'test_name' is misnormalized."))
    panel_header_raw: str | None = Field(
        default=None, max_length=1000,
        description=(
            "Verbatim section/panel header from the PDF, admin annotations "
            "stripped by Gemini. E.g. 'Hemoleucograma completa - Sange - "
            "Impedanta (PENTRA ES 60)'. Contains specimen + method + analyzer "
            "keywords for LOINC axis inference in the rules layer."))
    analyte_line_raw: str | None = Field(
        default=None, max_length=500,
        description=(
            "Verbatim inline metadata from the analyte row (specimen + method "
            "+ analyzer), copied by Gemini after stripping row number, name, "
            "value, unit and range. E.g. '-Ser - Turbidimetrie (ABX PENTRA "
            "C400 ISE)'. Complements panel_header_raw for Type B layouts."))


class LoincResponse(BaseModel):
    loinc: str
    name: str
    component: str | None = None
    property: str | None = None
    system: str | None = None
    method: str | None = None
    score: float
    # LOINC CLASS code (e.g. HEM, CHEM, SERO, ENDO, COAG, UA). Carried through
    # to the C# pipeline so the Compare view can group parameters by medical
    # specialty. Null when the LoincDictionary row has no CLASS value.
    loinc_class: str | None = None
    # Provenance: "anchor" => hard-coded canonical mapping (score=1.0,
    # patient-grade certainty). "semantic" => embedding+fuzzy+rules pipeline.
    loinc_source: str = "semantic"
    # "Verdict pe axe": per-axis explanation of the decision (strings only).
    axis_verdict: dict[str, str] | None = None


# -------------------- Endpoints --------------------
@app.get("/health")
async def health():
    return {"status": "ok"}


@app.get("/ready")
async def ready():
    if STORE.embeddings is None or not STORE.metadata:
        return JSONResponse(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            content={"status": "not_ready", "reason": "LOINC store not loaded"},
        )
    return {"status": "ready", "entries": STORE.size, "cache": cache_stats()}


@app.post("/loinc/match", response_model=LoincResponse)
def match(req: LoincRequest):
    if STORE.embeddings is None or not STORE.metadata:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="LOINC store is not loaded. Run seed_embeddings.py first.",
        )

    # Etapa Python-1: log receipt of the new source-context fields so we can
    # verify end-to-end that C# is sending them correctly for both Type A
    # (metadata in panel header) and Type B (metadata inline per row) PDFs.
    # The matching pipeline itself is NOT yet consuming these fields — that
    # arrives in Etapa Python-2/3. Anything unusual visible here (e.g. always
    # null, wrong content, obvious misparsing) can be caught before we wire
    # the fields into scoring.
    log.info(
        "/loinc/match received | test_name=%r unit=%r raw=%r panel_header=%r analyte_line=%r",
        req.test_name, req.unit,
        req.raw_parameter_name, req.panel_header_raw, req.analyte_line_raw,
    )

    try:
        result = find_loinc(
            req.test_name,
            unit=req.unit,
            raw_parameter_name=req.raw_parameter_name,
            panel_header_raw=req.panel_header_raw,
            analyte_line_raw=req.analyte_line_raw,
        )
    except Exception as ex:
        log.exception("find_loinc failed for input: %r (unit=%r)", req.test_name, req.unit)
        raise HTTPException(status_code=500, detail=str(ex))

    if result is None:
        raise HTTPException(status_code=404, detail="No LOINC match found.")
    return LoincResponse(**result.to_dict())


@app.post("/loinc/match-batch")
def match_batch(reqs: list[LoincRequest]):
    """Resolve MANY analytes in ONE request.

    The C# side used to issue one HTTP POST per analyte, sequentially — a
    40-parameter lab report meant 40 round-trips (15-120 s). Here the whole
    report is matched in a single call, with the CPU-bound matching spread
    over a thread pool.

    Returns a list positionally aligned with the input; entries the matcher
    could not resolve come back as ``null`` instead of a 404, so one bad
    analyte never fails the whole report.
    """
    if STORE.embeddings is None or not STORE.metadata:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="LOINC store is not loaded. Run seed_embeddings.py first.",
        )

    if not reqs:
        return []

    t0 = time.perf_counter()

    # Embed only the analytes we have NOT resolved before. The cache answers
    # repeats (the same analyte names come back for every user, every report)
    # without paying for the embedding or the 142 MB similarity scan again.
    keys = [
        cache_key(r.test_name or "", r.unit, r.raw_parameter_name,
                  r.panel_header_raw, r.analyte_line_raw)
        for r in reqs
    ]
    cached: dict[int, dict | None] = {}
    to_encode: list[int] = []
    duplicate_of: dict[int, int] = {}
    first_seen: dict[tuple, int] = {}
    for idx, key in enumerate(keys):
        found, value = cache_lookup(key)
        if found:
            cached[idx] = value.to_dict() if value is not None else None
        elif key in first_seen:
            # The very same question twice in one report: answer it once.
            duplicate_of[idx] = first_seen[key]
        else:
            first_seen[key] = idx
            to_encode.append(idx)

    embeddings: dict[int, object] = {}
    if to_encode:
        try:
            vectors = encode_queries([reqs[i].test_name or "" for i in to_encode])
            embeddings = {i: vectors[n] for n, i in enumerate(to_encode) if n < len(vectors)}
        except Exception:
            log.exception("batch embedding failed; falling back to per-analyte encoding")
            embeddings = {}
    t_emb = time.perf_counter()

    def one(idx: int):
        req = reqs[idx]
        try:
            result = find_loinc(
                req.test_name,
                unit=req.unit,
                raw_parameter_name=req.raw_parameter_name,
                panel_header_raw=req.panel_header_raw,
                analyte_line_raw=req.analyte_line_raw,
                query_embedding=embeddings.get(idx),
            )
        except Exception:
            log.exception("find_loinc failed for input: %r (unit=%r)", req.test_name, req.unit)
            return None
        return result.to_dict() if result is not None else None

    # Matching is numpy/CPU bound and releases the GIL inside numpy, but it is
    # limited by memory bandwidth, so more threads than cores only add
    # contention (a container gets 1-2 vCPU, not 16).
    results: list[dict | None] = [None] * len(reqs)
    for idx, value in cached.items():
        results[idx] = value

    if to_encode:
        workers = min(os.cpu_count() or 1, len(to_encode))
        if workers > 1:
            with ThreadPoolExecutor(max_workers=workers) as pool:
                for idx, value in zip(to_encode, pool.map(one, to_encode)):
                    results[idx] = value
        else:
            for idx in to_encode:
                results[idx] = one(idx)

    for idx, representative in duplicate_of.items():
        results[idx] = results[representative]

    matched = sum(1 for r in results if r is not None)
    log.info(
        "/loinc/match-batch | %d analytes (%d from cache, %d duplicates, %d computed), "
        "%d matched, %.0f ms total (%.0f ms batch-embedding + %.0f ms matching)",
        len(reqs), len(cached), len(duplicate_of), len(to_encode), matched,
        (time.perf_counter() - t0) * 1000,
        (t_emb - t0) * 1000, (time.perf_counter() - t_emb) * 1000,
    )
    return results


@app.get("/loinc/cache")
async def cache_info():
    """In-process result cache: size, hit rate. Useful to confirm at a glance
    that repeats are being served from memory instead of recomputed."""
    return cache_stats()


@app.get("/loinc/anchors")
def anchors():
    """Inspection endpoint. Returns the full canonical-anchor table so the
    operator can audit which canonical English terms are hard-coded to which
    LOINC code. The endpoint ALSO resolves every anchor against the loaded
    LoincStore so you can immediately see whether each code is present
    (and what its long-common-name is) on the current seed.
    """
    raw = all_anchors()
    items = []
    resolved = 0
    unresolved = 0
    for term, code in raw.items():
        meta = STORE.get_by_code(code) if STORE.metadata else None
        if meta is not None:
            resolved += 1
            items.append({
                "canonical_term": term,
                "loinc": code,
                "resolved": True,
                "loinc_long_name": meta.get("name"),
                "loinc_class": meta.get("class"),
            })
        else:
            unresolved += 1
            items.append({
                "canonical_term": term,
                "loinc": code,
                "resolved": False,
                "loinc_long_name": None,
                "loinc_class": None,
            })
    return {
        "total": anchor_count(),
        "resolved_in_store": resolved,
        "unresolved_in_store": unresolved,
        "store_loaded": bool(STORE.metadata),
        "anchors": items,
    }
