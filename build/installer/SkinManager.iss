#define ProductVersion "1.1.4"

[Setup]
AppId={{6FBD8E7A-8A61-4DB8-BD4A-4F2E43110B67}
AppName=SkinManager
AppVersion={#ProductVersion}
AppVerName=SkinManager {#ProductVersion}
AppPublisher=SkinManager
VersionInfoVersion=1.1.4.0
VersionInfoProductVersion=1.1.4.0
VersionInfoProductTextVersion={#ProductVersion}
VersionInfoDescription=SkinManager Setup
ShowLanguageDialog=yes
LanguageDetectionMethod=none
VersionInfoProductName=SkinManager
VersionInfoCompany=SkinManager
VersionInfoOriginalFileName=SkinManager-Setup-{#ProductVersion}.exe
DefaultDirName={localappdata}\Programs\SkinManager
DefaultGroupName=SkinManager
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\..\artifacts\release
OutputBaseFilename=SkinManager-Setup-{#ProductVersion}
SetupIconFile=..\..\assets\icons\SkinManager.ico
UninstallDisplayIcon={app}\SkinManager.exe
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
ChangesAssociations=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[CustomMessages]
english.CreateDesktopShortcut=Create a desktop shortcut
english.AdditionalShortcuts=Additional shortcuts:
english.StopFailureRecovered=The background skin agent could not confirm that the current page was restored. Setup or uninstall has been canceled. Open SkinManager, disconnect the skin, and try again.
english.StopFailureTimeout=Timed out while waiting for the background skin agent to exit. Setup or uninstall has been canceled. Close SkinManager and try again.
english.StopFailure=Stopping the background skin agent failed (exit code %1). Setup or uninstall has been canceled. Close SkinManager and try again.
english.StopAgentLaunchFailed=Could not start the command to stop the background skin agent. Uninstall has been canceled. Close SkinManager and try again.
english.StopAgentPrepareFailed=Could not start SkinManager.Agent.exe to stop the background skin agent in this folder. Close SkinManager and try again.
english.RemoveSkinLibraryPrompt=Remove the skin library? Selecting Yes deletes default skins, imported skins, and user changes in this folder. Selecting No keeps the entire skin library.
english.SkinLibraryPathNotConfirmed=Could not confirm that the skin library is inside the installation folder. To avoid deleting another path, the library was kept.
english.SkinLibraryDeleteIncomplete=Could not delete the entire skin library. Some files remain at:
chinesesimp.CreateDesktopShortcut=创建桌面快捷方式
chinesesimp.AdditionalShortcuts=附加快捷方式：
chinesesimp.StopFailureRecovered=皮肤后台未能确认已恢复当前页面。安装或卸载已中止；请打开 SkinManager，断开连接后再重试。
chinesesimp.StopFailureTimeout=等待皮肤后台退出超时。安装或卸载已中止；请关闭 SkinManager 后再重试。
chinesesimp.StopFailure=停止皮肤后台失败（退出代码 %1）。安装或卸载已中止；请关闭 SkinManager 后再重试。
chinesesimp.StopAgentLaunchFailed=无法启动皮肤后台停止命令。卸载已中止；请关闭 SkinManager 后重试。
chinesesimp.StopAgentPrepareFailed=无法启动 SkinManager.Agent.exe 来停止本目录中的皮肤后台。请关闭 SkinManager 后重试。
chinesesimp.RemoveSkinLibraryPrompt=是否删除皮肤库？选择“是”会删除此目录中的默认皮肤、导入皮肤和用户修改；选择“否”会保留整个皮肤库。
chinesesimp.SkinLibraryPathNotConfirmed=无法确认皮肤库位于安装目录中。为避免删除其他路径，皮肤库已保留。
chinesesimp.SkinLibraryDeleteIncomplete=未能完整删除皮肤库，仍有文件保留在：

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopShortcut}"; GroupDescription: "{cm:AdditionalShortcuts}"; Flags: unchecked

[Files]
Source: "..\..\artifacts\publish\installer-stage\SkinManager.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\artifacts\publish\installer-stage\SkinManager.Agent.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\artifacts\publish\installer-stage\ThirdPartyNotices.txt"; DestDir: "{app}"; Flags: ignoreversion

[Run]
Filename: "{app}\SkinManager.exe"; Flags: nowait; Check: IsSkinManagerUpdate

[Icons]
Name: "{autoprograms}\SkinManager"; Filename: "{app}\SkinManager.exe"; WorkingDir: "{app}"; IconFilename: "{app}\SkinManager.exe"
Name: "{autodesktop}\SkinManager"; Filename: "{app}\SkinManager.exe"; WorkingDir: "{app}"; IconFilename: "{app}\SkinManager.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "SkinManager"; Flags: uninsdeletevalue

[Code]
var
  RemoveSkinLibrary: Boolean;

function IsSkinManagerUpdate: Boolean;
begin
  Result := ExpandConstant('{param:SKINMANAGERUPDATE|0}') = '1';
end;

function StopInstalledAgent(var ExitCode: Integer): Boolean;
var
  AgentPath: String;
begin
  AgentPath := ExpandConstant('{app}\SkinManager.Agent.exe');
  if not FileExists(AgentPath) then begin
    ExitCode := 0;
    Result := True;
    exit;
  end;

  Result := Exec(AgentPath, '--stop', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ExitCode);
end;

function StopFailureMessage(ExitCode: Integer): String;
begin
  if ExitCode = 2 then
    Result := CustomMessage('StopFailureRecovered')
  else if ExitCode = 3 then
    Result := CustomMessage('StopFailureTimeout')
  else
    Result := FmtMessage(CustomMessage('StopFailure'), [IntToStr(ExitCode)]);
end;

function NormalizeAbsolutePath(const Path: String): String;
begin
  Result := RemoveBackslashUnlessRoot(PathNormalizeSlashes(ExpandFileName(Path)));
end;

function GetInstalledSkinPath: String;
var
  AppPath: String;
  AppPrefix: String;
  SkinPath: String;
begin
  Result := '';
  AppPath := NormalizeAbsolutePath(ExpandConstant('{app}'));
  SkinPath := NormalizeAbsolutePath(ExpandConstant('{app}\skin'));
  if not PathIsRooted(AppPath) or not PathIsRooted(SkinPath) then
    exit;
  if not PathSame(SkinPath, NormalizeAbsolutePath(PathCombine(AppPath, 'skin'))) then
    exit;

  AppPrefix := AppPath;
  if not PathSame(AppPath, ExtractFileDrive(AppPath) + '\') then
    AppPrefix := AppPath + '\';
  if not PathStartsWith(SkinPath, AppPrefix, True) then
    exit;
  Result := SkinPath;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
  if not StopInstalledAgent(ExitCode) then
    Result := CustomMessage('StopAgentPrepareFailed')
  else if ExitCode <> 0 then
    Result := StopFailureMessage(ExitCode);
end;

function InitializeUninstall: Boolean;
var
  ExitCode: Integer;
begin
  Result := StopInstalledAgent(ExitCode);
  if not Result then
    MsgBox(CustomMessage('StopAgentLaunchFailed'), mbError, MB_OK)
  else if ExitCode <> 0 then begin
    MsgBox(StopFailureMessage(ExitCode), mbError, MB_OK);
    Result := False;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppPath: String;
  SkinPath: String;
begin
  if CurUninstallStep = usUninstall then begin
    RemoveSkinLibrary := False;
    SkinPath := GetInstalledSkinPath;
    if (not UninstallSilent) and DirExists(SkinPath) then
      RemoveSkinLibrary := MsgBox(
        CustomMessage('RemoveSkinLibraryPrompt'),
        mbConfirmation,
        MB_YESNO or MB_DEFBUTTON2) = IDYES;
  end else if (CurUninstallStep = usPostUninstall) and RemoveSkinLibrary then begin
    SkinPath := GetInstalledSkinPath;
    if SkinPath = '' then
      MsgBox(CustomMessage('SkinLibraryPathNotConfirmed'), mbError, MB_OK)
    else if DirExists(SkinPath) and not DelTree(SkinPath, True, True, True) then
      MsgBox(CustomMessage('SkinLibraryDeleteIncomplete') + #13#10 + SkinPath, mbError, MB_OK)
    else begin
      AppPath := NormalizeAbsolutePath(ExpandConstant('{app}'));
      if not DirExists(SkinPath) then
        RemoveDir(AppPath);
    end;
  end;
end;

