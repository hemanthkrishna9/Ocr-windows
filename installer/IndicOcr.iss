; Inno Setup 6 script for the Indic OCR desktop app.
;
; Build (CI does this; see .github/workflows/build.yml):
;   ISCC.exe /DAppVersion=0.1.0 /DPublishDir=..\publish\win-x64 /DModelsDir=..\models installer\IndicOcr.iss
; Output: installer\out\IndicOcr-Setup-<version>.exe
;
; Everything the app needs is inside the one installer: the self-contained app, the OCR models and
; the fonts (the fonts are part of the publish folder). No download happens at install or run time.

; ---- Easy-to-change branding. The product name is a working name; the brand is still to be decided under NxtDuo. ----
#ifndef AppName
  #define AppName "Indic OCR"
#endif
#ifndef AppPublisher
  #define AppPublisher "NxtDuo"
#endif
#ifndef AppUrl
  #define AppUrl "https://github.com/"
#endif
#ifndef AppExeName
  #define AppExeName "IndicOcr.App.exe"
#endif
; Folder name under %LOCALAPPDATA% where the app keeps settings, cache and log (must match AppPaths.cs in the app).
#ifndef DataFolderName
  #define DataFolderName "IndicOcr"
#endif
; Version comes from CI (/DAppVersion=...). The fallback is only for local builds.
#ifndef AppVersion
  #define AppVersion "0.0.0-local"
#endif
; Inno needs a numeric VersionInfoVersion; CI passes /DAppNumericVersion=1.2.3.0
#ifndef AppNumericVersion
  #define AppNumericVersion "0.0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish\win-x64"
#endif
#ifndef ModelsDir
  #define ModelsDir "..\models"
#endif

[Setup]
; Fixed forever: this GUID identifies the app for upgrades and uninstall. Never change it.
AppId={{6F1D2C54-3B7A-4E58-9C0D-A41E7B93D2F8}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
VersionInfoVersion={#AppNumericVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} setup
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
OutputDir=out
OutputBaseFilename=IndicOcr-Setup-{#AppVersion}
; Per-user install by default (no admin, no UAC prompt). The user can choose "all users" in the dialog,
; or pass /ALLUSERS on the command line, which asks for elevation.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
InfoBeforeFile=..\THIRD_PARTY_NOTICES.md
CloseApplications=yes
RestartApplications=no
; Unsigned build: the installer must not claim a signature. See docs/install.md for signing.
; SignTool is intentionally not configured.

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
; The self-contained app (runtime, WinUI 3, ONNX Runtime, fonts\ ...).
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; OCR models next to the exe: the app looks in <exe dir>\models first.
Source: "{#ModelsDir}\det.onnx"; DestDir: "{app}\models"; Flags: ignoreversion
Source: "{#ModelsDir}\rec_*.onnx"; DestDir: "{app}\models"; Flags: ignoreversion
Source: "{#ModelsDir}\dict_*.txt"; DestDir: "{app}\models"; Flags: ignoreversion
; Licence and notice texts.
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "..\app\IndicOcr.App\fonts\OFL.txt"; DestDir: "{app}\licenses"; DestName: "OFL-fonts.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; Not run after a silent install, so CI and IT deployments do not start the app.
Filename: "{app}\{#AppExeName}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
// Data the app writes lives in %LOCALAPPDATA%\<DataFolderName>: settings.json, app.log and cache\.
// The cache only holds page images of a crashed session and is always removed.
// settings.json and app.log are kept unless the user says otherwise (silent uninstall always keeps them).

function DataDir(): String;
begin
  Result := ExpandConstant('{localappdata}\{#DataFolderName}');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Dir: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  Dir := DataDir();
  if not DirExists(Dir) then Exit;

  DelTree(Dir + '\cache', True, True, True);

  if not UninstallSilent() then
  begin
    if MsgBox('Also delete your {#AppName} settings and error log?' + #13#10 + #13#10 +
              'Choose No to keep them, for example if you plan to reinstall.' + #13#10 +
              'Folder: ' + Dir,
              mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    begin
      DeleteFile(Dir + '\settings.json');
      DeleteFile(Dir + '\app.log');
    end;
  end;

  // Remove the folder only if nothing else (for example a user's own models\ folder) is left in it.
  RemoveDir(Dir);
end;
