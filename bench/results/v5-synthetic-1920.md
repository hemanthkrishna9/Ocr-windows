# OCR benchmark report

- Data: `data/synthetic`  |  languages: per-page meta  |  det max side: 1920
- Machine: Intel(R) Xeon(R) Processor @ 2.10GHz, nproc=4, ORT intra-op threads used: **4**, run-all-recognisers: False, onnxruntime 1.31.0, Python 3.12.3
- System 1-min load average at start/end: 5.2 / 5.5 (other processes on the machine inflate timings when this exceeds ~1)
- Models: `/tmp/claude-0/m/models_final`
- Model load + warm-up (excluded from per-page time): **0.53 s** (det 0.14s, rec_devanagari 0.09s, rec_en 0.06s, rec_ka 0.06s, rec_ta 0.05s, rec_te 0.07s)
- Per-page seconds = detect + crop + recognise (all requested recognisers) after image decode; decode time reported in JSON.

## Aggregate

| Group | Pages | CER | CER no-space | Word acc | s/page mean | median | p95 | max |
|---|---|---|---|---|---|---|---|---|
| **ALL** | 90 | 11.18% | 10.81% | 74.8% | 2.75 | 2.06 | 7.81 | 9.13 |
| hi/clean | 10 | 1.06% | 1.28% | 95.3% | 2.25 | 2.25 | 2.67 | 2.67 |
| hi/degraded | 10 | 3.97% | 4.47% | 86.1% | 2.00 | 2.01 | 2.46 | 2.46 |
| kn/clean | 10 | 36.13% | 30.44% | 0.0% | 1.41 | 1.44 | 1.82 | 1.82 |
| kn/degraded | 10 | 55.55% | 54.13% | 0.1% | 1.37 | 1.46 | 1.92 | 1.92 |
| ta/clean | 10 | 0.80% | 0.89% | 92.9% | 2.24 | 2.27 | 2.64 | 2.64 |
| ta/degraded | 10 | 0.61% | 0.69% | 94.6% | 2.16 | 2.16 | 2.57 | 2.57 |
| te/clean | 10 | 0.69% | 0.66% | 94.6% | 3.30 | 2.48 | 8.52 | 8.52 |
| te/degraded | 10 | 3.69% | 4.16% | 78.9% | 2.86 | 2.05 | 8.25 | 8.25 |
| tem/clean | 5 | 1.70% | 1.82% | 94.5% | 7.64 | 7.81 | 9.13 | 9.13 |
| tem/degraded | 5 | 1.34% | 1.53% | 94.1% | 6.75 | 6.46 | 7.48 | 7.48 |

Time split per page (mean): detect 0.85 s (31%), crop 0.04 s, recognise 1.87 s (68%), image decode (not counted) 0.04 s; mean text lines per page 29.9.

CER/word accuracy are micro-averages (total errors / total reference units); word accuracy = 1 - WER; 'CER no-space' ignores all whitespace (the PP-OCRv3 Indic recognisers rarely emit spaces). Text is NFC-normalised and whitespace-collapsed before scoring.

## Per page

