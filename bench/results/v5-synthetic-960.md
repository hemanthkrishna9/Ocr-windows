# OCR benchmark report

- Data: `data/synthetic`  |  languages: per-page meta  |  det max side: 960
- Machine: Intel(R) Xeon(R) Processor @ 2.10GHz, nproc=4, ORT intra-op threads used: **4**, run-all-recognisers: False, onnxruntime 1.31.0, Python 3.12.3
- System 1-min load average at start/end: 7.4 / 5.2 (other processes on the machine inflate timings when this exceeds ~1)
- Models: `/tmp/claude-0/m/models_final`
- Model load + warm-up (excluded from per-page time): **0.84 s** (det 0.10s, rec_devanagari 0.12s, rec_en 0.13s, rec_ka 0.11s, rec_ta 0.10s, rec_te 0.11s)
- Per-page seconds = detect + crop + recognise (all requested recognisers) after image decode; decode time reported in JSON.

## Aggregate

| Group | Pages | CER | CER no-space | Word acc | s/page mean | median | p95 | max |
|---|---|---|---|---|---|---|---|---|
| **ALL** | 90 | 11.60% | 11.08% | 72.6% | 2.15 | 1.52 | 7.08 | 9.51 |
| hi/clean | 10 | 1.10% | 1.34% | 95.2% | 5.42 | 6.82 | 9.51 | 9.51 |
| hi/degraded | 10 | 6.86% | 7.11% | 76.6% | 4.26 | 4.80 | 8.30 | 8.30 |
| kn/clean | 10 | 36.46% | 30.67% | 0.0% | 0.95 | 0.92 | 1.19 | 1.19 |
| kn/degraded | 10 | 53.76% | 51.43% | 0.0% | 0.76 | 0.77 | 0.91 | 0.91 |
| ta/clean | 10 | 0.78% | 0.88% | 93.0% | 1.77 | 1.74 | 2.17 | 2.17 |
| ta/degraded | 10 | 0.54% | 0.61% | 95.4% | 1.55 | 1.55 | 1.85 | 1.85 |
| te/clean | 10 | 1.20% | 1.18% | 93.6% | 1.70 | 1.79 | 2.09 | 2.09 |
| te/degraded | 10 | 4.95% | 5.55% | 75.2% | 1.22 | 1.26 | 1.42 | 1.42 |
| tem/clean | 5 | 1.59% | 1.67% | 94.0% | 1.83 | 1.68 | 2.41 | 2.41 |
| tem/degraded | 5 | 1.63% | 1.82% | 92.7% | 1.54 | 1.49 | 1.83 | 1.83 |

Time split per page (mean): detect 0.23 s (11%), crop 0.04 s, recognise 1.87 s (87%), image decode (not counted) 0.06 s; mean text lines per page 29.8.

CER/word accuracy are micro-averages (total errors / total reference units); word accuracy = 1 - WER; 'CER no-space' ignores all whitespace (the PP-OCRv3 Indic recognisers rarely emit spaces). Text is NFC-normalised and whitespace-collapsed before scoring.

## Per page

