#define MyAppName "LaunchPad"
#define MyAppVersion "1.0.2"
#define MyAppPublisher "LaunchPad"
#define MyAppExeName "LaunchPad.exe"

[Setup]
AppId={{8F3A1C2E-6B47-4D19-9C5A-71E0B4D84A21}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\LaunchPad
UsePreviousAppDir=no
UninstallDisplayName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=LaunchPad-Setup
SetupIconFile=..\src\LaunchPad\Assets\LaunchPad.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=no
DiskSpanning=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableWelcomePage=no
InfoBeforeFile=..\DOWNLOAD-NOTE.txt
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a Desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "startmenuicon"; Description: "Create a Start Menu shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Dirs]
Name: "{app}\sessions"; Permissions: users-modify
Name: "{app}\qemu"
Name: "{app}\images"
[Files]
Source: "SetupPrepare.ps1"; Flags: dontcopy
Source: "..\dist\LaunchPad.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\DOWNLOAD-NOTE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\NOTICE"; DestDir: "{app}"; Flags: ignoreversion
; Prepared guest and QEMU. No grok.exe, no libguestfs, no WSL, and no password file.
; SetupPrepare.ps1 enables HypervisorPlatform when that feature is off.
; It calls New-LocalUser for BuildLaunchTest only when that account is missing.
Source: "..\..\build-launch-qemu\qemu\*"; DestDir: "{app}\qemu"; Flags: ignoreversion recursesubdirs createallsubdirs nocompression
Source: "..\..\build-launch-qemu\COPYING"; DestDir: "{app}\qemu"; Flags: ignoreversion
Source: "..\..\build-launch-qemu\images\debian-12-builder.qcow2"; DestDir: "{app}\images"; Flags: ignoreversion nocompression
Source: "..\..\build-launch-qemu\images\debian-12-nocloud-amd64-20260601-2496.qcow2"; DestDir: "{app}\images"; Flags: ignoreversion nocompression
Source: "..\..\build-launch-qemu\qemu\fence\QEMU-SOURCE.txt"; DestDir: "{app}\qemu"; Flags: ignoreversion

[Icons]
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startmenuicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Start LaunchPad"; Flags: nowait postinstall skipifsilent unchecked

[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\LaunchPad"

[Code]
function NextButtonClick(CurPageID: Integer): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if CurPageID <> wpReady then
    exit;

  ExtractTemporaryFile('SetupPrepare.ps1');
  if (not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\SetupPrepare.ps1') + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
  begin
    MsgBox('Setup stopped before it copied files.', mbError, MB_OK);
    Result := False;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  FenceShare, QemuShare: String;
begin
  if CurStep <> ssPostInstall then
    exit;

  // The console-size QEMU runs from qemu\fence with its own DLLs.
  // Its working directory is that folder, and -L share reads firmware there.
  // The junction points at the firmware already installed in qemu\share.
  FenceShare := ExpandConstant('{app}\qemu\fence\share');
  QemuShare := ExpandConstant('{app}\qemu\share');
  if (not DirExists(FenceShare)) and DirExists(QemuShare) and DirExists(ExpandConstant('{app}\qemu\fence')) then
    Exec(ExpandConstant('{cmd}'),
      '/c mklink /J "' + FenceShare + '" "' + QemuShare + '"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  ForceDirectories(ExpandConstant('{app}\sessions'));
  Exec(ExpandConstant('{sys}\icacls.exe'),
    '"' + ExpandConstant('{app}') + '" /grant BuildLaunchTest:(OI)(CI)RX *S-1-5-12:(OI)(CI)RX /T /C',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\icacls.exe'),
    '"' + ExpandConstant('{app}\sessions') + '" /grant BuildLaunchTest:(OI)(CI)M *S-1-5-12:(OI)(CI)M /T /C',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

end;
