# OCR benchmark report

- Data: `data/synthetic`  |  languages: per-page meta  |  det max side: 1280
- Machine: Intel(R) Xeon(R) Processor @ 2.10GHz, nproc=4, ORT intra-op threads used: **4**, run-all-recognisers: False, onnxruntime 1.31.0, Python 3.12.3
- System 1-min load average at start/end: 6.4 / 7.4 (other processes on the machine inflate timings when this exceeds ~1)
- Models: `/tmp/claude-0/m/models_final`
- Model load + warm-up (excluded from per-page time): **0.58 s** (det 0.09s, rec_devanagari 0.10s, rec_en 0.10s, rec_ka 0.08s, rec_ta 0.07s, rec_te 0.08s)
- Per-page seconds = detect + crop + recognise (all requested recognisers) after image decode; decode time reported in JSON.

## Aggregate

| Group | Pages | CER | CER no-space | Word acc | s/page mean | median | p95 | max |
|---|---|---|---|---|---|---|---|---|
| **ALL** | 90 | 11.40% | 10.91% | 73.4% | 5.53 | 5.77 | 8.92 | 10.45 |
| hi/clean | 10 | 1.01% | 1.22% | 95.5% | 7.15 | 7.79 | 10.45 | 10.45 |
| hi/degraded | 10 | 6.10% | 6.38% | 79.5% | 6.58 | 6.77 | 9.07 | 9.07 |
| kn/clean | 10 | 36.02% | 30.30% | 0.0% | 3.02 | 3.14 | 3.87 | 3.87 |
| kn/degraded | 10 | 53.55% | 51.47% | 0.0% | 2.59 | 2.72 | 3.11 | 3.11 |
| ta/clean | 10 | 0.85% | 0.95% | 92.4% | 6.31 | 6.54 | 8.46 | 8.46 |
| ta/degraded | 10 | 0.58% | 0.65% | 94.9% | 6.22 | 7.16 | 8.36 | 8.36 |
| te/clean | 10 | 0.79% | 0.77% | 93.8% | 6.17 | 7.00 | 8.66 | 8.66 |
| te/degraded | 10 | 4.13% | 4.62% | 78.2% | 4.70 | 4.79 | 6.26 | 6.26 |
| tem/clean | 5 | 3.35% | 3.49% | 93.3% | 7.55 | 7.74 | 8.85 | 8.85 |
| tem/degraded | 5 | 1.55% | 1.76% | 93.1% | 6.43 | 6.30 | 7.49 | 7.49 |

Time split per page (mean): detect 0.86 s (15%), crop 0.06 s, recognise 4.61 s (83%), image decode (not counted) 0.07 s; mean text lines per page 29.8.

CER/word accuracy are micro-averages (total errors / total reference units); word accuracy = 1 - WER; 'CER no-space' ignores all whitespace (the PP-OCRv3 Indic recognisers rarely emit spaces). Text is NFC-normalised and whitespace-collapsed before scoring.

## Per page

