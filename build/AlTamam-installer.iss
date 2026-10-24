; Phase 15 (15.04) — REAL Windows installer for AlTamam, Inno Setup 6 (approved tool).
; Compiled by CI: ISCC.exe /DAppVersion=x.y.z /DPublishDir=path build/AlTamam-installer.iss
#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish\AlTamam-win-x64"
#endif
#ifndef TargetArch
  #define TargetArch "win-x64"
#endif

[Setup]
#if CompareText(TargetArch, "win-x86") == 0
; Separate installer registrations preserve the x64 upgrade identity.
AppId={{B94D3F15-6C24-4585-87F6-8A93D3A18532}
#else
AppId={{3A156EF1-176E-45DD-A570-8224D739A454}
#endif
AppName=التمام اليومي
UninstallDisplayName=التمام اليومي
AppVersion={#AppVersion}
AppPublisher=AlTamam
DefaultDirName={autopf}\AlTamam\{#TargetArch}
DefaultGroupName=التمام اليومي
#if CompareText(TargetArch, "win-x86") == 0
ArchitecturesAllowed=x86compatible
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\artifacts
OutputBaseFilename=AlTamam-Installer-{#TargetArch}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\AlTamam.DesktopHost\Assets\AlTamam.ico
UninstallDisplayIcon={app}\AlTamam.DesktopHost.exe
; 15.04 uninstall policy: NEVER delete the encrypted DB or backups — user data
; survives uninstall/reinstall by design (data lives in %LOCALAPPDATA%\AlTamam).
DisableProgramGroupPage=yes
LicenseFile=..\docs\release\LICENSE-NOTES.txt

[Languages]
Name: "arabic"; MessagesFile: "compiler:Languages\Arabic.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; WebView2 Evergreen bootstrapper (15.05): installs runtime only if absent;
; offline fallback = MicrosoftEdgeWebview2Setup.exe /silent /install requires
; bundled runtime CAB — owner decision recorded in release notes until then.

[Dirs]
Name: "{localappdata}\AlTamam"; Permissions: users-modify
Name: "{localappdata}\AlTamam\backups"; Permissions: users-modify

[Icons]
Name: "{group}\التمام اليومي"; Filename: "{app}\AlTamam.DesktopHost.exe"
Name: "{autodesktop}\التمام اليومي"; Filename: "{app}\AlTamam.DesktopHost.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\AlTamam.DesktopHost.exe"; Description: "{cm:LaunchProgram,التمام اليومي}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Intentionally EMPTY for data files: {localappdata}\AlTamam must survive.
; Only install-dir leftovers that are safe to remove:
Type: filesandordirs; Name: "{app}\logs"

[Code]
// Preserve-user-data guard: warn on uninstall that DB/backups remain.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    MsgBox('بياناتك محفوظة / Your encrypted database and backups were preserved under %LOCALAPPDATA%\AlTamam.', mbInformation, MB_OK);
end;
