; Inno Setup script for the RSL Companion Account Data Extractor.
;
; Build:
;   1. dotnet publish ..\RslCompanionUploader.csproj -c Release -r win-x64 --self-contained true ^
;        -o ..\publish\win-x64
;   2. ISCC.exe setup.iss
;
; Per-user install (no admin prompt): files go to %LocalAppData%\Programs and the
; rslcompanion-extractor:// protocol is registered under HKCU at install time, so the
; "Launch Account Data Extractor" button on rslcompanion.com works right after install,
; before the app has ever been started. The app re-registers the protocol on every run
; as a self-heal; the uninstaller removes it.

#define MyAppName "RSL Companion Account Data Extractor"
; Overridable from the command line (CI passes the release tag): ISCC /DMyAppVersion=1.2.0 setup.iss
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "RSL Companion"
#define MyAppURL "https://rslcompanion.com"
#define MyAppExeName "RslCompanionUploader.exe"
#define MyProtocol "rslcompanion-extractor"
#define PublishDir "..\publish\win-x64"

[Setup]
AppId={{8E0E4C6B-2B7D-4C43-9A31-5D9F6C1A7E42}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=dist
OutputBaseFilename=RslCompanionAccountDataExtractor-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Package the whole publish output: exe, appsettings.json, and — when built with the private
; extraction submodule — the engine's runtime data (known-offsets.json, offsets_cache.json,
; resource-allowlist.json). The champion index is deliberately not among them: it is a product of
; the engine, not an input, and the server keys heroes off baseTypeId rather than the name it fed.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.xml"; Flags: ignoreversion recursesubdirs createallsubdirs

[Registry]
; rslcompanion-extractor:// protocol handler — lets rslcompanion.com launch the app and hand
; over a sign-in token. HKA resolves to HKCU because PrivilegesRequired=lowest.
Root: HKA; Subkey: "Software\Classes\{#MyProtocol}"; ValueType: string; ValueData: "URL:{#MyAppName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\{#MyProtocol}"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\{#MyProtocol}\DefaultIcon"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKA; Subkey: "Software\Classes\{#MyProtocol}\shell\open\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[UninstallDelete]
; The app stores its per-user data OUTSIDE the install dir, in %LocalAppData%\RslCompanionUploader:
;   - creds.dat  : DPAPI-encrypted Firebase refresh token ("remember me")
;   - WebView2\   : embedded-browser profile (site cookies / IndexedDB session)
; The [Files] uninstall only cleans {app}, so without this an uninstall would leave a live
; credential behind and a reinstall would silently sign the user back in. Remove the whole folder.
; UninstallDelete is best-effort: a transiently-locked WebView2 file is skipped, not fatal.
Type: filesandordirs; Name: "{localappdata}\RslCompanionUploader"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
