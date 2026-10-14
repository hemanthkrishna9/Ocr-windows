# Third-party notices

This file lists the components the app and its tools depend on, with their licences.

**Before release, re-check this list against the actual package references** (`grep PackageReference src/*/*.csproj app/*/*.csproj`, `tools/models/requirements-*.txt`, `bench/`, and the model sources).

## Models

All shipped models are published by PaddlePaddle / PaddleOCR under the Apache-2.0 licence and converted to ONNX by `tools/models/prepare_models.sh`.

| Model | Used for | Licence |
| --- | --- | --- |
| PP-OCRv5 mobile detector (`mobile_zh_det`) | Text detection for all languages (`det.onnx`) | Apache-2.0 |
| PP-OCRv5 mobile recognizers: Telugu, Devanagari (Hindi/Marathi), Tamil, English | `rec_te`, `rec_hi`, `rec_ta`, `rec_en` | Apache-2.0 |
| PP-OCRv3 Kannada recognizer (`ka_PP_OCRv3_rec`), beta | `rec_kn` (no PP-OCRv5 Kannada model exists; older model, many errors) | Apache-2.0 |
| Character dictionaries | v5: read from the model `.yml`; Kannada: `ka_dict.txt` from the `paddleocr` 2.9.1 wheel | Apache-2.0 |

Source of the model files: the NuGet packages `Sdcb.PaddleOCR.Models.LocalV5` 3.3.1 and `Sdcb.PaddleOCR.Models.LocalV3` 2.7.0.1 (Apache-2.0), which repackage the official PaddleOCR models. Their embedded resources are read as data by `tools/models/prepare_models.sh` (checked by SHA-256). The DLLs are never loaded, shipped or run, and the packages are not referenced by any project.

The conversion tools (paddlepaddle, paddle2onnx, onnx, onnxruntime, dnfile, PyYAML and their dependencies, pinned in `tools/models/requirements-*.txt`) run only at build time and are not distributed.

## Fonts

| Component | Licence |
| --- | --- |
| Noto Sans, Noto Sans Devanagari / Tamil / Telugu / Kannada (bundled in `app/IndicOcr.App/fonts`, used for the searchable PDF text layer) | SIL OFL 1.1, full text in `app/IndicOcr.App/fonts/OFL.txt` |

## .NET packages

Direct `PackageReference`s in `src/` and `app/` (and notable transitive ones).

| Package | Version | Project | Licence |
| --- | --- | --- | --- |
| Microsoft.ML.OnnxRuntime | 1.31.0 | Core | MIT |
| SkiaSharp | 4.153.1 | Core | MIT |
| SkiaSharp.NativeAssets.Linux.NoDependencies | 4.153.1 | Core | MIT |
| PDFtoImage | 5.4.0 | Core | MIT |
| bblanchon.PDFium.* (transitive, PDFium binaries) | 152.0.7961 | Core | Apache-2.0 wrapper; PDFium itself is BSD-3-Clause / Apache-2.0 |
| PDFsharp | 6.2.4 | Core | MIT. **Keep pinned at 6.2.4. PDFsharp 6.5 and later are AGPL-licensed and must not be adopted.** |
| DocumentFormat.OpenXml | 3.5.1 | Core | MIT |
| BitMiracle.LibTiff.NET | 2.4.660 | Core | BSD-3-Clause per the vendor (https://bitmiracle.github.io/libtiff.net/help/articles/license.html). **Confirm before release.** |
| Microsoft.WindowsAppSDK | 1.6.250108002 | App | MIT (Microsoft redistributable runtime components) |
| Microsoft.Windows.SDK.BuildTools | 10.0.26100.1742 | App | Microsoft software licence; build-time only, not distributed |
| System.IO.Packaging, System.Memory, System.Numerics.Tensors, System.Security.Cryptography.Pkcs, Microsoft.Extensions.*.Abstractions (transitive) | see `project.assets.json` | Core | MIT |

`src/IndicOcr.Cli` adds no packages. The test project (not shipped) uses xunit 2.4.2 (Apache-2.0), Microsoft.NET.Test.Sdk 17.6.0 (MIT), coverlet.collector 6.0.0 (MIT), SkiaSharp.HarfBuzz 4.153.1 and HarfBuzzSharp.NativeAssets.Linux 14.2.1.301 (MIT).

## Python benchmark (`bench/`)

| Component | Licence |
| --- | --- |
| onnxruntime | MIT |
| numpy | BSD-3-Clause |
| opencv-python-headless | Apache-2.0 |
| Pillow | MIT-CMU (HPND) |
| pypdfium2 | Apache-2.0 / BSD-3-Clause |
