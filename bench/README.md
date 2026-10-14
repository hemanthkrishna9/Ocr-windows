# Phase 0 benchmark: accuracy and speed of PaddleOCR PP-OCRv3 on ONNX Runtime

Measures word accuracy, character error rate and seconds per A4 page for the v1 engine (one detector plus
one recogniser per language, CPU only) before any app work. Pure Python, no network access.

## Setup

```
cd bench
python3.12 -m venv .venv
.venv/bin/pip install -r requirements.txt
export INDICOCR_MODELS=/path/to/models      # or pass --models DIR each time
```

Two model folder layouts are detected automatically:

- v5 (`det.onnx`, `rec_{te,hi,ta,kn,en}.onnx`, `dict_{te,hi,ta,kn,en}.txt`): PP-OCRv5 mobile.
- v3 (`en_PP_OCRv3_det.onnx`, `{te,devanagari,ta,ka,en}_PP_OCRv3_rec.onnx`, `{...}_dict.txt`).

Language codes: `te`, `hi`/`mr` (Devanagari), `ta`, `kn`, `en`. With `te+en`, if the Indic dictionary already
contains A-Z, a-z and 0-9 (v5), only the Indic recogniser runs; `--run-all-recs` also runs the English one and keeps
the higher-confidence result per line.

## Dataset layout

```
bench/data/<set>/<name>.png | .jpg | .pdf     page image or PDF (PDF: all pages are OCRed, text concatenated)
bench/data/<set>/<name>.txt                   ground truth, UTF-8, the text of the page (one printed line per line)
bench/data/<set>/meta.json                    optional, see below
```

`meta.json` (optional):

```json
{ "langs": ["te", "en"],
  "pages": { "deed_01": { "langs": ["te"] } } }
```

Per-page `langs` wins over the set-wide `langs`; `--langs` on the command line wins over both.
Pages without a `.txt` are skipped with a warning. Scoring is Unicode NFC and whitespace normalised, so line
breaks versus spaces do not matter.

## Adding your 50 real pages (they stay private)

`bench/data/real*/` is git-ignored: nothing in there is ever committed.

1. `mkdir bench/data/real` (or `real-telugu`, `real-legal`, ...; any name starting with `real`).
2. Copy each page in as `p01.jpg` (or `.png` / `.pdf`). Use the scan as it comes from your scanner or phone.
3. For each page create `p01.txt` with the correct text, typed or corrected by hand. Quick way: run the OCR
   once (`--dump-text`, step 5), copy `out/real-text/p01.ocr.txt` to `bench/data/real/p01.txt` and fix every
   error by eye. Do this carefully; the ground truth defines the score.
4. Optional `bench/data/real/meta.json` with `{"langs": ["te", "en"]}` (or pass `--langs te+en`). Put a page
   in `pages` if its languages differ from the set default.
5. Run:
   ```
   .venv/bin/python -m indicocr_bench.run --data data/real --langs te+en \
       --report results/real-baseline.md --dump-text out/real-text
   ```
   (`results/real-baseline.*` is committable only if you are happy to publish the per-page numbers; the
   file names leak nothing but you may prefer `out/`, which is ignored.)
6. Read the aggregate table first. The v1 target is word accuracy of 90% or better on clean printed Telugu and under
   2 s per page. Then open the worst pages (per-page table) and compare `out/real-text/<name>.ocr.txt` to your `.txt`.

## Running

```
.venv/bin/python -m indicocr_bench.run --models DIR --data data/synthetic --max-side 1920 --report results/x.md
```

Options: `--langs te+en` (every listed recogniser runs on every line, highest confidence wins: the v1 router),
`--max-side N` (detector long side, rounded to multiples of 32; try 960/1280/1920), `--threads N`
(ONNX Runtime intra-op threads, default all cores), `--rec-batch N`, `--pdf-dpi 200`, `--limit N`,
`--json path` (default: next to `--report`), `--dump-text DIR`.

The report lists per-page and aggregate CER, "CER no-space", word accuracy (= 1 - WER), seconds per page
(detect + crop + recognise; image decode excluded and listed in JSON), model load time separately, threads and nproc.
Timings are only meaningful on an otherwise idle machine; the report prints the system load average.

## Synthetic pages

```
.venv/bin/python -m indicocr_bench.synth --out data/synthetic --per-lang 10 --mixed 5 --seed 1234
```

Renders A4 pages (2480x3508 at 300 dpi; the second half of each language at 150 dpi) with Noto Sans
Telugu/Devanagari/Tamil/Kannada (+ Noto Sans for English, via fontconfig; fails clearly if missing), text
from a small built-in corpus (`corpus.py`, original sentences, a few hundred words per language), as a
clean PNG and a degraded JPEG (rotation up to 1.5 degrees, blur, noise, low contrast, JPEG quality 55).
`tem_*` pages are Telugu with English words and numbers mixed in. Synthetic accuracy is an optimistic upper
bound (one font family, a small vocabulary, perfect layout): the real target is the 50 real pages.

## Tests

```
INDICOCR_MODELS=/path/to/models .venv/bin/python -m pytest -q
```

The end-to-end test is skipped when the models are missing.

## Known issues found while building this

- (v3 folder only) The supplied `en_dict.txt` (63 lines) does not match `en_PP_OCRv3_rec.onnx` (97 classes). The code falls back
  to PaddleOCR's standard English dictionary order, verified against the model. Check your copy of the dict.
- (v3) The Indic recognisers almost never output the space character and Telugu loses viramas/matras
  (CER about 0.35 on clean synthetic lines); the Devanagari model emits the short-i matra before the consonant
  (visual order). PP-OCRv5 fixes most of this. See `results/synthetic-baseline.md`.
