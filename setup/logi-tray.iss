; ============================================================================
;  完整版（WPF + 亚克力）安装入口
;  安装包产物：release\logi-tray-v<版本>-setup.exe
;
;  版本号由 tools\package_release.py 从 csproj 读出后用 /DAppVersion=... 传入；
;  手工编译时用下面的默认值（记得同步）。RepoRoot 默认是本文件的上一级目录，
;  因此仓库克隆到任意位置都能编译。
; ============================================================================
#ifndef RepoRoot
  // 本文件在 setup\ 下，SourcePath 是它的完整路径，取两次目录即仓库根
  #define RepoRoot ExtractFileDir(ExtractFileDir(SourcePath))
#endif

#define AppName  "logi-tray"
#define AppTitle "logi-tray"
#define AppSourceDir RepoRoot + "\wpf\bin\Release\net8.0-windows"
#define AppIconFile  RepoRoot + "\wpf\app.ico"
#define AppReadme    RepoRoot + "\setup\readme\logi-tray.txt"

#ifndef AppVersion
  #define AppVersion "1.2.4"
#endif

#include "app-common.iss"