| Page | Lines | CER | CER no-space | Word acc | det s | rec s | total s |
|---|---|---|---|---|---|---|---|
| hi_00_clean.png | 24 | 1.4% | 1.8% | 93.4% | 0.78 | 0.99 | 1.80 |
| hi_00_degraded.jpg | 30 | 1.7% | 1.9% | 93.2% | 1.00 | 1.42 | 2.46 |
| hi_01_clean.png | 32 | 0.9% | 1.1% | 95.7% | 0.75 | 1.83 | 2.62 |
| hi_01_degraded.jpg | 30 | 0.8% | 0.9% | 97.2% | 0.66 | 1.30 | 2.00 |
| hi_02_clean.png | 27 | 1.2% | 1.5% | 94.6% | 0.67 | 1.22 | 1.93 |
| hi_02_degraded.jpg | 27 | 1.4% | 1.6% | 94.8% | 0.66 | 1.15 | 1.85 |
| hi_03_clean.png | 28 | 0.6% | 0.8% | 97.1% | 0.77 | 1.25 | 2.07 |
| hi_03_degraded.jpg | 28 | 1.1% | 1.3% | 96.1% | 0.71 | 1.26 | 2.01 |
| hi_04_clean.png | 27 | 1.4% | 1.7% | 93.6% | 0.71 | 1.46 | 2.22 |
| hi_04_degraded.jpg | 29 | 1.6% | 1.8% | 93.6% | 0.71 | 1.29 | 2.04 |
| hi_05_clean.png | 31 | 1.5% | 1.8% | 93.7% | 0.57 | 2.07 | 2.66 |
| hi_05_degraded.jpg | 28 | 2.1% | 2.4% | 91.5% | 0.67 | 1.54 | 2.22 |
| hi_06_clean.png | 31 | 0.8% | 1.0% | 96.2% | 0.72 | 1.93 | 2.67 |
| hi_06_degraded.jpg | 27 | 7.7% | 8.3% | 72.7% | 0.60 | 1.12 | 1.74 |
| hi_07_clean.png | 27 | 1.2% | 1.3% | 95.4% | 0.62 | 1.23 | 1.86 |
| hi_07_degraded.jpg | 28 | 10.4% | 12.1% | 67.7% | 0.60 | 0.90 | 1.52 |
| hi_08_clean.png | 31 | 0.8% | 1.0% | 96.5% | 0.58 | 1.66 | 2.25 |
| hi_08_degraded.jpg | 30 | 4.5% | 5.0% | 83.1% | 0.63 | 1.20 | 1.84 |
| hi_09_clean.png | 33 | 0.9% | 1.1% | 95.8% | 0.58 | 1.82 | 2.41 |
| hi_09_degraded.jpg | 30 | 8.6% | 9.5% | 70.4% | 0.67 | 1.62 | 2.31 |
| kn_00_clean.png | 32 | 38.1% | 33.1% | 0.0% | 0.91 | 0.87 | 1.82 |
| kn_00_degraded.jpg | 29 | 39.4% | 34.9% | 0.0% | 0.76 | 0.66 | 1.46 |
| kn_01_clean.png | 33 | 35.5% | 29.6% | 0.0% | 0.68 | 0.78 | 1.50 |
| kn_01_degraded.jpg | 31 | 38.1% | 32.3% | 0.0% | 0.81 | 0.64 | 1.50 |
| kn_02_clean.png | 31 | 34.4% | 28.4% | 0.0% | 0.75 | 0.82 | 1.60 |
| kn_02_degraded.jpg | 31 | 40.4% | 36.3% | 0.0% | 1.01 | 0.86 | 1.92 |
| kn_03_clean.png | 25 | 37.2% | 31.8% | 0.0% | 0.69 | 0.48 | 1.21 |
| kn_03_degraded.jpg | 31 | 38.6% | 33.6% | 0.4% | 0.88 | 0.58 | 1.51 |
| kn_04_clean.png | 29 | 36.1% | 30.6% | 0.0% | 0.71 | 0.69 | 1.44 |
| kn_04_degraded.jpg | 27 | 38.5% | 33.4% | 0.4% | 0.92 | 0.51 | 1.48 |
| kn_05_clean.png | 30 | 36.1% | 30.1% | 0.0% | 0.72 | 0.65 | 1.39 |
| kn_05_degraded.jpg | 26 | 88.8% | 93.2% | 0.0% | 0.72 | 0.45 | 1.19 |
| kn_06_clean.png | 31 | 37.0% | 31.5% | 0.0% | 0.66 | 0.81 | 1.49 |
| kn_06_degraded.jpg | 30 | 43.5% | 39.0% | 0.0% | 0.57 | 0.51 | 1.10 |
| kn_07_clean.png | 27 | 36.6% | 30.6% | 0.0% | 0.57 | 0.58 | 1.17 |
| kn_07_degraded.jpg | 34 | 85.2% | 89.9% | 0.0% | 0.60 | 0.58 | 1.20 |
| kn_08_clean.png | 31 | 34.5% | 28.2% | 0.0% | 0.58 | 0.60 | 1.20 |
| kn_08_degraded.jpg | 28 | 89.1% | 94.8% | 0.0% | 0.65 | 0.50 | 1.17 |
| kn_09_clean.png | 27 | 35.9% | 30.2% | 0.0% | 0.62 | 0.61 | 1.25 |
| kn_09_degraded.jpg | 28 | 56.8% | 57.4% | 0.0% | 0.66 | 0.45 | 1.13 |
| ta_00_clean.png | 31 | 1.1% | 1.2% | 89.7% | 0.94 | 1.61 | 2.60 |
| ta_00_degraded.jpg | 36 | 0.5% | 0.5% | 95.4% | 0.69 | 1.71 | 2.45 |
| ta_01_clean.png | 31 | 0.8% | 0.9% | 92.9% | 0.67 | 1.51 | 2.22 |
| ta_01_degraded.jpg | 33 | 0.6% | 0.6% | 94.5% | 0.69 | 1.39 | 2.13 |
| ta_02_clean.png | 34 | 1.1% | 1.2% | 90.4% | 0.73 | 1.86 | 2.64 |
| ta_02_degraded.jpg | 29 | 0.6% | 0.7% | 94.5% | 0.72 | 1.29 | 2.05 |
| ta_03_clean.png | 29 | 0.7% | 0.8% | 94.8% | 0.80 | 1.44 | 2.29 |
| ta_03_degraded.jpg | 36 | 0.5% | 0.5% | 95.9% | 0.76 | 1.77 | 2.57 |
| ta_04_clean.png | 29 | 0.6% | 0.7% | 94.7% | 0.77 | 1.59 | 2.42 |
| ta_04_degraded.jpg | 32 | 0.8% | 0.9% | 92.9% | 0.74 | 1.49 | 2.29 |
| ta_05_clean.png | 27 | 0.8% | 0.9% | 93.0% | 0.69 | 1.26 | 1.97 |
| ta_05_degraded.jpg | 32 | 1.4% | 1.5% | 89.5% | 0.65 | 1.68 | 2.35 |
| ta_06_clean.png | 31 | 0.6% | 0.7% | 94.1% | 0.60 | 1.52 | 2.14 |
| ta_06_degraded.jpg | 35 | 0.4% | 0.5% | 96.1% | 0.61 | 1.43 | 2.06 |
| ta_07_clean.png | 29 | 1.0% | 1.1% | 91.8% | 0.60 | 1.13 | 1.75 |
| ta_07_degraded.jpg | 28 | 0.7% | 0.8% | 93.6% | 0.59 | 0.91 | 1.52 |
| ta_08_clean.png | 33 | 0.6% | 0.7% | 94.4% | 0.58 | 1.51 | 2.10 |
| ta_08_degraded.jpg | 31 | 0.4% | 0.4% | 97.1% | 0.56 | 1.47 | 2.05 |
| ta_09_clean.png | 30 | 0.7% | 0.8% | 93.2% | 0.68 | 1.57 | 2.27 |
| ta_09_degraded.jpg | 34 | 0.5% | 0.5% | 95.8% | 0.62 | 1.52 | 2.16 |
| te_00_clean.png | 27 | 0.6% | 0.6% | 94.7% | 0.73 | 1.00 | 1.77 |
| te_00_degraded.jpg | 27 | 1.7% | 1.9% | 86.8% | 0.66 | 0.98 | 1.67 |
| te_01_clean.png | 30 | 1.1% | 1.2% | 92.6% | 0.65 | 1.23 | 1.92 |
| te_01_degraded.jpg | 32 | 1.0% | 1.1% | 94.7% | 0.78 | 1.29 | 2.11 |
| te_02_clean.png | 30 | 0.5% | 0.5% | 96.2% | 0.73 | 1.45 | 2.22 |
| te_02_degraded.jpg | 29 | 0.5% | 0.5% | 96.1% | 0.65 | 1.29 | 1.98 |
| te_03_clean.png | 35 | 0.8% | 0.8% | 94.6% | 0.69 | 1.85 | 2.57 |
| te_03_degraded.jpg | 27 | 0.8% | 0.9% | 94.4% | 0.74 | 1.16 | 1.95 |
| te_04_clean.png | 29 | 0.6% | 0.5% | 94.8% | 0.67 | 1.77 | 2.49 |
| te_04_degraded.jpg | 26 | 0.7% | 0.8% | 95.1% | 0.91 | 1.13 | 2.07 |
| te_05_clean.png | 26 | 0.5% | 0.3% | 95.4% | 0.71 | 1.11 | 1.84 |
| te_05_degraded.jpg | 28 | 2.9% | 3.2% | 78.9% | 0.61 | 0.96 | 1.59 |
| te_06_clean.png | 34 | 0.6% | 0.5% | 94.4% | 0.61 | 1.85 | 2.48 |
| te_06_degraded.jpg | 34 | 3.4% | 3.8% | 76.1% | 0.64 | 1.38 | 2.05 |
| te_07_clean.png | 29 | 0.8% | 0.8% | 94.8% | 0.60 | 1.20 | 1.81 |
| te_07_degraded.jpg | 30 | 8.6% | 9.7% | 52.0% | 0.68 | 1.04 | 1.75 |
| te_08_clean.png | 31 | 0.8% | 0.8% | 94.8% | 0.75 | 7.75 | 8.52 |
| te_08_degraded.jpg | 30 | 13.2% | 15.0% | 40.2% | 2.07 | 6.13 | 8.25 |
| te_09_clean.png | 28 | 0.6% | 0.5% | 94.0% | 1.46 | 5.85 | 7.33 |
| te_09_degraded.jpg | 27 | 1.5% | 1.7% | 89.1% | 1.09 | 4.09 | 5.23 |
| tem_00_clean.png | 34 | 4.2% | 4.3% | 92.7% | 1.91 | 7.15 | 9.13 |
| tem_00_degraded.jpg | 31 | 1.5% | 1.7% | 95.4% | 2.04 | 5.38 | 7.48 |
| tem_01_clean.png | 27 | 0.7% | 0.8% | 96.4% | 1.68 | 5.17 | 6.93 |
| tem_01_degraded.jpg | 27 | 0.9% | 1.1% | 95.2% | 2.04 | 4.31 | 6.46 |
| tem_02_clean.png | 32 | 1.1% | 1.2% | 92.8% | 1.84 | 5.91 | 7.81 |
| tem_02_degraded.jpg | 28 | 1.7% | 2.0% | 92.0% | 1.41 | 4.53 | 6.00 |
| tem_03_clean.png | 29 | 0.8% | 1.0% | 95.6% | 1.55 | 4.89 | 6.49 |
| tem_03_degraded.jpg | 32 | 1.1% | 1.2% | 94.5% | 1.79 | 5.61 | 7.45 |
| tem_04_clean.png | 32 | 0.8% | 0.9% | 96.0% | 2.12 | 5.64 | 7.83 |
| tem_04_degraded.jpg | 27 | 1.5% | 1.7% | 93.0% | 1.85 | 4.46 | 6.38 |
