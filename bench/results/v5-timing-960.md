# OCR benchmark report

- Data: `data/synthetic-timing`  |  languages: per-page meta  |  det max side: 960
- Machine: Intel(R) Xeon(R) Processor @ 2.10GHz, nproc=4, ORT intra-op threads used: **4**, run-all-recognisers: False, onnxruntime 1.31.0, Python 3.12.3
- System 1-min load average at start/end: 6.0 / 5.7 (other processes on the machine inflate timings when this exceeds ~1)
- Models: `/tmp/claude-0/m/models_final`
- Model load + warm-up (excluded from per-page time): **0.63 s** (det 0.09s, rec_devanagari 0.11s, rec_en 0.08s, rec_ta 0.11s, rec_te 0.12s)
- Per-page seconds = detect + crop + recognise (all requested recognisers) after image decode; decode time reported in JSON.

## Aggregate

| Group | Pages | CER | CER no-space | Word acc | s/page mean | median | p95 | max |
|---|---|---|---|---|---|---|---|---|
| **ALL** | 6 | 0.93% | 1.02% | 93.8% | 3.57 | 2.31 | 7.22 | 7.22 |
| hi/clean | 1 | 1.23% | 1.52% | 94.6% | 5.49 | 5.49 | 5.49 | 5.49 |
| ta/clean | 1 | 0.81% | 0.90% | 92.3% | 7.22 | 7.22 | 7.22 | 7.22 |
| te/clean | 3 | 0.94% | 0.99% | 93.4% | 2.23 | 2.26 | 2.31 | 2.31 |
| tem/clean | 1 | 0.81% | 0.90% | 94.7% | 1.99 | 1.99 | 1.99 | 1.99 |

Time split per page (mean): detect 0.38 s (11%), crop 0.06 s, recognise 3.13 s (88%), process CPU time 9.87 s/page (divide by threads for an idle-machine wall estimate: 2.47 s); image decode (not counted) 0.10 s; mean text lines per page 29.5.

CER/word accuracy are micro-averages (total errors / total reference units); word accuracy = 1 - WER; 'CER no-space' ignores all whitespace (the PP-OCRv3 Indic recognisers rarely emit spaces). Text is NFC-normalised and whitespace-collapsed before scoring.

## Per page

| Page | Lines | CER | CER no-space | Word acc | det s | rec s | total s |
|---|---|---|---|---|---|---|---|
| hi_00_clean.png | 24 | 1.2% | 1.5% | 94.6% | 0.58 | 4.86 | 5.49 |
| ta_00_clean.png | 31 | 0.8% | 0.9% | 92.3% | 0.56 | 6.60 | 7.22 |
| te_00_clean.png | 27 | 1.0% | 0.9% | 91.6% | 0.60 | 1.58 | 2.26 |
| te_01_clean.png | 30 | 1.1% | 1.3% | 93.6% | 0.18 | 2.08 | 2.31 |
| te_02_clean.png | 30 | 0.8% | 0.8% | 94.4% | 0.19 | 1.89 | 2.12 |
| tem_00_clean.png | 35 | 0.8% | 0.9% | 94.7% | 0.17 | 1.77 | 1.99 |
