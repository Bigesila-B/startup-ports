#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{B8E057AA-C2F2-43FB-BC09-5724CA3017BF}
AppName=StartupPorts
AppVersion={#AppVersion}
AppPublisher=Bigesila-B
AppPublisherURL=https://github.com/Bigesila-B/startup-ports
DefaultDirName={localappdata}\Programs\StartupPorts
DefaultGroupName=StartupPorts
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
MinVersion=10.0.22000
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
UninstallDisplayIcon={app}\StartupPorts.exe
CloseApplications=no
RestartApplications=no
OutputDir=..\artifacts\release
OutputBaseFilename=StartupPorts-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\StartupPorts"; Filename: "{app}\StartupPorts.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\StartupPorts.exe"; Description: "{cm:LaunchProgram,StartupPorts}"; Flags: nowait postinstall skipifsilent

[Code]
function RunMaintenance(const Mode: String): Boolean;
var
  Launcher: String;
  ResultCode: Integer;
begin
  Launcher := ExpandConstant('{app}\StartupPorts.exe');
  Result := not FileExists(Launcher);
  if not Result then
    Result := Exec(Launcher, Mode, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) and
              (ResultCode = 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not RunMaintenance('--prepare-upgrade') then
    Result := 'StartupPorts is still running. Close StartupPorts and retry the update.';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usUninstall) and (not RunMaintenance('--uninstall')) then
    RaiseException('StartupPorts could not remove its scheduled tasks. Retry from the account that installed it.');
end;
