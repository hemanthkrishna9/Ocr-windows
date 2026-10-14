# Indic OCR desktop app (WinUI 3)

Unpackaged WinUI 3 app (Windows App SDK 1.6, .NET 8, self-contained). It uses `src/IndicOcr.Core` for all OCR and export. It makes no network calls: no update checks, no telemetry, no web fonts.

## Build and run (Windows 10 1809+ or Windows 11)

Requirements: .NET 8 SDK. Visual Studio is optional.

```powershell
dotnet build app/IndicOcr.App/IndicOcr.App.csproj -c Release -r win-x64 -p:Platform=x64
dotnet run --project app/IndicOcr.App/IndicOcr.App.csproj -c Release -r win-x64 -p:Platform=x64

# Self-contained publish folder (no runtime install needed on the target PC)
dotnet publish app/IndicOcr.App/IndicOcr.App.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -o publish/win-x64
```

For ARM64 use `-r win-arm64 -p:Platform=ARM64` (not yet verified in CI).

## Where the models go

The app looks for a models folder in this order and uses the first that has at least one language:

1. `--models "D:\path\to\models"` command-line argument
2. `INDICOCR_MODELS` environment variable
3. `models\` next to `IndicOcr.App.exe`
4. `%LOCALAPPDATA%\IndicOcr\models`

A models folder holds `det.onnx` plus one `rec_xx.onnx` and `dict_xx.txt` pair per language (for example `rec_te.onnx` and `dict_te.txt` for Telugu).

If none is found the app shows a first-run page listing these places.

## Fonts

The searchable PDF text layer needs fonts that cover the Indic scripts. The build copies Noto Sans (Latin), Telugu, Devanagari, Tamil and Kannada from `app/IndicOcr.App/fonts/` to `fonts\` next to `IndicOcr.App.exe`, and the PDF exporter looks there first, so it works on a stock Windows install. The fonts are SIL OFL 1.1 (`fonts/OFL.txt`).

Settings (selected languages, Auto-detect, redaction toggle, snip hotkey) are stored in `%LOCALAPPDATA%\IndicOcr\settings.json`. A small error log with exception types and stack traces only (never document text) is written to `%LOCALAPPDATA%\IndicOcr\app.log`.

## Using it

- Drop images, PDFs or folders on the window, or use Open files / Open folder.
- Auto-detect language is on for a new install: each line is assigned its script (Telugu, Hindi, Tamil, Kannada, or English for Latin text), so mixed pages need no setup. The page header shows "Detected: Telugu, English". With Auto on, the Indic tick boxes are an optional restriction ("only these scripts"; none ticked means every installed one) and English is greyed out because Latin text is always found. Untick Auto to pick the languages yourself (default Telugu + English). An install that already has a settings file keeps its choice (Auto off). Languages without a model are disabled; Kannada is marked beta (from `OcrEngine.IsBeta`). Auto needs at least one Indic model.
- Click a document in the queue to see the page with line boxes. Click a box or a text line to select it; edit the text on the right. Exports use your edits.
- Copy all text, Save .txt, Save .docx, Save searchable PDF and Save tables as Excel (.xlsx) work on the selected document. The workbook has one sheet per table found on each page (a page without a table gets a "Page N text" sheet). Cells are read again from the page image, so table cells do not carry your line edits.
- Export menu, "Redact ID numbers": when ticked (remembered between runs), Copy all text and every export first search the edited pages for Aadhaar, PAN and phone numbers and open a review dialog. Each finding shows its page, kind and masked text with a tick box (all ticked); the page view shows the ticked areas in black. On "Redact and continue" the ticked numbers are blacked out in the image (searchable PDF, and the table extraction for Excel) and replaced by X in the text (.txt, .docx, clipboard, PDF text layer). The unredacted page image is never written while redaction is on. Findings the check digit could not confirm are marked "unverified" (ticked anyway). Unticked findings stay readable, and numbers the reader missed, misread or wrote in Indian-language digits are not found, so check the result. Suggested file names end in `.redacted`. A screen snip has no review step: with redaction on it masks every number found before copying.
- Do not run the app as administrator: Windows blocks drag and drop into elevated windows.

## Keyboard shortcuts

| Keys | Action |
| --- | --- |
| Win+Shift+O (global, default) | Screen snip: drag a rectangle, the text is read and copied to the clipboard |
| Esc / right-click / click without dragging (in snip overlay) | Cancel the snip |
| Export menu, Redact ID numbers | Turn redaction on or off for Copy all text, snips and all exports |
| Tab / click in the text list | Move between lines; the matching box is highlighted |

The hotkey is shown at the top of the window. Change it with the `Hotkey` entry in `%LOCALAPPDATA%\IndicOcr\settings.json` (for example `"Ctrl+Shift+F9"`; modifiers `Ctrl`, `Alt`, `Shift`, `Win`; keys A-Z, 0-9, F1-F24) and restart. Ctrl+Alt+<letter> is avoided on purpose: Windows treats it as AltGr, which Indic keyboard layouts use for typing. If another program already owns the hotkey, or the setting is invalid, the top bar and status bar say so; the Snip button still works. Starting a snip from the button minimises the window first so it is not in the screenshot. Crops shorter than 400 px are enlarged 2x before reading. Text copied by a snip or Copy all text is kept out of the clipboard history and cloud clipboard.

## Memory and temporary files

After a page is read it is stored as a JPEG (quality 90) in `%LOCALAPPDATA%\IndicOcr\cache\<session>` and released from memory; it is decoded again for the preview and, one page at a time, for the searchable PDF. The folder is deleted when you close the app or press Clear queue (it is greyed out while reading, saving or loading a preview), and folders left by a crashed session are removed at the next start. The cache holds only your own page images, never extracted text. Snips are not slowed by a long document: both use the OCR engine at the same time.

## Solution file

The app is deliberately not part of `IndicOcr.sln`: WinUI 3 cannot be built on Linux, so the solution (used by Linux CI and tests) would not restore there. CI builds each project separately; see `.github/workflows/build.yml`. The Windows job also starts the published `IndicOcr.App.exe` for 10 seconds and checks that a `.pri` file is present.

## Not verified yet (needs Windows)

This code was written on Linux and has not been compiled yet; the GitHub Actions Windows job is the first real build. Hand-test: first-run page, pickers, drag and drop, box/line selection sync, all three exports (Export menu, including Excel and redaction with the review dialog and its black preview boxes), Auto-detect on mixed pages and the "Detected:" label, switching Auto off and on, the snip hotkey on a multi-monitor setup with mixed DPI scaling, the toast notice (must not steal focus), minimise-then-snip from the button, closing the app during a long document, and the toolbar at 1366x768 with 125% scaling.
