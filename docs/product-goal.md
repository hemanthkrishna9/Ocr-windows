# Offline Indic OCR for Windows — Product Goal

Oct 14, 2026 · @SMH

## Vision

**Goal:** the fastest, most accurate offline OCR for Indian-language documents on Windows. Every page stays on the user's PC; nothing is uploaded.

It turns scans, photos and PDFs in Telugu, Hindi, Tamil, Kannada and other Indian languages, mixed with English, into editable text, searchable PDFs and Word files. It does this in seconds on an ordinary laptop with no GPU.

**Working name:** to be decided under the NxtDuo brand.

## The gap on Windows today

Yes, the gap is real. No modern, fast, offline Windows app reads Indian-language scans well. The good Indic OCR is in the cloud, and the offline options are old, slow or Latin-only.

| Option | Indian languages | Offline | The problem |
| --- | --- | --- | --- |
| Windows built-in OCR (Snipping Tool, PowerToys, `Windows.Media.Ocr`) | Effectively none; built around about 25 mostly European and East Asian languages ([Windows blog](https://blogs.windows.com/windowsdeveloper/2016/02/08/optical-character-recognition-ocr-for-windows-)) | Yes | Cannot read Telugu, Tamil, Kannada and others |
| Copilot+ PC AI features | Optimised for 6 languages: English, Chinese, French, German, Japanese, Spanish ([Copilot+ notes](https://www.allithypermarket.com.my/pages/copilot-plus-pcs)) | Yes | Needs NPU hardware; no Indic |
| ABBYY, IRIS, Kofax | Do not support Indian languages ([SimpleOCR](https://www.simpleocr.com/?p=3771)) | Yes | Expensive and no Indic |
| [e-Aksharayan](https://tdil-dc.in/eocr/index.html) (MeitY / C-DAC) | 7: Hindi, Bangla, Malayalam, Gurmukhi, Tamil, Kannada, Assamese. No Telugu | Yes | 45 to 60 s per A4 page; free version caps at 5 pages; dated UI |
| Tesseract-based desktop tools | 8 or so Indic languages | Yes | Weakest accuracy on Indic conjuncts |
| Google Lens, Drive, Sarvam Vision, Bhashini | Most or all 22 | No | Every page is uploaded to a server |

The target user today chooses between a slow or Latin-only offline tool and uploading Aadhaar cards, land records and legal papers to the cloud. The product removes that trade-off.

## Target users

The first users are people who handle sensitive regional-language paper every day and are not allowed, or do not want, to upload it.

| Segment | What they scan | Why offline matters |
| --- | --- | --- |
| Advocates and legal clerks | Court orders, FIRs, sale deeds in Telugu and other languages | Client confidentiality |
| Tahsildar and revenue office staff, document writers | Land records (pahani, adangal), EC, registration papers | Government data rules; poor connectivity |
| CA and tax practices | Notices, invoices, bank statements | Client financial data |
| Hospitals and clinics | Prescriptions, discharge summaries | Patient privacy |
| Students and exam aspirants | Telugu and Hindi printed study material, old papers | Free and works on any laptop |
| Researchers, archives, publishers | Old printed books and periodicals | Bulk volume; no per-page API cost |

Open question: which single segment to win first. Legal and revenue work in Andhra Pradesh and Telangana is the strongest guess, because Telugu has the weakest existing support.

## Product scope

The MVP does one job well: a printed page in a chosen Indian language plus English becomes accurate editable text, offline.

**MVP (v1)**

- Drag and drop images and PDFs; single page or a whole folder
- User picks the language(s): Telugu, Hindi/Marathi, Tamil, Kannada, plus English
- Output: copy text, .txt, .docx, searchable PDF (text layer under the image)
- Side-by-side view with line boxes; click a line to correct it
- Screen-snip OCR with a hotkey, like PowerToys Text Extractor but for Indic
- Zero network calls after model download; a visible "offline" indicator

**v2**

- Automatic script detection per line, so mixed Telugu and English pages need no setup
- "Accurate pack": optional download of the 22-language model for harder pages and the remaining languages
- PII redaction: find and black out Aadhaar, PAN, phone numbers before export
- Tables to Excel

**Later**

- Local full-text search across all scanned documents
- Handwriting (via the accurate pack)
- Team or office licence with batch watch-folders

**Not in scope:** cloud sync, translation, mobile apps.

## Architecture

One small detector always runs. A router sends each line to the fast engine or the accurate pack, so only the models a document needs are loaded.

&#91;embedded content: processing pipeline · one detector, two recognizer tiers\]

- C# with WinUI 3; ONNX Runtime for PaddleOCR, with DirectML as an optional GPU boost
- Recognizers held in a cache keyed by language and loaded on first use
- If indic-ocr cannot be exported to ONNX, it runs as a local Python sidecar over a named pipe
- Routing: v1 uses the user's language choice; v2 adds a tiny per-line script-ID classifier with confidence fallback

## Model and licensing decisions

Ship PaddleOCR in v1 because it is small, ONNX and C#-friendly. Add indic-ocr as an optional pack once its licence is cleared. Avoid anything extracted from other vendors' apps.

| Model | Role | Size | Licence check |
| --- | --- | --- | --- |
| [PaddleOCR ONNX](https://huggingface.co/monkt/paddleocr-onnx) (PP-OCRv3 det + rec) | v1 engine: Telugu, Tamil, Kannada, Devanagari, English | about 8.6 MB per language | PaddleOCR is Apache 2.0; confirm the ONNX repo's terms |
| [bodhan-ai/indic-ocr](https://huggingface.co/bodhan-ai/indic-ocr) | Accurate pack: 22 languages printed, 12 handwritten | 1.7 GB | Gated; read terms before commercial use |
| [Indra OCR](https://huggingface.co/datalab-to/indra-ocr) | English-heavy and digital PDFs: fast layout and tables | about 18 MB | Free under $20M revenue but not if competing with Datalab's API; ask them |
| Tesseract | Fallback for Gujarati, Odia, Punjabi, Malayalam until the pack lands | small | Apache 2.0 |
| Own Telugu/Dravidian CRNN | Long-term moat, trained on the Mozhi dataset ([CVIT](https://cvit.iiit.ac.in/usodi/tdocrmil.php)) | target under 15 MB | Ours |
| Google ML Kit extracted models | Do not use | n/a | Extracted from APKs; Google's terms apply |

## Roadmap

Spend the first two weeks proving accuracy on real documents before writing the app; the MVP follows by week 8 and a public launch by week 14.

&#91;embedded content: roadmap · 4 phases, 3 gates\]

Timings are a proposal for part-time work alongside a full-time job; adjust once phase 0 shows how much model work is needed.

## Risks and open questions

The biggest risk is accuracy: if PaddleOCR v3 is not good enough on real Telugu documents, v1 has nothing to sell.

| Risk | Fallback |
| --- | --- |
| PaddleOCR Indic accuracy too low on real scans | Measure first on 50 real pages; if weak, pull the accurate pack or own-CRNN work forward |
| indic-ocr licence forbids commercial use | Keep it as a free personal-use add-on, or license it; own model as the long-term answer |
| indic-ocr needs PyTorch and is slow on CPU | Run it as a local Python sidecar; try ONNX export; show a progress bar |
| Microsoft adds Indic to Windows OCR | Compete on PDF/Word output, redaction, bulk and accuracy, not on snipping alone |
| Users will not pay for desktop software | Free tier for students; paid for professionals and offices |
| Low trust in "offline" claims | Publish a network-free audit; let users block the app in the firewall and still use it |

Open questions:

- [ ] Which segment and state to launch in first?
- [ ] Microsoft Store, direct download, or both?
- [ ] Price: one-time licence or yearly?

## Success metrics

v1 succeeds if it reads real Telugu documents accurately, fast, with zero network traffic.

| Metric | v1 target |
| --- | --- |
| Word accuracy, clean printed Telugu | 90% or better on our own 50-page test set |
| Time per A4 page, 4-core laptop, no GPU | under 2 s (e-Aksharayan takes 45 to 60 s) |
| Installer size without the accurate pack | under 150 MB |
| Network calls after model download | 0 |
| Beta users who keep using it after 4 weeks | 40% of 50 beta users |

## Sources

- [Windows 10 OCR languages, Windows Developer Blog](https://blogs.windows.com/windowsdeveloper/2016/02/08/optical-character-recognition-ocr-for-windows-)
- [Windows AI Text Recognition API](https://learn.microsoft.com/windows/ai/apis/text-recognition)
- [e-Aksharayan, TDIL / MeitY](https://tdil-dc.in/eocr/index.html)
- [Indian-language OCR software, SimpleOCR](https://www.simpleocr.com/?p=3771)
- [PaddleOCR ONNX models](https://huggingface.co/monkt/paddleocr-onnx)
- [bodhan-ai/indic-ocr](https://huggingface.co/bodhan-ai/indic-ocr)
- [datalab-to/indra-ocr](https://huggingface.co/datalab-to/indra-ocr)
- [CVIT, Towards Deployable OCR Models for Indic Languages](https://cvit.iiit.ac.in/usodi/tdocrmil.php)
