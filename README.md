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

Models are not committed to the repository. Build them with the preparation script:

```bash
tools/models/prepare_models.sh ./models-work ./models
```

Put the output in `models/` next to the executable, or in `%LOCALAPPDATA%\IndicOcr\models`.

## CLI quick start

```bash
dotnet run --project src/IndicOcr.Cli -- ocr page.png --models models --lang te+en --format txt,docx,pdf
```

## Benchmark

Accuracy and speed are measured in `bench/`. See [bench/README.md](bench/README.md).

## Product goal

The product scope, target users and success metrics are in [docs/product-goal.md](docs/product-goal.md).

## Third-party notices

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
