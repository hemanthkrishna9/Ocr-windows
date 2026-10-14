# Checking that nothing leaves your PC

Indic OCR makes one promise: your documents are read on your PC and never sent anywhere. The app has no update checker, no telemetry and no crash upload. This page shows how you, or an auditor, can check that yourself instead of trusting us.

## 1. Block the app in the Windows Firewall, then use it

If the app still works with the network blocked, it does not need the network.

1. Find the program: `%LOCALAPPDATA%\Programs\Indic OCR\IndicOcr.App.exe` (or the folder you installed into).
2. Open "Windows Defender Firewall with Advanced Security" (press Start, type `wf.msc`).
3. "Outbound Rules", "New Rule...", choose "Program", pick `IndicOcr.App.exe`, choose "Block the connection", tick all three profiles, name it "Block Indic OCR".
4. Open a scanned page in the app and run all three exports. It reads and exports as normal.

The same from an administrator PowerShell:

```powershell
New-NetFirewallRule -DisplayName "Block Indic OCR" -Direction Outbound -Action Block -Program "$env:LOCALAPPDATA\Programs\Indic OCR\IndicOcr.App.exe"
```

Also pull the network cable or turn on airplane mode before you start the app. Everything must still work, including the first run (the models are inside the install folder).

Remove the rule when you are done: `Remove-NetFirewallRule -DisplayName "Block Indic OCR"`.

## 2. Watch the connections

Look for any connection made by the app while you use it:

- Resource Monitor: press Start, type `resmon`, open the Network tab, and look at "Network Activity" and "TCP Connections" for `IndicOcr.App.exe`. It should never appear.
- Command line, from an administrator prompt: `netstat -b -n 5` lists connections with the owning program every 5 seconds. `IndicOcr.App.exe` should not be listed.
- Stronger: a packet capture on your router or in Wireshark, filtered to the PC, while you OCR a document. You will see other programs' traffic, but none that depends on what you do in the app.

The app does open local things on your PC (files you choose, the clipboard, the screen for snips). These are not network connections.

## 3. What our automated tests prove

On every build, GitHub Actions on a clean Windows machine does this (`.github/workflows/build.yml`, step "Silent install, then offline end-to-end OCR check"):

1. Builds the one installer and fails the build if it is larger than 150 MB (models included).
2. Installs it silently into a temp folder.
3. Creates a Windows Firewall rule that blocks all outbound traffic for the command-line tool, and first proves with a control program that such a rule really blocks traffic on that machine.
4. Reads a Telugu test image with the blocked tool, using only the models from the installed folder, and writes `.txt`, `.docx` and searchable `.pdf` files. The build fails unless the expected Telugu words are in the text.
5. Uninstalls silently and checks that the install folder is gone.

The command-line tool and the desktop app share the same OCR engine, so this shows that the OCR and export code needs no network. It does not by itself prove the desktop window code is silent. For that, use sections 1 and 2 on the real app. The code review rule in this project is that any new network call blocks a change; searching the source for `HttpClient`, `WebClient`, `Socket` or `System.Net` should find nothing in the hand-written code (the only hits are XAML namespace URLs and compiler-generated implicit usings).

## 4. Specific things people ask about

- ONNX Runtime telemetry. The Windows build of ONNX Runtime can emit diagnostic events through Windows event tracing. The engine turns this off at start-up (`OrtEnv.DisableTelemetryEvents()`) before any model is loaded. These events go to local Windows tracing, not to a server, but we switch them off anyway.
- Clipboard. Text the app copies (Copy all text, and snips) is marked so it is kept out of the Win+V clipboard history and out of the cloud clipboard that syncs between your devices.
- Update checks. There are none. New versions come only when you install a new installer. The installer has no "check for updates" step either.
- Logs. The error log holds exception types and stack traces only, never the text of your documents. Find it at `%LOCALAPPDATA%\IndicOcr\app.log`; you can read or delete it any time.
- Temporary files. Page images are cached as JPEG files in `%LOCALAPPDATA%\IndicOcr\cache` while the app runs and deleted when it closes. The uninstaller also removes this folder.
- Fonts and web content. Fonts for the searchable PDF ship in the install folder. The window shows no web content and loads nothing from the internet.
- Models. They are built from pinned, checksummed sources at build time and shipped inside the installer. See `THIRD_PARTY_NOTICES.md` for their licences.

## 5. What this does not cover

- Windows itself, other programs, antivirus and your network may still talk to the internet. The checks above are about Indic OCR only.
- An unsigned build cannot prove who made it. Compare the installer's SHA-256 with `SHA256SUMS` on the release page, and see `docs/install.md` about signing.
- If you find a network connection from Indic OCR, treat it as a bug and report it.
