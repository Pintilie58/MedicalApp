"""Measures the real cost of one LOINC match so capacity planning is not guesswork.

Run: python3 /app/memory/probes/loinc_capacity_probe.py
Prints: model load time, encode latency (1 vs batch), cost of the 97k-row
similarity scan, RSS of one worker.
"""
import os, time, resource
import numpy as np

os.environ.setdefault("TOKENIZERS_PARALLELISM", "false")


def rss_mb():
    return resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024


t0 = time.perf_counter()
from sentence_transformers import SentenceTransformer
t_import = time.perf_counter() - t0

t0 = time.perf_counter()
model = SentenceTransformer("sentence-transformers/all-MiniLM-L6-v2")
t_load = time.perf_counter() - t0
rss_after_model = rss_mb()

names = ["Glycated hemoglobin", "Cholesterol total", "Thyroid stimulating hormone",
         "Alanine aminotransferase", "Ferritin", "C reactive protein",
         "Prothrombin time INR", "Lymphocytes percent", "Glucose serum", "Creatinine"]

model.encode(["warmup"])

t0 = time.perf_counter()
for n in names:
    model.encode([n])
t_one_by_one = (time.perf_counter() - t0) / len(names)

big = names * 9  # 90 analytes, a large real report
t0 = time.perf_counter()
model.encode(big, batch_size=32)
t_batch = time.perf_counter() - t0

# Similarity scan against a REAL-SIZE dictionary (97k LOINC rows x 384 dims).
N = 97_000
mat = np.random.rand(N, 384).astype(np.float32)
mat /= np.linalg.norm(mat, axis=1, keepdims=True)
rss_after_matrix = rss_mb()
q = np.random.rand(384).astype(np.float32)
q /= np.linalg.norm(q)

t0 = time.perf_counter()
for _ in range(20):
    sims = mat @ q
    np.argpartition(-sims, 25)[:25]
t_scan = (time.perf_counter() - t0) / 20

print(f"import sentence_transformers : {t_import*1000:8.0f} ms")
print(f"model load (cold)            : {t_load*1000:8.0f} ms")
print(f"encode 1 name                : {t_one_by_one*1000:8.1f} ms")
print(f"encode 90 names (batched)    : {t_batch*1000:8.0f} ms  ({t_batch/90*1000:.1f} ms/name)")
print(f"similarity scan 97k rows     : {t_scan*1000:8.1f} ms")
print(f"RSS after model              : {rss_after_model:8.0f} MB")
print(f"RSS after 97k matrix         : {rss_after_matrix:8.0f} MB")
print(f"matrix itself                : {N*384*4/1024/1024:8.0f} MB")
print(f"CPU count                    : {os.cpu_count()}")
