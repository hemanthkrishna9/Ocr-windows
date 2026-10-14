# Third-party notices

This file lists the components the app and its tools depend on, with their licences.

**Before release, re-check this list against the actual package references** (`*.csproj`, `requirements.txt`, and the model sources).

## Models

| Component | Role | Licence |
| --- | --- | --- |
| PaddleOCR PP-OCRv3 models (det and rec) | OCR engine models, converted to ONNX | Apache-2.0 |
| Sdcb.PaddleOCR.Models.LocalV3 | Model source only; its embedded model files are read as data by `tools/models/prepare_models.sh`. The DLL is not shipped or run. | Apache-2.0 |
| Noto fonts | Fonts for PDF and document output | SIL OFL 1.1 |

## .NET application

| Component | Licence |
| --- | --- |
| ONNX Runtime | MIT |
| SkiaSharp | MIT |
| HarfBuzzSharp | MIT |
| PDFtoImage | MIT |
| PDFium | BSD-3-Clause / Apache-2.0 |
| PDFsharp | MIT |
| DocumentFormat.OpenXml | MIT |
| Windows App SDK | MIT |
| CommunityToolkit.Mvvm | MIT |

## Python benchmark (`bench/`)

| Component | Licence |
| --- | --- |
| onnxruntime | MIT |
| numpy | BSD-3-Clause |
| opencv-python-headless | Apache-2.0 |
| Pillow | MIT-CMU (HPND) |
| pypdfium2 | Apache-2.0 / BSD-3-Clause |