| Page | Lines | CER | CER no-space | Word acc | det s | rec s | total s |
|---|---|---|---|---|---|---|---|
| hi_00_clean.png | 24 | 1.2% | 1.5% | 94.6% | 0.72 | 8.69 | 9.51 |
| hi_00_degraded.jpg | 30 | 2.3% | 2.6% | 90.4% | 0.80 | 7.40 | 8.30 |
| hi_01_clean.png | 32 | 1.1% | 1.4% | 95.0% | 0.46 | 8.32 | 8.85 |
| hi_01_degraded.jpg | 30 | 0.9% | 1.1% | 96.3% | 0.54 | 5.98 | 6.59 |
| hi_02_clean.png | 27 | 1.4% | 1.7% | 94.0% | 0.52 | 6.31 | 6.90 |
| hi_02_degraded.jpg | 27 | 2.0% | 2.4% | 92.5% | 0.34 | 4.40 | 4.80 |
| hi_03_clean.png | 28 | 0.6% | 0.8% | 96.9% | 0.60 | 6.57 | 7.24 |
| hi_03_degraded.jpg | 28 | 1.4% | 1.6% | 95.0% | 0.49 | 5.87 | 6.45 |
| hi_04_clean.png | 27 | 1.1% | 1.3% | 94.9% | 0.52 | 5.52 | 6.14 |
| hi_04_degraded.jpg | 29 | 1.7% | 2.0% | 92.7% | 0.46 | 6.54 | 7.08 |
| hi_05_clean.png | 31 | 1.6% | 1.9% | 94.0% | 0.46 | 6.31 | 6.82 |
| hi_05_degraded.jpg | 28 | 5.1% | 5.4% | 79.5% | 0.68 | 3.87 | 4.59 |
| hi_06_clean.png | 31 | 0.9% | 1.0% | 96.2% | 0.60 | 2.15 | 2.79 |
| hi_06_degraded.jpg | 27 | 18.2% | 17.3% | 44.7% | 0.18 | 0.84 | 1.04 |
| hi_07_clean.png | 27 | 1.4% | 1.6% | 94.4% | 0.18 | 1.39 | 1.58 |
| hi_07_degraded.jpg | 28 | 15.4% | 16.9% | 55.7% | 0.18 | 0.87 | 1.06 |
| hi_08_clean.png | 31 | 1.0% | 1.2% | 96.1% | 0.18 | 1.77 | 1.97 |
| hi_08_degraded.jpg | 30 | 7.7% | 8.0% | 70.7% | 0.20 | 1.13 | 1.36 |
| hi_09_clean.png | 33 | 0.8% | 1.0% | 96.1% | 0.18 | 2.19 | 2.39 |
| hi_09_degraded.jpg | 30 | 14.3% | 14.3% | 48.9% | 0.18 | 1.16 | 1.37 |
| kn_00_clean.png | 32 | 38.6% | 33.3% | 0.0% | 0.20 | 0.94 | 1.19 |
| kn_00_degraded.jpg | 29 | 39.9% | 35.5% | 0.0% | 0.18 | 0.56 | 0.78 |
| kn_01_clean.png | 33 | 36.0% | 29.9% | 0.0% | 0.17 | 0.83 | 1.04 |
| kn_01_degraded.jpg | 31 | 39.7% | 33.9% | 0.0% | 0.16 | 0.60 | 0.81 |
| kn_02_clean.png | 31 | 35.2% | 29.2% | 0.0% | 0.18 | 0.82 | 1.05 |
| kn_02_degraded.jpg | 31 | 40.5% | 35.4% | 0.0% | 0.15 | 0.53 | 0.73 |
| kn_03_clean.png | 25 | 36.6% | 31.2% | 0.0% | 0.16 | 0.67 | 0.86 |
| kn_03_degraded.jpg | 31 | 39.2% | 34.1% | 0.0% | 0.18 | 0.68 | 0.91 |
| kn_04_clean.png | 29 | 36.4% | 30.7% | 0.0% | 0.18 | 0.65 | 0.87 |
| kn_04_degraded.jpg | 27 | 39.0% | 34.0% | 0.0% | 0.18 | 0.50 | 0.73 |
| kn_05_clean.png | 30 | 36.6% | 30.5% | 0.0% | 0.18 | 0.70 | 0.90 |
| kn_05_degraded.jpg | 26 | 84.4% | 85.5% | 0.0% | 0.18 | 0.44 | 0.65 |
| kn_06_clean.png | 31 | 37.3% | 31.8% | 0.0% | 0.18 | 0.74 | 0.94 |
| kn_06_degraded.jpg | 30 | 43.1% | 38.5% | 0.0% | 0.17 | 0.57 | 0.77 |
| kn_07_clean.png | 27 | 37.2% | 31.4% | 0.0% | 0.18 | 0.73 | 0.92 |
| kn_07_degraded.jpg | 34 | 78.6% | 81.0% | 0.0% | 0.20 | 0.61 | 0.84 |
| kn_08_clean.png | 31 | 34.6% | 28.3% | 0.0% | 0.18 | 0.68 | 0.89 |
| kn_08_degraded.jpg | 28 | 85.8% | 90.5% | 0.0% | 0.18 | 0.44 | 0.65 |
| kn_09_clean.png | 27 | 35.9% | 30.0% | 0.0% | 0.20 | 0.67 | 0.88 |
| kn_09_degraded.jpg | 28 | 50.0% | 48.9% | 0.5% | 0.22 | 0.48 | 0.72 |
| ta_00_clean.png | 31 | 0.8% | 0.9% | 92.3% | 0.16 | 1.45 | 1.65 |
| ta_00_degraded.jpg | 36 | 0.4% | 0.5% | 96.2% | 0.18 | 1.52 | 1.75 |
| ta_01_clean.png | 31 | 0.8% | 0.9% | 92.4% | 0.18 | 1.93 | 2.16 |
| ta_01_degraded.jpg | 33 | 0.4% | 0.4% | 96.3% | 0.20 | 1.60 | 1.85 |
| ta_02_clean.png | 34 | 0.9% | 1.0% | 91.7% | 0.16 | 1.86 | 2.07 |
| ta_02_degraded.jpg | 29 | 0.7% | 0.7% | 95.1% | 0.18 | 1.27 | 1.50 |
| ta_03_clean.png | 29 | 1.0% | 1.1% | 92.2% | 0.20 | 1.76 | 2.01 |
| ta_03_degraded.jpg | 36 | 0.5% | 0.5% | 95.9% | 0.16 | 1.60 | 1.81 |
| ta_04_clean.png | 29 | 0.6% | 0.7% | 94.7% | 0.17 | 1.33 | 1.55 |
| ta_04_degraded.jpg | 32 | 0.5% | 0.5% | 95.8% | 0.18 | 1.32 | 1.55 |
| ta_05_clean.png | 27 | 1.1% | 1.3% | 90.4% | 0.18 | 1.21 | 1.42 |
| ta_05_degraded.jpg | 32 | 1.7% | 1.9% | 87.3% | 0.20 | 1.09 | 1.32 |
| ta_06_clean.png | 31 | 0.5% | 0.6% | 95.5% | 0.18 | 1.32 | 1.52 |
| ta_06_degraded.jpg | 35 | 0.3% | 0.4% | 96.9% | 0.18 | 1.33 | 1.53 |
| ta_07_clean.png | 29 | 0.9% | 1.0% | 91.8% | 0.18 | 1.15 | 1.36 |
| ta_07_degraded.jpg | 28 | 0.5% | 0.6% | 94.9% | 0.17 | 0.99 | 1.18 |
| ta_08_clean.png | 33 | 0.7% | 0.8% | 94.0% | 0.18 | 1.97 | 2.17 |
| ta_08_degraded.jpg | 31 | 0.1% | 0.1% | 99.0% | 0.21 | 1.18 | 1.40 |
| ta_09_clean.png | 30 | 0.6% | 0.7% | 94.4% | 0.18 | 1.55 | 1.74 |
| ta_09_degraded.jpg | 34 | 0.4% | 0.5% | 96.2% | 0.21 | 1.34 | 1.57 |
| te_00_clean.png | 27 | 1.0% | 0.9% | 91.6% | 0.17 | 1.14 | 1.36 |
| te_00_degraded.jpg | 27 | 2.0% | 2.3% | 85.9% | 0.19 | 1.08 | 1.32 |
| te_01_clean.png | 30 | 1.1% | 1.3% | 93.6% | 0.19 | 1.68 | 1.91 |
| te_01_degraded.jpg | 32 | 1.4% | 1.6% | 91.6% | 0.17 | 1.19 | 1.42 |
| te_02_clean.png | 30 | 0.8% | 0.8% | 94.4% | 0.18 | 1.56 | 1.79 |
| te_02_degraded.jpg | 29 | 0.6% | 0.7% | 95.4% | 0.16 | 1.21 | 1.41 |
| te_03_clean.png | 34 | 0.8% | 0.9% | 95.9% | 0.17 | 1.87 | 2.09 |
| te_03_degraded.jpg | 27 | 1.1% | 1.3% | 93.2% | 0.20 | 1.01 | 1.26 |
| te_04_clean.png | 29 | 0.8% | 0.5% | 92.5% | 0.19 | 1.38 | 1.61 |
| te_04_degraded.jpg | 26 | 1.8% | 2.0% | 87.2% | 0.21 | 0.89 | 1.15 |
| te_05_clean.png | 26 | 0.5% | 0.5% | 95.4% | 0.18 | 1.03 | 1.22 |
| te_05_degraded.jpg | 28 | 3.0% | 3.2% | 76.8% | 0.18 | 0.94 | 1.14 |
| te_06_clean.png | 34 | 0.6% | 0.6% | 93.7% | 0.18 | 1.64 | 1.85 |
| te_06_degraded.jpg | 34 | 4.3% | 4.9% | 71.5% | 0.16 | 1.13 | 1.31 |
| te_07_clean.png | 29 | 0.8% | 0.8% | 94.5% | 0.16 | 1.43 | 1.60 |
| te_07_degraded.jpg | 30 | 12.3% | 14.0% | 46.6% | 0.19 | 0.91 | 1.12 |
| te_08_clean.png | 30 | 4.6% | 4.6% | 89.4% | 0.21 | 1.47 | 1.71 |
| te_08_degraded.jpg | 30 | 17.5% | 19.6% | 32.7% | 0.16 | 0.82 | 1.00 |
| te_09_clean.png | 28 | 0.4% | 0.3% | 95.3% | 0.20 | 1.65 | 1.86 |
| te_09_degraded.jpg | 27 | 1.8% | 2.1% | 87.4% | 0.16 | 0.85 | 1.03 |
| tem_00_clean.png | 35 | 0.8% | 0.9% | 94.7% | 0.16 | 1.64 | 1.84 |
| tem_00_degraded.jpg | 31 | 1.6% | 1.8% | 95.4% | 0.17 | 1.61 | 1.83 |
| tem_01_clean.png | 27 | 0.7% | 0.8% | 95.3% | 0.19 | 1.39 | 1.63 |
| tem_01_degraded.jpg | 27 | 1.3% | 1.4% | 93.1% | 0.18 | 1.25 | 1.49 |
| tem_02_clean.png | 32 | 1.1% | 1.0% | 92.6% | 0.22 | 2.14 | 2.41 |
| tem_02_degraded.jpg | 28 | 2.2% | 2.5% | 90.0% | 0.19 | 1.23 | 1.48 |
| tem_03_clean.png | 29 | 0.8% | 0.9% | 95.6% | 0.19 | 1.39 | 1.62 |
| tem_03_degraded.jpg | 32 | 1.4% | 1.6% | 92.9% | 0.18 | 1.50 | 1.72 |
| tem_04_clean.png | 31 | 4.5% | 4.6% | 92.3% | 0.17 | 1.46 | 1.68 |
| tem_04_degraded.jpg | 27 | 1.7% | 1.8% | 91.2% | 0.17 | 0.97 | 1.18 |
