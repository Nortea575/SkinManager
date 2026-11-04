#define ProductVersion "1.0.0"

[Setup]
AppId={{6FBD8E7A-8A61-4DB8-BD4A-4F2E43110B67}
AppName=SkinManager
AppVersion={#ProductVersion}
AppVerName=SkinManager {#ProductVersion}
AppPublisher=SkinManager
VersionInfoVersion=1.0.0.0
VersionInfoProductVersion=1.0.0
VersionInfoDescription=SkinManager Setup
VersionInfoProductName=SkinManager
VersionInfoCompany=SkinManager
VersionInfoOriginalFileName=SkinManager-Setup-1.0.0.exe
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
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："; Flags: unchecked

[Files]
Source: "..\..\artifacts\publish\installer-stage\SkinManager.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\artifacts\publish\installer-stage\SkinManager.Agent.exe"; DestDir: "{app}"; Flags: ignoreversion

[Run]
Filename: "{app}\SkinManager.exe"; Flags: nowait; Check: IsSkinManagerUpdate

[Icons]
Name: "{autoprograms}\SkinManager"; Filename: "{app}\SkinManager.exe"; WorkingDir: "{app}"; IconFilename: "{app}\SkinManager.exe"
Name: "{autodesktop}\SkinManager"; Filename: "{app}\SkinManager.exe"; WorkingDir: "{app}"; IconFilename: "{app}\SkinManager.exe"; Tasks: desktopicon

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
    Result := '皮肤后台未能确认已恢复当前页面。安装或卸载已中止；请打开 SkinManager，断开连接后再重试。'
  else if ExitCode = 3 then
    Result := '等待皮肤后台退出超时。安装或卸载已中止；请关闭 SkinManager 后再重试。'
  else
    Result := '停止皮肤后台失败（退出代码 ' + IntToStr(ExitCode) + '）。安装或卸载已中止；请关闭 SkinManager 后再重试。';
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
    Result := '无法启动 SkinManager.Agent.exe 来停止本目录中的皮肤后台。请关闭 SkinManager 后重试。'
  else if ExitCode <> 0 then
    Result := StopFailureMessage(ExitCode);
end;

function InitializeUninstall: Boolean;
var
  ExitCode: Integer;
begin
  Result := StopInstalledAgent(ExitCode);
  if not Result then
    MsgBox('无法启动皮肤后台停止命令。卸载已中止；请关闭 SkinManager 后重试。', mbError, MB_OK)
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
        '是否删除皮肤库？选择“是”会删除此目录中的默认皮肤、导入皮肤和用户修改；选择“否”会保留整个皮肤库。',
        mbConfirmation,
        MB_YESNO or MB_DEFBUTTON2) = IDYES;
  end else if (CurUninstallStep = usPostUninstall) and RemoveSkinLibrary then begin
    SkinPath := GetInstalledSkinPath;
    if SkinPath = '' then
      MsgBox('无法确认皮肤库位于安装目录中。为避免删除其他路径，皮肤库已保留。', mbError, MB_OK)
    else if DirExists(SkinPath) and not DelTree(SkinPath, True, True, True) then
      MsgBox('未能完整删除皮肤库，仍有文件保留在：' + #13#10 + SkinPath, mbError, MB_OK)
    else begin
      AppPath := NormalizeAbsolutePath(ExpandConstant('{app}'));
      if not DirExists(SkinPath) then
        RemoveDir(AppPath);
    end;
  end;
end;

