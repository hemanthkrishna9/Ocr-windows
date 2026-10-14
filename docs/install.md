# Installing and removing Indic OCR

Indic OCR is one installer file. It contains the app, the Telugu, Hindi/Marathi, Tamil, Kannada (beta) and English models, and the fonts. Nothing is downloaded during or after install, so you can install it on a PC that has never been online.

"Indic OCR" is a working name; the final brand is still to be decided.

## Install

1. Download `IndicOcr-Setup-<version>.exe` and check it against `SHA256SUMS` from the same release (`certutil -hashfile IndicOcr-Setup-<version>.exe SHA256` in a command prompt, then compare).
2. Run it. By default it installs for your Windows user only. It does not need administrator rights and shows no UAC prompt.
3. Optional: tick "Create a desktop shortcut". A Start-menu entry is always created.

The wizard offers "all users" as an option; that installs under `C:\Program Files` and asks for administrator approval. Per-user is recommended. Do not run the app itself as administrator, because Windows blocks drag and drop into elevated windows.

Requirements: 64-bit Windows 10 version 1809 or later, or Windows 11. No .NET or other runtime install is needed (the app carries its own).

### Silent install (IT deployment)

```
IndicOcr-Setup-<version>.exe /VERYSILENT /SUPPRESSMSGBOXES /CURRENTUSER /DIR="D:\Apps\IndicOcr"
```

Use `/ALLUSERS` instead of `/CURRENTUSER` for a machine-wide install (needs an elevated prompt). `/LOG="C:\temp\setup.log"` writes an install log. The log contains file paths only, never document text.

## Where things live

| What | Where |
| --- | --- |
| Program, models, fonts (per-user install) | `%LOCALAPPDATA%\Programs\Indic OCR\` (for example `C:\Users\you\AppData\Local\Programs\Indic OCR`) |
| Program (all-users install) | `C:\Program Files\Indic OCR\` |
| Models | `<install dir>\models\` (`det.onnx`, `rec_xx.onnx`, `dict_xx.txt`) |
| Licence texts | `<install dir>\licenses\` |
| Settings (languages, snip hotkey) | `%LOCALAPPDATA%\IndicOcr\settings.json` |
| Error log (exception types and stack traces only, never document text) | `%LOCALAPPDATA%\IndicOcr\app.log` |
| Page-image cache while you work | `%LOCALAPPDATA%\IndicOcr\cache\` (cleared when you close the app) |
| Optional extra models | `%LOCALAPPDATA%\IndicOcr\models\` (used only if the install dir has none) |

Your documents are never copied anywhere except the temporary cache above and the output files you save.

## Uninstall

Settings, Apps, "Indic OCR", Uninstall. Or run `unins000.exe` in the install folder.

The uninstaller removes the program, models and shortcuts, and always deletes `%LOCALAPPDATA%\IndicOcr\cache`. It then asks whether to delete your settings and error log too. The default answer is No, so a reinstall keeps your settings. A silent uninstall (`unins000.exe /VERYSILENT`) always keeps them. To remove everything by hand, delete `%LOCALAPPDATA%\IndicOcr`.

## Upgrade

Run the newer installer. It recognises the earlier install (the app ID never changes) and replaces the files in place. Settings are kept.

## SmartScreen warning on unsigned builds

Current builds are not code-signed. Windows SmartScreen will show "Windows protected your PC" with "Unknown publisher" the first time you run the installer. This is expected for an unsigned file and does not mean it is unsafe. To continue: click "More info", then "Run anyway". Check the SHA-256 against `SHA256SUMS` first, so you know the file is the one we published.

### TODO before a public launch: code signing

Signing needs a certificate that only the publisher can obtain and pay for. Nothing in the repository can replace it.

- Buy a code-signing certificate for the publishing company (NxtDuo or whichever legal entity ships the product). Options:
  - An OV (organisation validation) certificate from a public CA. Cheaper, but SmartScreen reputation builds slowly over many downloads.
  - An EV (extended validation) certificate. Costs more, needs company verification and a hardware token or cloud HSM; it gives immediate SmartScreen trust.
  - Microsoft Artifact Signing (formerly Trusted Signing), a cloud service with a monthly fee and organisation identity checks (availability depends on country).
- Provide it to CI as repository secrets (never commit it). Then add to `installer/IndicOcr.iss` a `SignTool=` line and `SignedUninstaller=yes`, add a signing step for `IndicOcr.App.exe` before ISCC runs, and sign the final installer.
- The offline promise does not change: signing happens on the build machine; the app never contacts a certificate server by itself.

Until then, the release page says the build is unsigned.
