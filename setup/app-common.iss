; ============================================================================
;  logi-tray 安装工程 · 三个版本共用的主体
; ============================================================================
;  本文件不能单独编译，它被三个入口脚本 include：
;      setup\logi-tray.iss        完整版   logi-tray
;      setup\logi-tray-lite.iss   轻量版   logi-tray-lite
;      setup\multi-tray.iss       多品牌版 multi-tray
;
;  为什么拆成「共用主体 + 三个入口」：
;    三个版本的安装行为（部署目录、快捷方式、卸载清理、托盘常驻、.NET 检测）
;    必须完全一致，否则用户遇到的问题会因版本而异。这份逻辑只写一遍，
;    入口脚本只负责声明「我是谁、装哪个 exe、用哪个图标」。
;
;  打包入口：tools\package_release.py（先 dotnet build，再调用 ISCC 编译本工程）。
;  手工编译：ISCC.exe /DRepoRoot=... /DAppSourceDir=... setup\logi-tray.iss
; ============================================================================

#ifndef AppName
  #error 必须由入口脚本定义 AppName（logi-tray / logi-tray-lite / multi-tray）
#endif
#ifndef AppVersion
  #error 必须由入口脚本定义 AppVersion
#endif
#ifndef RepoRoot
  #error 必须定义 RepoRoot（仓库根目录）
#endif
#ifndef AppSourceDir
  #error 必须定义 AppSourceDir（dotnet build 的输出目录）
#endif
#ifndef AppIconFile
  #error 必须由入口脚本定义 AppIconFile
#endif
#ifndef AppReadme
  #error 必须由入口脚本定义 AppReadme
#endif
#ifndef AppTitle
  #define AppTitle AppName
#endif

; 三个版本刻意使用不同的 AppId，从而可以同时安装、同时运行、各自独立卸载。
; AppId 一旦发布就不能再改：Inno 靠它识别「这是同一个程序的另一个版本」。
#define AppPublisher "yixing233"
#define AppUrl       "https://github.com/yixing233/logi-tray"
#define AppExeName   AppName + ".exe"

[Setup]
AppId={#AppName}
AppName={#AppTitle}
AppVersion={#AppVersion}
AppVerName={#AppTitle} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}/releases/latest
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppTitle}
VersionInfoProductVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppTitle}

; ---- 装到当前用户目录，全程不弹 UAC ----
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppTitle}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
UsePreviousAppDir=yes

; ---- 单文件安装包 ----
OutputDir={#RepoRoot}\release
OutputBaseFilename={#AppName}-v{#AppVersion}-setup
SetupIconFile={#AppIconFile}
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppTitle}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableWelcomePage=no
AllowNoIcons=yes
LicenseFile={#RepoRoot}\LICENSE
CloseApplications=no
RestartApplications=no
; 用户配置（%LOCALAPPDATA%\{#AppName}）不随卸载删除，与旧版卸载脚本行为一致

[Languages]
; 官方安装器不自带简体中文，这是社区维护的完整翻译，随仓库一起分发。
Name: "zh"; MessagesFile: "{#RepoRoot}\setup\ChineseSimplified.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
zh.CreateDesktopIcon=创建桌面快捷方式(&D)
zh.AdditionalIcons=附加快捷方式：
zh.TasksGroupTitle=其它选项：
zh.TaskAutostart=开机自动启动（仅当前用户）
zh.NetMissingBody=本程序运行在微软的 .NET 8 桌面运行时之上，当前系统未检测到它。%n%n请先安装免费的 .NET Desktop Runtime 8.0（x64），装好后重新运行本安装包。%n%n是否现在打开官方下载页面？
zh.AppRunningBody=%1 正在运行，安装前需要先退出。%n%n是否自动关闭它并继续安装？（你的设置不会丢失）
zh.KillFailedBody=未能自动关闭 %1，请手动退出后重试。

en.CreateDesktopIcon=Create a &desktop shortcut
en.AdditionalIcons=Additional shortcuts:
en.TasksGroupTitle=Other options:
en.TaskAutostart=Start automatically at sign-in (current user only)
en.NetMissingBody=This program runs on Microsoft's .NET 8 Desktop Runtime, which was not found on this system.%n%nPlease install the free .NET Desktop Runtime 8.0 (x64) first, then run this installer again.%n%nOpen the official download page now?
en.AppRunningBody=%1 is running and must be closed before installing.%n%nClose it automatically and continue? (Your settings are preserved)
en.KillFailedBody=Could not close %1 automatically. Please exit it and try again.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
; 自启默认不勾选：程序自身的设置里 Autostart 默认关闭，安装器不该替用户
; 做一个他从未选择的决定。勾上才会写 Run 项。
Name: "autostart";   Description: "{cm:TaskAutostart}";   GroupDescription: "{cm:TasksGroupTitle}"; Flags: unchecked

[Files]
Source: "{#AppSourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Excludes: "*.pdb,*.xml,win-x64\*,win-x86\*,win-arm64\*"
Source: "{#RepoRoot}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppReadme}"; DestDir: "{app}"; DestName: "使用说明.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppTitle}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppTitle}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppTitle}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppTitle}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

; ============================================================================
;  [Code] —— 需要「有逻辑」的那几步
;   1) .NET 8 桌面运行时检测与引导下载
;   2) 程序正在运行时先请用户确认，再自动关闭（否则文件被占用装不上）
;   3) 「开机自动启动」任务：写当前用户的 Run 项
;   4) 把托盘图标从 Windows 11 的折叠区提出来
;   5) 卸载时清掉自启项与托盘注册记录
; ============================================================================

