# OCR benchmark report

- Data: `data/synthetic-timing`  |  languages: per-page meta  |  det max side: 1920
- Machine: Intel(R) Xeon(R) Processor @ 2.10GHz, nproc=4, ORT intra-op threads used: **4**, run-all-recognisers: False, onnxruntime 1.31.0, Python 3.12.3
- System 1-min load average at start/end: 5.3 / 6.3 (other processes on the machine inflate timings when this exceeds ~1)
- Models: `/tmp/claude-0/m/models_final`
- Model load + warm-up (excluded from per-page time): **0.71 s** (det 0.06s, rec_devanagari 0.13s, rec_en 0.11s, rec_ta 0.11s, rec_te 0.13s)
- Per-page seconds = detect + crop + recognise (all requested recognisers) after image decode; decode time reported in JSON.

## Aggregate

| Group | Pages | CER | CER no-space | Word acc | s/page mean | median | p95 | max |
|---|---|---|---|---|---|---|---|---|
| **ALL** | 6 | 1.68% | 1.76% | 93.4% | 6.03 | 6.21 | 9.15 | 9.15 |
| hi/clean | 1 | 1.45% | 1.78% | 93.4% | 5.51 | 5.51 | 5.51 | 5.51 |
| ta/clean | 1 | 1.08% | 1.20% | 89.7% | 7.84 | 7.84 | 7.84 | 7.84 |
| te/clean | 3 | 0.76% | 0.79% | 94.5% | 6.72 | 6.21 | 9.15 | 9.15 |
| tem/clean | 1 | 4.22% | 4.33% | 92.7% | 2.71 | 2.71 | 2.71 | 2.71 |

Time split per page (mean): detect 1.75 s (29%), crop 0.07 s, recognise 4.21 s (70%), process CPU time 13.32 s/page (divide by threads for an idle-machine wall estimate: 3.33 s); image decode (not counted) 0.14 s; mean text lines per page 29.3.

CER/word accuracy are micro-averages (total errors / total reference units); word accuracy = 1 - WER; 'CER no-space' ignores all whitespace (the PP-OCRv3 Indic recognisers rarely emit spaces). Text is NFC-normalised and whitespace-collapsed before scoring.

## Per page

| Page | Lines | CER | CER no-space | Word acc | det s | rec s | total s |
|---|---|---|---|---|---|---|---|
| hi_00_clean.png | 24 | 1.4% | 1.8% | 93.4% | 1.99 | 3.45 | 5.51 |
| ta_00_clean.png | 31 | 1.1% | 1.2% | 89.7% | 2.07 | 5.70 | 7.84 |
| te_00_clean.png | 27 | 0.6% | 0.6% | 94.7% | 1.78 | 4.37 | 6.21 |
| te_01_clean.png | 30 | 1.1% | 1.2% | 92.6% | 2.11 | 6.93 | 9.15 |
| te_02_clean.png | 30 | 0.5% | 0.5% | 96.2% | 1.75 | 2.97 | 4.79 |
| tem_00_clean.png | 34 | 4.2% | 4.3% | 92.7% | 0.81 | 1.86 | 2.71 |
