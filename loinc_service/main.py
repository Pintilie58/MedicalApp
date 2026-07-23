"""
main.py
-------
FastAPI entry point for the LOINC matcher microservice.

Endpoints
---------
GET  /health       -> liveness probe (no LOINC data needed)
GET  /ready        -> readiness probe (returns 503 until LoincStore is loaded)
POST /loinc/match  -> resolve a LOINC code for an English medical term

Run (development):
    uvicorn main:app --host 127.0.0.1 --port 8000 --reload

Run (production-like, single worker):
    uvicorn main:app --host 0.0.0.0 --port 8000 --workers 1
"""

from __future__ import annotations

import logging
from contextlib import asynccontextmanager

from fastapi import FastAPI, HTTPException, status
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field

from loinc_store import STORE
from canonical_anchors import all_anchors, anchor_count
from pipeline import find_loinc, get_model

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


# -------------------- Endpoints --------------------
@app.get("/health")
def health():
    return {"status": "ok"}


@app.get("/ready")
def ready():
    if STORE.embeddings is None or not STORE.metadata:
        return JSONResponse(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            content={"status": "not_ready", "reason": "LOINC store not loaded"},
        )
    return {"status": "ready", "entries": STORE.size}


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
        result = find_loinc(req.test_name, unit=req.unit)
    except Exception as ex:
        log.exception("find_loinc failed for input: %r (unit=%r)", req.test_name, req.unit)
        raise HTTPException(status_code=500, detail=str(ex))

    if result is None:
        raise HTTPException(status_code=404, detail="No LOINC match found.")
    return LoincResponse(**result.to_dict())


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