[Code]
const
  DotNetDesktopUrl  = 'https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0';
  RunKey            = 'Software\Microsoft\Windows\CurrentVersion\Run';
  NotifyIconKeyRoot = 'Control Panel\NotifyIconSettings';

var
  NetVersion: String;

{ -------------------------------------------------------------------------
  1. .NET 8 桌面运行时

  为什么查目录而不是注册表：.NET 的运行时安装信息并不保证写进
  HKLM\SOFTWARE\dotnet\Setup\InstalledVersions（本机实测装了 8.0.28 / 8.0.31
  仍查不到那个键），但共享框架目录一定存在。

  只认 8.x：程序的目标框架是 net8.0-windows，装 9.x 不能替代 8.x 的桌面运行时。
  找到则返回版本号（如 8.0.31），找不到返回空串。
  ------------------------------------------------------------------------- }
function FindNet8InSharedDir(SharedRoot: String): String;
var
  FindRec: TFindRec;
begin
  Result := '';
  if not FindFirst(SharedRoot + '\8.*', FindRec) then
    Exit;
  try
    repeat
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
      begin
        Result := FindRec.Name;
        Exit;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

function DetectNet8DesktopRuntime(): String;
begin
  { 只认 64 位那一份。

    安装包本身是 ArchitecturesAllowed=x64compatible，程序也是 x64；
    只装了 x86 版 .NET 8 的机器根本跑不起来。早先这里把 Program Files (x86)
    也当作命中，于是 x86-only 的机器会“检测通过”，装完双击却没反应。 }
  Result := FindNet8InSharedDir(
    ExpandConstant('{sd}\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App'));
end;

{ -------------------------------------------------------------------------
  2. 程序是否在运行

  为什么不用 Inno 的 AppMutex：三个版本各有自己的互斥体名，而安装包在
  程序「已经启动、但用户没退出」时最容易撞上文件占用。直接问系统有没有
  这个 exe 的进程更简单，也与语言区域无关（只看进程名，不看本地化输出）。
  ------------------------------------------------------------------------- }
function IsAppRunning(): Boolean;
var
  ResultCode: Integer;
  Lines: TArrayOfString;
  OutFile: String;
