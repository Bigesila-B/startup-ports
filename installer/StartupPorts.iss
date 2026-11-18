#ifndef AppVersion
  #define AppVersion "0.2.7"
#endif
#define AppGuid "B8E057AA-C2F2-43FB-BC09-5724CA3017BF"

[Setup]
AppId={{{#AppGuid}}
AppName=StartupPorts
AppVersion={#AppVersion}
AppPublisher=Bigesila-B
AppPublisherURL=https://github.com/Bigesila-B/startup-ports
DefaultDirName={localappdata}\Programs\StartupPorts
DefaultGroupName=StartupPorts
DisableDirPage=auto
UsePreviousAppDir=yes
AlwaysShowDirOnReadyPage=yes
DisableProgramGroupPage=yes
ShowLanguageDialog=yes
PrivilegesRequired=lowest
MinVersion=10.0.22000
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
UninstallDisplayIcon={app}\StartupPorts.exe
SetupIconFile=..\src\StartupPorts\Assets\StartupPorts.ico
CloseApplications=no
RestartApplications=no
OutputDir=..\artifacts\release
OutputBaseFilename=StartupPorts-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "zhcn"; MessagesFile: "ChineseSimplified.isl"

[CustomMessages]
en.ChangeInstallFolder=To change the installation folder, uninstall StartupPorts, then run Setup again and choose the new folder.
zhcn.ChangeInstallFolder=如需更改安装目录，请先卸载 StartupPorts，再运行安装程序并选择新目录。
en.CloseBeforeUpgrade=StartupPorts is still running. Close it and retry the update.
zhcn.CloseBeforeUpgrade=StartupPorts 仍在运行。请关闭程序后重试更新。
en.UninstallTasksFailed=StartupPorts could not remove its scheduled tasks. Retry from the account that installed it.
zhcn.UninstallTasksFailed=无法移除 StartupPorts 的计划任务。请使用安装此程序的 Windows 账户重试。

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "InnoSetup-LICENSE.txt"; DestDir: "{app}\Licenses"; Flags: ignoreversion

[Registry]
Root: HKCU; Subkey: "Software\StartupPorts"; ValueType: string; ValueName: "Language"; ValueData: "{code:AppLanguage}"; Flags: createvalueifdoesntexist uninsdeletevalue uninsdeletekeyifempty

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Icons]
Name: "{userprograms}\StartupPorts"; Filename: "{app}\StartupPorts.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\StartupPorts"; Filename: "{app}\StartupPorts.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\StartupPorts.exe"; Description: "{cm:LaunchProgram,StartupPorts}"; Flags: nowait postinstall skipifsilent

[Code]
function AppLanguage(Param: String): String;
begin
  if ActiveLanguage = 'zhcn' then
    Result := 'zh-CN'
  else
    Result := 'en-US';
end;

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
var
  PreviousDirectory: String;
begin
  Result := '';
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{{#AppGuid}}_is1',
       'InstallLocation', PreviousDirectory) then
    if (PreviousDirectory <> '') and
       (CompareText(RemoveBackslashUnlessRoot(ExpandFileName(PreviousDirectory)),
                    RemoveBackslashUnlessRoot(ExpandFileName(WizardDirValue))) <> 0) then
    begin
      Result := CustomMessage('ChangeInstallFolder');
      Exit;
    end;
  if not RunMaintenance('--prepare-upgrade') then
    Result := CustomMessage('CloseBeforeUpgrade');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usUninstall) and (not RunMaintenance('--uninstall')) then
    RaiseException(CustomMessage('UninstallTasksFailed'));
end;
