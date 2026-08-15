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
; The folder this installer packs wholesale. Releases fill it in CI: .github/workflows/release.yml
; runs `dotnet publish -c Release -r win-x64 --self-contained true -o publish\win-x64` on a fresh
; runner, so a real release is always built from a clean checkout at the pinned extraction gitlink
; and cannot pick up anything stale. publish\ is gitignored and never committed.
;
; RUNNING ISCC BY HAND IS THE UNSAFE PATH. Inno packs whatever is in this folder with no idea how old
; it is, so a local build against a leftover publish ships that leftover — silently, since the
; installer compiles fine. One was found here holding a champion catalog a month older than the
; source tree, and a second holding one with two champions' ascension ranges wrong. If you compile
; locally, publish immediately before, with the same flags CI uses (note --self-contained TRUE; a
; false build produces a framework-dependent layout that is not what ships).
;
; The folder is deliberately left absent rather than pre-populated: ISCC fails loudly on a missing
; source, which is a better outcome than packing a stale one.
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
; extraction submodule — the engine's runtime data (known-offsets.json, resource-allowlist.json)
; and the bundled metadata catalogs under exports\ (champion_index.json, mastery_index.json). Those
; are shipped as plain JSON so refreshing them after a game patch is a file swap in {app}\exports,
; not a rebuild; champion_index.json gives each hero a real name, faction and role.
; champion_index.json is a verbatim copy of the one in RslCompanionMetadata/exports — one file, one
; shape (~0.7 MB). The slim/full pair this line used to describe is gone: types[] was folded into the
; champion, so the single catalog is smaller than the old slim copy was. It carries PLAYABLE
; CHAMPIONS ONLY; boss_index.json is a separate schema and is deliberately not shipped, since nothing
; a user owns is a boss. See the note in the engine's csproj and
; RslCompanionMetadata/docs/champion-index-contract.md.
; offsets_cache.json is deliberately NOT among them: it is per-session scratch the app writes next
; to the exe on first run, and a shipped copy would only carry a heap address that died with
; whichever machine produced it. known-offsets.json is what spares users the calibration scan.
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

; Preferences the app writes back (UserSettings.cs) live in a DIFFERENT folder from the one above —
; %LocalAppData%\RslCompanion — which they share with the extraction engine's calibrated-offsets.json.
; Without this line an uninstall left the answers behind, so a reinstall silently inherited them and
; never re-asked: the stay-signed-in choice, the activity-log level, and whether the app may check for
; updates on its own. Those are consent, and consent should not outlive the app that asked for it.
;
; ONLY settings.json is removed. calibrated-offsets.json is minutes of scanning per game build that
; the user paid for once; it is keyed by build hash, so it stays correct across reinstalls and after
; the game updates, and throwing it away would cost a ~35-50s rescan to recover something the
; uninstall had no reason to touch. dirifempty then takes the folder itself, but only when nothing
; else is left in it — i.e. exactly when no calibration was ever done.
Type: files; Name: "{localappdata}\RslCompanion\settings.json"
Type: dirifempty; Name: "{localappdata}\RslCompanion"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
; The in-app update banner downloads this installer, runs it with /SILENT /relaunch=1 and exits so
; its files can be replaced — so without this entry an update would end with the app simply gone
; (the line above is postinstall+skipifsilent: it is the wizard's finish-page checkbox, which a
; silent install never shows). Gated on the parameter rather than on silence alone, so an unattended
; deployment does not get a window it never asked for. The app passes /NORESTARTAPPLICATIONS to keep
; the restart manager from launching a second copy alongside this one.
Filename: "{app}\{#MyAppExeName}"; Flags: nowait skipifnotsilent; Check: RelaunchRequested

[Code]
// Whether the caller asked for the app to be started again afterwards (the in-app updater does:
// /SILENT /relaunch=1). Only consulted by the silent-mode [Run] entry above.
function RelaunchRequested(): Boolean;
begin
  Result := ExpandConstant('{param:relaunch|0}') = '1';
end;

// Inno already upgrades in place when AppId matches (files below are ignoreversion, so they get
// overwritten regardless). This just surfaces that to the user instead of silently replacing files
// with no feedback. It deliberately never runs the previous uninstaller: that would trigger
// [UninstallDelete] and wipe creds.dat / the WebView2 profile, signing the user out on every update.
//
// Suppressed in silent mode: a MsgBox still shows there, so without the guard the in-app updater
// would sit on an invisible modal dialog waiting for a click nobody knows to give.
function InitializeSetup(): Boolean;
var
  PrevVersion: String;
  UninstallKey: String;
begin
  Result := True;
  UninstallKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#SetupSetting("AppId")}_is1';
  if (not WizardSilent()) and RegQueryStringValue(HKA, UninstallKey, 'DisplayVersion', PrevVersion) then
    MsgBox(FmtMessage('{#MyAppName} %1 is already installed.'#13#10'Setup will update it to version {#MyAppVersion}.', [PrevVersion]), mbInformation, MB_OK);
end;
