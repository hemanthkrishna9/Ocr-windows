# Synthetic baseline (Phase 0)

Synthetic A4 pages, 90 pages: te, hi, ta, kn x 10 (5 at 300 dpi, 5 at 150 dpi) x clean PNG / degraded JPEG, plus
5 te+en mixed x clean / degraded. Seed 1234. Generator: `python -m indicocr_bench.synth --out data/synthetic --per-lang 10 --mixed 5 --seed 1234`.
Machine: 4 vCPU (nproc=4) Intel Xeon @ 2.1 GHz, ONNX Runtime 1.31 CPU, 4 intra-op threads, Python 3.12.

**Synthetic accuracy is an upper bound, not the real target**: one font family, ~200-word vocabulary per language, perfect layout.
The real gate is the 50 real pages.

**Timing caveat.** These runs shared the machine with other agents' builds/tests (1-min load average 5 to 7 on 4 cores),
so wall-clock seconds are inflated and noisy (a 1280 run came out slower than a 1920 run). The numbers below that
matter are the per-page CPU time (immune to queueing, divided by 4 threads = idle-machine estimate) and the det/rec split.
Re-run on an idle machine before quoting any speed figure.

Commands (v5 folder `/tmp/claude-0/m/models_final`, v3 folder `/tmp/claude-0/m/models`):
```
for ms in 1920 1280 960; do
  python -m indicocr_bench.run --models <dir> --data data/synthetic --max-side $ms --report results/<v>-synthetic-$ms.md
done
python -m indicocr_bench.run --models models_final --data data/synthetic-tem --max-side 1280 [--run-all-recs]
python -m indicocr_bench.run --models models_final --data data/synthetic-timing --max-side {1920,1280,960}   # 6 dense pages
```
Full per-page tables and JSON: `results/v5-synthetic-*.md|json`, `results/v3-synthetic-*.md|json`.

## Accuracy: PP-OCRv5 (te, hi, ta, en) vs PP-OCRv3, det max side 1920

CER = character error rate; word acc = 1 - WER (spaces matter). Kannada is PP-OCRv3 in both columns.

| Group | v3 CER | v3 word acc | v5 CER | v5 word acc |
|---|---|---|---|---|
| te clean | 35.6% | 0.6% | **0.7%** | **94.6%** |
| te degraded | 59.9% | 0.2% | 3.7% | 78.9% |
| te+en clean | 32.8% | 45.9% | **1.7%** | **94.5%** |
| te+en degraded | 37.7% | 36.7% | 1.3% | 94.1% |
| hi clean | 34.4% | 0.0% | 1.1% | 95.3% |
| hi degraded | 61.9% | 0.0% | 4.0% | 86.1% |
| ta clean | 30.2% | 0.0% | 0.8% | 92.9% |
| ta degraded | 49.7% | 0.0% | 0.6% | 94.6% |
| kn clean (v3) | 36.4% | 0.0% | 36.1% | 0.0% |
| kn degraded (v3) | 60.7% | 0.0% | 55.6% | 0.1% |
| ALL (90) | 45.1% | 4.3% | 11.2% | 74.8% (kn drags it; without kn about 2% CER) |

Notes: v3 numbers used the corrupted/mismatched `en_dict.txt` fallback (see problems) for te+en.
v5 te+en used the Indic recogniser only. Running the English recogniser as well changed nothing useful:
te+en CER 3.1% vs 2.5% Indic-only (1280, 10 pages), word acc 92.9% vs 93.2%, at +1.6x recognise time
(6.5 s -> 11.0 s under load). Recommendation: Indic-only for te+en (dicts already contain A-Z a-z 0-9).

## Detector size sweep (PP-OCRv5, all 90 pages; v5 accuracy ALL / without Kannada hardly changes)

| --max-side | CER ALL | word acc ALL | te clean CER | hi degraded CER | wall s/page (loaded box) |
|---|---|---|---|---|---|
| 1920 | 11.18% | 74.8% | 0.69% | 3.97% | 2.75 mean (noisy) |
| 1280 | 11.40% | 73.4% | 0.79% | 6.10% | 5.53 mean (noisy) |
| 960 | 11.60% | 72.6% | 1.20% | 6.86% | 2.15 mean (noisy) |

Lower detector resolution costs a little accuracy (degraded Hindi most) and saves detector time only; the
recogniser time is fixed by text size, so lower max-side helps less than hoped.

## Speed: where the time goes (6 dense 300-dpi te/tem/hi/ta pages, v5, 4 threads, `results/v5-timing-*.md`)

| --max-side | detect | recognise | CPU time per page | idle-machine estimate (CPU/4) | wall (loaded) |
|---|---|---|---|---|---|
| 1920 | 1.75 s (29%) | 4.21 s (70%) | 13.3 s | ~3.3 s | 6.0 s |
| 1280 | 0.56 s (11%) | 4.38 s (88%) | 12.4 s | ~3.1 s | 5.0 s |
| 960 | 0.38 s (11%) | 3.13 s (88%) | 9.9 s | ~2.5 s | 3.6 s |

About 29 text lines per A4 page, each a 1500-2000 px wide recogniser input; the PP-OCRv5 recogniser is ~85-90% of
the time. Honest conclusion: the **<2 s/page target is NOT demonstrated on this 4-core machine** for dense
full-text A4 pages with the PP-OCRv5 recognisers (estimate 2.5-3.3 s idle). Sparser pages (less text) are faster
(the 150-dpi/short-line Tamil/Telugu pages ran 1.2-2.2 s even under load 5). PP-OCRv3 recognisers were faster
(Kannada v3: 0.8-1.4 s/page) but unusable for te/hi/ta. Levers not yet tried: int8 quantisation of rec, batching by width
buckets, 960 detector, dropping the line-width cap, OpenVINO/DirectML.

## Problems found

1. **v3 folder `en_dict.txt` is wrong** (63 lines, no punctuation) for `en_PP_OCRv3_rec.onnx` (97 classes). The loader falls back to PaddleOCR's standard English dictionary order (verified against the model).
2. **v3 Indic recognisers are poor**: Telugu drops viramas/matras (`పిల్లల` -> `పలల`), almost never emits spaces (word accuracy ~0%), Hindi emits the short-i matra before the consonant (`किया` -> `िकया`, visual order), and Hindi conjuncts (`स्त`) are lost.
3. `te_dict.txt` (v3) starts with junk entries (`te_img/5ILSVRC201val3489.jp...`); every character of the synthetic corpora is present in every dict (only a ZWNJ is missing from Kannada).
4. **Kannada (v3) is the weak link**: CER 36% clean / 55-60% degraded. Needs a v5 kn model.
5. Degradation (blur + JPEG + low contrast + noise) costs v5 Telugu/Hindi 3 to 5 points of CER; Tamil is robust.
6. Speed target not met on dense pages (see above). Timing numbers need an idle machine.