begin
  Result := False;
  OutFile := ExpandConstant('{tmp}\running.txt');

  { tasklist 带 /NH（不要表头）并把结果写到临时文件，再判断文件里有没有
    我们的进程名。这样可以完全避开各语言版本的表头文字差异。 }
  Exec(ExpandConstant('{cmd}'),
       '/c tasklist /FI "IMAGENAME eq {#AppExeName}" /NH > "' + OutFile + '"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  if not LoadStringsFromFile(OutFile, Lines) then
    Exit;

  { 没有匹配进程时 tasklist 输出一行「信息: 没有运行的任务...」，
    因此不能只看行数，必须看行里是否真的出现进程名。 }
  Result := (GetArrayLength(Lines) > 0)
            and (Pos(Lowercase('{#AppExeName}'), Lowercase(Lines[0])) > 0);
end;

procedure EnsureAppNotRunning();
var
  ResultCode: Integer;
begin
  if not IsAppRunning() then
    Exit;

  if MsgBox(FmtMessage(ExpandConstant('{cm:AppRunningBody}'), ['{#AppTitle}']),
            mbConfirmation, MB_YESNO or MB_DEFBUTTON1) <> IDYES then
    Abort;

  Exec(ExpandConstant('{cmd}'), '/c taskkill /F /IM "{#AppExeName}" >nul 2>nul',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  { 进程退出后文件句柄才释放；立刻复制文件仍可能撞上占用 }
  Sleep(900);

  if IsAppRunning() then
  begin
    MsgBox(FmtMessage(ExpandConstant('{cm:KillFailedBody}'), ['{#AppTitle}']),
           mbError, MB_OK);
    Abort;
  end;
end;

{ -------------------------------------------------------------------------
  3. 开机自启动

  任务勾选后写 HKCU 的 Run 项，值与程序内「开机自启」开关完全一致
  （同一个键名、同样指向安装目录下的 exe），因此设置页里的开关能正确
  反映并接管它的状态。取消勾选则删除该项。
  ------------------------------------------------------------------------- }
procedure SetAutoStart(const Enable: Boolean);
var
  Cmd: String;
begin
  Cmd := '"' + ExpandConstant('{app}\{#AppExeName}') + '"';
  if Enable then
    RegWriteStringValue(HKCU, RunKey, '{#AppName}', Cmd)
  else if RegValueExists(HKCU, RunKey, '{#AppName}') then
    RegDeleteValue(HKCU, RunKey, '{#AppName}');
end;

{ -------------------------------------------------------------------------
  4. 把托盘图标从 Windows 11 的折叠区提出来

  背景：Windows 在程序第一次注册托盘图标时，才在
  HKCU\Control Panel\NotifyIconSettings 下建立哈希名的项，里面有
  ExecutablePath 与 IsPromoted。新装程序的 IsPromoted 默认是空的，
  图标就蜷在 ^ 折叠区里，用户会以为「装完没反应」。

  所以安装结尾启动一次程序，等该项出现后把它置为 1。
  这一步失败无害（图标仍在，只是可能在折叠区），因此全程静默、不报错。
  ------------------------------------------------------------------------- }
function PromoteTrayIcon(): Boolean;
var
  Names: TArrayOfString;
  I, Attempt: Integer;
  Path, ExecPath: String;
begin
  Result := False;

  { 最多等 20 秒（40 × 500ms）：程序启动到注册托盘图标通常只要一两秒，
    但冷启动 + 首次读 HID 会慢一些。等不到也不影响安装结果。 }
  for Attempt := 1 to 40 do
  begin
    if RegGetSubkeyNames(HKCU, NotifyIconKeyRoot, Names) then
      for I := 0 to GetArrayLength(Names) - 1 do
      begin
        Path := NotifyIconKeyRoot + '\' + Names[I];
        if RegQueryStringValue(HKCU, Path, 'ExecutablePath', ExecPath)
           and (Pos(Lowercase(ExpandConstant('{app}\{#AppExeName}')), Lowercase(ExecPath)) > 0) then
        begin
          RegWriteDWordValue(HKCU, Path, 'IsPromoted', 1);
          Result := True;
          Exit;
        end;
      end;
    Sleep(500);
  end;
end;

procedure LaunchAndPromote();
var
  ResultCode: Integer;
begin
  { 已经在运行（用户安装前没关、我们又替他关掉了的话这里不会重复启动） }
  if not IsAppRunning() then
    Exec(ExpandConstant('{app}\{#AppExeName}'), '', ExpandConstant('{app}'),
         SW_SHOWNORMAL, ewNoWait, ResultCode);

  PromoteTrayIcon();
end;

{ -------------------------------------------------------------------------
  5. 安装 / 卸载钩子
  ------------------------------------------------------------------------- }
function InitializeSetup(): Boolean;
var
  Answer: Integer;
begin
  Result := True;

  NetVersion := DetectNet8DesktopRuntime();
  if NetVersion = '' then
  begin
    Answer := MsgBox(ExpandConstant('{cm:NetMissingBody}'), mbCriticalError,
                     MB_YESNO or MB_DEFBUTTON1);
    if Answer = IDYES then
      ShellExec('open', DotNetDesktopUrl, '', '', SW_SHOWNORMAL, ewNoWait, Answer);
    Result := False;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    EnsureAppNotRunning();

    { 迁移：旧 .bat 安装方式把快捷方式直接放在开始菜单 Programs 根下，
      新版放在 Programs\{#AppTitle}\ 里。不删掉旧的会出现两个同名入口，
      其中一个指向已被覆盖的旧路径。 }
    DeleteFile(ExpandConstant('{userprograms}\{#AppTitle}.lnk'));
  end;

  if CurStep = ssPostInstall then
  begin
    { 只在用户勾了「开机自动启动」时**写入**；**不在安装时删除**已存在的自启项。

      任务页默认不勾（首次安装不该替用户做决定），但“这次没勾”并不等于
      “用户想关掉自启”——早先这里无条件 SetAutoStart(WizardIsTaskSelected(...))，
      于是覆盖安装一次就把用户已经在程序里开好的 Run 项删掉了；静默部署
      （/VERYSILENT）下更不会被随后的启动补回来。

      安装完成后自启状态以程序内的设置为准（启动时 AutoStartService 会按
      settings.json 落盘/清除，并自动修正被移动过的程序路径），卸载时仍会
      清理，不会留下指向已删文件的 Run 项。 }
    if WizardIsTaskSelected('autostart') then
      SetAutoStart(True);
  end;

  if CurStep = ssDone then
  begin
    { 静默安装（批量部署）不擅自拉起界面，与 [Run] 的 skipifsilent 保持一致。
      代价是这次不会把托盘图标提出折叠区，首次手动启动后用户在 ^ 里能找到它。 }
    if not WizardSilent then
      LaunchAndPromote();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Names: TArrayOfString;
  I: Integer;
  Path, ExecPath: String;
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    { 先退进程：否则安装目录删不干净，托盘图标还会留一个「幽灵」 }
    Exec(ExpandConstant('{cmd}'), '/c taskkill /F /IM "{#AppExeName}" >nul 2>nul',
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(600);

    { 自启项：不清理的话，卸载后每次开机都会弹「找不到文件」 }
    if RegValueExists(HKCU, RunKey, '{#AppName}') then
      RegDeleteValue(HKCU, RunKey, '{#AppName}');

    { 托盘注册记录：留着会在通知区域设置里堆积无主条目 }
    if RegGetSubkeyNames(HKCU, NotifyIconKeyRoot, Names) then
      for I := 0 to GetArrayLength(Names) - 1 do
      begin
        Path := NotifyIconKeyRoot + '\' + Names[I];
        if RegQueryStringValue(HKCU, Path, 'ExecutablePath', ExecPath)
           and (Pos(Lowercase(ExpandConstant('{app}\{#AppExeName}')), Lowercase(ExecPath)) > 0) then
          RegDeleteKeyIncludingSubkeys(HKCU, Path);
      end;

    { 旧 .bat 方式可能在桌面留下同名快捷方式 }
    DeleteFile(ExpandConstant('{userdesktop}\{#AppTitle}.lnk'));
  end;
end;