| Page | Lines | CER | CER no-space | Word acc | det s | rec s | total s |
|---|---|---|---|---|---|---|---|
| hi_00_clean.png | 24 | 0.9% | 1.1% | 95.7% | 0.58 | 1.35 | 1.97 |
| hi_00_degraded.jpg | 30 | 1.9% | 2.1% | 92.2% | 0.60 | 4.12 | 4.77 |
| hi_01_clean.png | 32 | 0.8% | 1.0% | 96.3% | 0.68 | 8.31 | 9.08 |
| hi_01_degraded.jpg | 30 | 1.0% | 1.2% | 96.1% | 1.15 | 6.37 | 7.61 |
| hi_02_clean.png | 27 | 1.2% | 1.5% | 94.6% | 0.98 | 5.03 | 6.09 |
| hi_02_degraded.jpg | 27 | 1.5% | 1.9% | 94.3% | 0.85 | 4.43 | 5.36 |
| hi_03_clean.png | 28 | 0.8% | 1.0% | 96.2% | 0.79 | 4.89 | 5.77 |
| hi_03_degraded.jpg | 28 | 1.3% | 1.5% | 95.0% | 0.80 | 5.02 | 5.90 |
| hi_04_clean.png | 27 | 1.3% | 1.5% | 94.9% | 0.95 | 5.67 | 6.69 |
| hi_04_degraded.jpg | 29 | 1.4% | 1.7% | 93.9% | 0.89 | 7.95 | 8.92 |
| hi_05_clean.png | 31 | 1.5% | 1.7% | 94.0% | 1.06 | 9.36 | 10.45 |
| hi_05_degraded.jpg | 28 | 3.4% | 3.9% | 86.9% | 1.46 | 7.51 | 9.07 |
| hi_06_clean.png | 31 | 0.6% | 0.8% | 97.2% | 0.95 | 6.81 | 7.79 |
| hi_06_degraded.jpg | 27 | 13.1% | 13.1% | 59.2% | 0.99 | 5.89 | 6.94 |
| hi_07_clean.png | 27 | 1.2% | 1.5% | 95.0% | 0.89 | 5.38 | 6.30 |
| hi_07_degraded.jpg | 28 | 15.8% | 17.1% | 56.3% | 0.89 | 3.74 | 4.68 |
| hi_08_clean.png | 31 | 0.9% | 1.1% | 96.1% | 0.89 | 6.88 | 7.79 |
| hi_08_degraded.jpg | 30 | 7.0% | 7.3% | 73.6% | 0.94 | 4.87 | 5.84 |
| hi_09_clean.png | 33 | 0.9% | 1.1% | 95.2% | 1.03 | 8.49 | 9.54 |
| hi_09_degraded.jpg | 30 | 14.6% | 14.2% | 48.4% | 0.87 | 5.85 | 6.77 |
| kn_00_clean.png | 32 | 38.1% | 33.1% | 0.0% | 0.88 | 2.92 | 3.87 |
| kn_00_degraded.jpg | 29 | 38.3% | 33.9% | 0.0% | 0.75 | 1.45 | 2.30 |
| kn_01_clean.png | 33 | 35.6% | 29.7% | 0.0% | 0.80 | 2.26 | 3.14 |
| kn_01_degraded.jpg | 31 | 38.0% | 32.3% | 0.0% | 0.75 | 1.81 | 2.63 |
| kn_02_clean.png | 31 | 34.3% | 28.1% | 0.0% | 1.08 | 2.15 | 3.31 |
| kn_02_degraded.jpg | 31 | 39.7% | 35.5% | 0.0% | 0.95 | 1.96 | 3.03 |
| kn_03_clean.png | 25 | 36.2% | 30.8% | 0.5% | 0.65 | 1.95 | 2.64 |
| kn_03_degraded.jpg | 31 | 38.9% | 34.0% | 0.0% | 0.83 | 1.87 | 2.80 |
| kn_04_clean.png | 29 | 36.0% | 30.5% | 0.0% | 0.89 | 1.65 | 2.64 |
| kn_04_degraded.jpg | 27 | 38.3% | 33.1% | 0.0% | 0.75 | 1.89 | 2.72 |
| kn_05_clean.png | 30 | 36.2% | 30.2% | 0.0% | 1.02 | 2.25 | 3.32 |
| kn_05_degraded.jpg | 26 | 86.8% | 88.9% | 0.0% | 0.78 | 0.92 | 1.73 |
| kn_06_clean.png | 31 | 36.6% | 31.0% | 0.0% | 0.79 | 2.54 | 3.36 |
| kn_06_degraded.jpg | 30 | 43.2% | 38.7% | 0.0% | 0.93 | 1.75 | 2.74 |
| kn_07_clean.png | 27 | 36.9% | 31.0% | 0.0% | 0.69 | 1.73 | 2.45 |
| kn_07_degraded.jpg | 34 | 77.3% | 79.3% | 0.0% | 0.98 | 2.11 | 3.11 |
| kn_08_clean.png | 31 | 34.3% | 28.2% | 0.0% | 1.05 | 1.86 | 2.95 |
| kn_08_degraded.jpg | 28 | 85.5% | 89.6% | 0.0% | 0.91 | 1.56 | 2.52 |
| kn_09_clean.png | 27 | 35.9% | 30.1% | 0.0% | 0.85 | 1.69 | 2.57 |
| kn_09_degraded.jpg | 28 | 53.4% | 54.1% | 0.0% | 0.84 | 1.45 | 2.33 |
| ta_00_clean.png | 31 | 1.0% | 1.1% | 90.7% | 0.82 | 3.68 | 4.61 |
| ta_00_degraded.jpg | 36 | 0.7% | 0.8% | 93.5% | 0.33 | 2.64 | 3.03 |
| ta_01_clean.png | 31 | 1.0% | 1.1% | 91.0% | 0.34 | 1.95 | 2.34 |
| ta_01_degraded.jpg | 33 | 0.6% | 0.6% | 94.9% | 0.32 | 3.28 | 3.66 |
| ta_02_clean.png | 34 | 0.9% | 1.0% | 91.3% | 0.84 | 7.52 | 8.46 |
| ta_02_degraded.jpg | 29 | 0.6% | 0.7% | 95.1% | 1.35 | 6.49 | 7.92 |
| ta_03_clean.png | 29 | 0.9% | 1.0% | 92.7% | 1.03 | 6.62 | 7.74 |
| ta_03_degraded.jpg | 36 | 0.3% | 0.3% | 97.4% | 0.94 | 7.30 | 8.36 |
| ta_04_clean.png | 29 | 0.6% | 0.7% | 94.1% | 1.00 | 5.45 | 6.54 |
| ta_04_degraded.jpg | 32 | 0.7% | 0.8% | 94.2% | 0.93 | 6.16 | 7.16 |
| ta_05_clean.png | 27 | 0.9% | 1.0% | 92.4% | 0.92 | 4.51 | 5.48 |
| ta_05_degraded.jpg | 32 | 1.4% | 1.5% | 88.2% | 0.97 | 4.16 | 5.19 |
| ta_06_clean.png | 31 | 0.7% | 0.8% | 93.7% | 0.73 | 6.72 | 7.50 |
| ta_06_degraded.jpg | 35 | 0.2% | 0.2% | 98.0% | 0.86 | 5.13 | 6.05 |
| ta_07_clean.png | 29 | 0.9% | 1.0% | 91.8% | 0.69 | 5.22 | 5.95 |
| ta_07_degraded.jpg | 28 | 0.7% | 0.8% | 93.6% | 0.77 | 4.26 | 5.06 |
| ta_08_clean.png | 33 | 0.6% | 0.7% | 94.0% | 0.85 | 7.45 | 8.33 |
| ta_08_degraded.jpg | 31 | 0.4% | 0.4% | 96.7% | 0.93 | 7.34 | 8.33 |
| ta_09_clean.png | 30 | 0.9% | 1.0% | 91.5% | 1.49 | 4.66 | 6.17 |
| ta_09_degraded.jpg | 34 | 0.4% | 0.5% | 96.7% | 0.98 | 6.44 | 7.46 |
| te_00_clean.png | 27 | 0.8% | 0.8% | 92.9% | 0.93 | 4.56 | 5.55 |
| te_00_degraded.jpg | 27 | 1.4% | 1.4% | 89.3% | 1.03 | 4.06 | 5.18 |
| te_01_clean.png | 30 | 1.1% | 1.2% | 93.0% | 1.09 | 7.47 | 8.66 |
| te_01_degraded.jpg | 32 | 1.2% | 1.4% | 93.2% | 0.83 | 5.18 | 6.11 |
| te_02_clean.png | 30 | 0.7% | 0.8% | 94.4% | 1.01 | 5.89 | 7.00 |
| te_02_degraded.jpg | 29 | 0.7% | 0.7% | 94.7% | 0.83 | 5.34 | 6.26 |
| te_03_clean.png | 34 | 0.6% | 0.6% | 95.7% | 0.81 | 7.28 | 8.16 |
| te_03_degraded.jpg | 27 | 0.9% | 0.9% | 94.0% | 0.85 | 4.85 | 5.77 |
| te_04_clean.png | 29 | 0.7% | 0.6% | 93.5% | 0.94 | 6.03 | 7.06 |
| te_04_degraded.jpg | 26 | 0.7% | 0.8% | 94.7% | 0.83 | 3.72 | 4.64 |
| te_05_clean.png | 27 | 1.0% | 0.8% | 91.2% | 0.57 | 3.29 | 3.89 |
| te_05_degraded.jpg | 28 | 2.5% | 2.7% | 81.3% | 0.55 | 2.23 | 2.81 |
| te_06_clean.png | 34 | 0.6% | 0.5% | 94.2% | 0.56 | 4.72 | 5.31 |
| te_06_degraded.jpg | 34 | 3.5% | 3.9% | 76.1% | 0.79 | 2.93 | 3.75 |
| te_07_clean.png | 29 | 1.0% | 1.0% | 94.5% | 0.51 | 3.45 | 3.98 |
| te_07_degraded.jpg | 30 | 10.2% | 11.6% | 51.3% | 0.51 | 2.53 | 3.07 |
| te_08_clean.png | 31 | 0.8% | 0.8% | 93.7% | 0.67 | 4.19 | 4.88 |
| te_08_degraded.jpg | 30 | 15.4% | 17.4% | 35.7% | 0.63 | 3.94 | 4.59 |
| te_09_clean.png | 28 | 0.7% | 0.7% | 93.4% | 0.60 | 6.56 | 7.18 |
| te_09_degraded.jpg | 27 | 1.7% | 1.9% | 87.8% | 0.80 | 3.97 | 4.79 |
| tem_00_clean.png | 33 | 7.8% | 8.0% | 89.4% | 0.78 | 7.42 | 8.28 |
| tem_00_degraded.jpg | 31 | 1.7% | 2.0% | 94.9% | 0.88 | 6.17 | 7.12 |
| tem_01_clean.png | 27 | 0.5% | 0.6% | 96.8% | 0.92 | 5.91 | 6.94 |
| tem_01_degraded.jpg | 27 | 1.6% | 1.8% | 91.3% | 1.05 | 5.16 | 6.30 |
| tem_02_clean.png | 31 | 4.6% | 4.7% | 90.9% | 0.73 | 8.06 | 8.85 |
| tem_02_degraded.jpg | 28 | 1.9% | 2.2% | 91.7% | 0.96 | 4.90 | 5.94 |
| tem_03_clean.png | 29 | 0.7% | 0.8% | 95.9% | 1.18 | 6.48 | 7.74 |
| tem_03_degraded.jpg | 32 | 1.3% | 1.5% | 93.6% | 0.95 | 6.45 | 7.49 |
| tem_04_clean.png | 32 | 0.9% | 1.0% | 95.5% | 0.87 | 5.00 | 5.97 |
| tem_04_degraded.jpg | 27 | 1.2% | 1.3% | 93.3% | 0.87 | 4.36 | 5.30 |
