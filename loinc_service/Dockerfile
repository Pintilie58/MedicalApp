# LoincMatcher - FastAPI + sentence-transformers (CPU only)
# Build context: ./loinc_service

FROM python:3.11-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    HF_HOME=/opt/hf \
    LOINC_HOST=0.0.0.0 \
    LOINC_PORT=8000

WORKDIR /srv

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
       curl gcc g++ unixodbc-dev \
    && rm -rf /var/lib/apt/lists/*

# CPU-only PyTorch first: the default wheel drags in ~2.5 GB of CUDA libraries.
RUN pip install --no-cache-dir torch==2.5.1 --index-url https://download.pytorch.org/whl/cpu

COPY requirements.txt ./
RUN pip install --no-cache-dir -r requirements.txt

# Bake the embedding model into the image so startup needs no internet.
RUN python -c "from sentence_transformers import SentenceTransformer; SentenceTransformer('sentence-transformers/all-MiniLM-L6-v2')"

COPY . .

EXPOSE 8000
HEALTHCHECK --interval=15s --timeout=5s --start-period=90s --retries=5 \
    CMD curl -fsS http://127.0.0.1:8000/health || exit 1

CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8000", "--workers", "1"]
