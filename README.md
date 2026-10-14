# Offline Indic OCR for Windows

Offline OCR for Telugu, Hindi, Tamil, Kannada and other Indian-language scans, photos and PDFs, mixed with English. It produces editable text, searchable PDFs and Word files.

Nothing is uploaded. Every page is processed on your PC, and the app makes no network calls after the models are in place.

## Repository layout

| Path | Contents |
| --- | --- |
| `src/IndicOcr.Core` | C# OCR engine (ONNX Runtime, PaddleOCR models) |
| `src/IndicOcr.Cli` | Command-line tool |
| `tests/` | Unit and integration tests |
| `app/IndicOcr.App` | WinUI 3 desktop app |
| `bench/` | Python accuracy and speed benchmark |
| `tools/models/` | Scripts that prepare the ONNX models |
| `docs/` | Product goal and design notes |

## Getting the models

Models are not committed to the repository. Build them with the preparation script (Linux or macOS with `python3.11`, `curl` and `unzip`; about 2 minutes):

```bash
tools/models/prepare_models.sh ./models-work ./models
```

Put the output in `models/` next to the executable, or in `%LOCALAPPDATA%\IndicOcr\models`.

What you get:

- Detector: PP-OCRv5 mobile (`det.onnx`).
- Recognizers: PP-OCRv5 mobile for Telugu, Hindi/Marathi (Devanagari), Tamil and English.
- Kannada (beta): PP-OCRv3 recognizer. No PP-OCRv5 Kannada model exists, so expect many more errors than for the other languages. The app labels it "Kannada (beta)".
- All models are Apache-2.0 from PaddlePaddle, obtained through the `Sdcb.PaddleOCR.Models.LocalV5` 3.3.1 and `LocalV3` 2.7.0.1 NuGet packages (read as data, never run).

The script is reproducible: every pip package is pinned (`tools/models/requirements-*.txt`), every download is verified by SHA-256, and the outputs are compared with `tools/models/models.sha256`.

### How CI gets the models

The `models` job in `.github/workflows/build.yml` runs the same script on Ubuntu with Python 3.11, caches the result (key: hash of `tools/models/`), checks it against `models.sha256` and uploads it as an artifact. The `core-linux` and `windows` jobs download that artifact and set `INDICOCR_MODELS`, so every model-backed integration test runs in CI instead of being skipped. The Windows job also sets `INDICOCR_TEST_FONTS` to the bundled Noto fonts.

## CLI quick start

```bash
dotnet run --project src/IndicOcr.Cli -- ocr page.png --models models --lang te+en --format txt,docx,pdf
```

`ocr` takes a file or a folder (`--recursive` for sub-folders). Outputs go next to each input, or under `--out-dir` (sub-folders of a folder input are kept):

| Format | Output file |
| --- | --- |
| `txt` | `<name>.txt` |
| `docx` | `<name>.docx` |
| `pdf` (searchable) | `<name>.ocr.pdf` |

The input is never overwritten: the command refuses to run if any output path equals an input path or another output. If two inputs in one folder share a base name (`multi.tif` and `multi.pdf`), the source extension is added to both: `multi.tif.txt`, `multi.pdf.txt`, `multi.tif.ocr.pdf`.

A file that cannot be processed is reported as `FAILED <file>: <reason>` on stderr and the rest of the batch continues. Exit codes: 0 all good, 1 at least one file failed (or no input found), 2 bad arguments. Kannada (`--lang kn`) is beta quality.

## Benchmark

Accuracy and speed are measured in `bench/`. See [bench/README.md](bench/README.md).

## Product goal

The product scope, target users and success metrics are in [docs/product-goal.md](docs/product-goal.md).

## Third-party notices

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
