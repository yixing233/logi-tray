"""Package both editions of logi-tray into release zips.

Usage:
    python tools/package_release.py

Produces two independent archives so users pick the edition they want:
    release/logi-tray-vX.Y.Z.zip        full edition (WPF + acrylic, 3 icon styles)
    release/logi-tray-lite-vX.Y.Z.zip   lightweight edition (WinForms, number icon)

Each archive gets its own installer, uninstaller and readme, generated from the
templates below so the two cannot drift apart.
"""
import os
import re
import shutil
import subprocess
import zipfile

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RELEASE = os.path.join(REPO, "release")


def csproj_version(path):
    with open(path, encoding="utf-8") as f:
        m = re.search(r"<Version>([^<]+)</Version>", f.read())
    return m.group(1) if m else "0.0.0"


FULL_VER = csproj_version(os.path.join(REPO, "wpf", "MouseBatteryTray.csproj"))
LITE_VER = csproj_version(os.path.join(REPO, "lite", "LogiTrayLite.csproj"))
print(f"full version = {FULL_VER}")
print(f"lite version = {LITE_VER}")

INSTALLER = r"""@echo off
chcp 65001 >nul
title {title} · 一键安装向导
setlocal enabledelayedexpansion

echo ======================================================
echo        {title} · 一键安装向导
echo ======================================================
echo.

set "TARGET_DIR=%LOCALAPPDATA%\Programs\{app}"
set "NET8_URL=https://dotnet.microsoft.com/download/dotnet/8.0"

echo [1/6] 正在检测 .NET 8 桌面运行时...
set "HAS_NET8="
for %%D in ("%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App") do (
  if exist %%D (
    for /f "delims=" %%V in ('dir /b /ad %%D 2^>nul ^| findstr /b "8."') do set "HAS_NET8=%%V"
  )
)
if not defined HAS_NET8 (
  for %%D in ("%ProgramFiles(x86)%\dotnet\shared\Microsoft.WindowsDesktop.App") do (
    if exist %%D (
      for /f "delims=" %%V in ('dir /b /ad %%D 2^>nul ^| findstr /b "8."') do set "HAS_NET8=%%V"
    )
  )
)

if defined HAS_NET8 (
  echo       [OK] 已检测到 .NET 8 桌面运行时 ^(!HAS_NET8!^)
) else (
  echo       [X] 未检测到 .NET 8 桌面运行时！
  echo.
  echo       {title} 需要微软官方免费的 .NET 8 Desktop Runtime。
  echo       即将为你打开官方下载页面，请下载并安装：
  echo         .NET Desktop Runtime 8.0 - x64
  echo       下载地址: %NET8_URL%
  echo.
  choice /c YN /n /m "      是否现在打开下载页面？(Y/N) "
  if errorlevel 2 goto :net8_hint
  start "" "%NET8_URL%"
  goto :net8_hint
)

echo.
echo [2/6] 正在关闭旧版进程...
taskkill /f /im {app}.exe >nul 2>nul
taskkill /f /im mouse-tray.exe >nul 2>nul
taskkill /f /im logi-tray.exe >nul 2>nul
taskkill /f /im logi-tray-lite.exe >nul 2>nul

echo [3/6] 正在部署程序文件至 %TARGET_DIR%
if not exist "%TARGET_DIR%" mkdir "%TARGET_DIR%" >nul 2>nul
xcopy /y /e /q "%~dp0*.*" "%TARGET_DIR%\" >nul
if errorlevel 1 (
  echo       [X] 文件复制失败，请检查磁盘权限。
  pause
  exit /b 1
)

echo [4/6] 正在创建快捷方式...
set "LNK_NAME={title}.lnk"
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ws = New-Object -ComObject WScript.Shell;" ^
  "$dirs = @('%APPDATA%\Microsoft\Windows\Start Menu\Programs', [Environment]::GetFolderPath('Desktop'));" ^
  "foreach ($d in $dirs) {{ $p = Join-Path $d '%LNK_NAME%'; $s = $ws.CreateShortcut($p); $s.TargetPath = '%TARGET_DIR%\{app}.exe'; $s.IconLocation = '%TARGET_DIR%\app.ico,0'; $s.WorkingDirectory = '%TARGET_DIR%'; $s.Description = '{title}'; $s.Save() }}"

echo [5/6] 正在启动 {title}...
start "" "%TARGET_DIR%\{app}.exe"

REM ---------------------------------------------------------------
REM  任务栏常驻显示：必须在程序**启动之后**再做。
REM
REM  Windows 是在程序首次注册托盘图标时才创建
REM  HKCU\Control Panel\NotifyIconSettings 下对应的项。
REM  首次安装时程序还没运行过，此时去设置 IsPromoted 找不到目标，
REM  图标就会一直蜷在任务栏的折叠区里。
REM  因此这里先启动程序，轮询等待注册表项出现，再置为常驻显示。
REM ---------------------------------------------------------------
echo.
echo       正在配置任务栏常驻显示（等待托盘图标注册）...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$key = 'HKCU:\Control Panel\NotifyIconSettings';" ^
  "$deadline = (Get-Date).AddSeconds(25);" ^
  "$done = $false;" ^
  "while (-not $done -and (Get-Date) -lt $deadline) {{" ^
  "  Start-Sleep -Milliseconds 500;" ^
  "  if (-not (Test-Path $key)) {{ continue }}" ^
  "  Get-ChildItem $key -ErrorAction SilentlyContinue | ForEach-Object {{" ^
  "    $v = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue;" ^
  "    if ($v.ExecutablePath -like '*{app}.exe*') {{" ^
  "      Set-ItemProperty $_.PSPath -Name 'IsPromoted' -Value 1 -ErrorAction SilentlyContinue;" ^
  "      $script:done = $true" ^
  "    }}" ^
  "  }}" ^
  "}};" ^
  "if ($done) {{ Write-Host '      [OK] 已设为常驻显示' }} else {{ Write-Host '      [i] 未能自动设置，可从任务栏折叠区拖出图标' }}"

echo.
echo ======================================================
echo   [OK] 安装完成！
echo ------------------------------------------------------
echo   程序已常驻屏幕右下角任务栏通知区域。
{usage}
echo.
echo   如需卸载，请运行安装目录下的 卸载.bat
echo ======================================================
echo.
pause
exit /b 0

:net8_hint
echo.
echo ------------------------------------------------------
echo   请先安装 .NET 8 Desktop Runtime 后再运行本脚本。
echo   官方下载: %NET8_URL%
echo ------------------------------------------------------
echo.
pause
exit /b 1
"""

UNINSTALLER = r"""@echo off
chcp 65001 >nul
title {title} · 卸载向导
echo ======================================================
echo                {title} · 卸载向导
echo ======================================================
echo.

set "TARGET_DIR=%LOCALAPPDATA%\Programs\{app}"

echo [1/5] 正在停止进程...
taskkill /f /im {app}.exe >nul 2>nul
taskkill /f /im mouse-tray.exe >nul 2>nul
timeout /t 1 >nul

echo [2/5] 正在清理开机自启项...
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "{app}" /f >nul 2>nul

echo [3/5] 正在清理快捷方式...
del /q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\{title}.lnk" >nul 2>nul
del /q "%USERPROFILE%\Desktop\{title}.lnk" >nul 2>nul

echo [4/5] 正在移除程序文件...
if exist "%TARGET_DIR%" rd /s /q "%TARGET_DIR%" >nul 2>nul

echo [5/5] 正在清理托盘图标注册记录...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$key = 'HKCU:\Control Panel\NotifyIconSettings';" ^
  "if (Test-Path $key) {{ Get-ChildItem $key | ForEach-Object {{ $v = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue; if ($v.ExecutablePath -like '*{app}.exe*') {{ Remove-Item $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue }} }} }}"

echo.
echo ======================================================
echo   [OK] {title} 已从您的电脑中完整卸载。
echo.
echo   注：用户配置与历史数据保留在
echo       %LOCALAPPDATA%\{data}
echo   如需彻底清除，请手动删除该文件夹。
echo ======================================================
echo.
pause
exit /b 0
"""

USAGE_FULL = r"""{title} v{version} · 使用说明
==================================================

【两个版本怎么选】
  完整版   logi-tray        带亚克力毛玻璃效果，支持三种托盘图标样式与主题切换。
  轻量版   logi-tray-lite   纯 WinForms 实现，内存占用显著更低，托盘图标固定为数字样式。
  两者数据目录相互独立，可同时安装、同时运行。

【快速开始】
  1. 运行「一键安装.bat」（无需管理员权限）
  2. 托盘图标即刻出现在任务栏右下角
  3. 若图标被折叠，点击任务栏的 ^ 箭头即可看到

【日常操作】
  · 左键点击托盘图标    展开电量详情卡片
  · 右键点击托盘图标    打开菜单（详情 / 设置 / 图标样式 / 外观主题 / 退出）

【设置中心】
  · 电量阈值与提醒      自定义低电量与严重低电量阈值，可开关桌面通知
  · 托盘图标样式        电池胶囊 / 环形进度 / 纯数字
  · 外观主题            跟随系统 / 浅色模式 / 深色模式
  · 亚克力效果          可关闭改用纯色底板，兼顾性能
  · 开机自动启动        仅写入当前用户注册表
  · 后台刷新间隔        10 秒 ~ 2 分钟

【绿色便携】
  也可直接双击 {app}.exe 运行，不写入任何系统位置。
  配置文件位于 %LOCALAPPDATA%\{data}

【常见问题】
  Q: 显示「设备离线或休眠」？
  A: 鼠标长时间未操作会进入休眠，动一下鼠标即可恢复读数。

  Q: 提示缺少 .NET 8？
  A: 安装脚本会自动检测并引导下载微软官方的
     .NET Desktop Runtime 8.0.x（Windows x64，免费）。
"""

USAGE_LITE = r"""{title} v{version} · 使用说明
==================================================

【这个版本是什么】
  轻量版：纯 WinForms 实现，不加载 WPF 渲染栈，
  因此内存占用显著低于完整版。代价是界面为朴素风格，没有亚克力效果，
  托盘图标固定为纯数字样式。

  需要亚克力毛玻璃与三种图标样式，请改用完整版 logi-tray。

【快速开始】
  1. 运行「一键安装.bat」（无需管理员权限）
  2. 托盘图标即刻出现在任务栏右下角
  3. 若图标被折叠，点击任务栏的 ^ 箭头即可看到

【日常操作】
  · 左键点击托盘图标    展开电量详情卡片
  · 右键点击托盘图标    打开菜单（显示详情 / 设置 / 退出）

【设置】
  · 电量阈值与提醒      自定义低电量与严重低电量阈值，可开关桌面通知
  · 开机自动启动        仅写入当前用户注册表
  · 后台刷新间隔        10 秒 ~ 2 分钟
  · 关于                查看版本、作者、开源协议，并检查更新

【外观】
  自动跟随 Windows 的浅色 / 深色应用主题，无需手动切换。

【绿色便携】
  也可直接双击 {app}.exe 运行，不写入任何系统位置。
  配置文件位于 %LOCALAPPDATA%\{data}

【常见问题】
  Q: 显示「设备离线或休眠」？
  A: 鼠标长时间未操作会进入休眠，动一下鼠标即可恢复读数。

  Q: 托盘图标可以换成电池或环形吗？
  A: 轻量版只提供数字样式；如需切换请使用完整版。

  Q: 提示缺少 .NET 8？
  A: 安装脚本会自动检测并引导下载微软官方的
     .NET Desktop Runtime 8.0.x（Windows x64，免费）。
"""


def build(project_rel):
    r = subprocess.run(["dotnet", "build", project_rel, "-c", "Release", "-v", "minimal"],
                       cwd=REPO, capture_output=True, text=True, errors="replace")
    if r.returncode != 0:
        print(f"  BUILD FAILED for {project_rel}")
        print(r.stdout[-1500:])
        return False
    print(f"  built {project_rel}")
    return True


def copy_app_body(src_dir, dst_dir, title, app, data_dir, version, usage):
    """Copy the app body into the package.

    Copies recursively (the lucide icon font lives in Fonts/, and without it the
    About page and settings icons render as tofu), while skipping build noise and
    the self-contained win-x64 runtime folder that a previous publish may have
    left behind -- that folder is ~200 MB and the README documents installing the
    shared .NET 8 Desktop Runtime instead.
    """
    os.makedirs(dst_dir, exist_ok=True)
    copied = []

    for root, dirs, files in os.walk(src_dir):
        dirs[:] = [d for d in dirs if d.lower() not in ("win-x64", "win-x86", "win-arm64")]

        for name in files:
            if name.endswith((".pdb", ".xml")):
                continue
            src = os.path.join(root, name)
            rel = os.path.relpath(src, src_dir)
            if rel.startswith(".."):
                continue
            dst = os.path.join(dst_dir, rel)
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            shutil.copy2(src, dst)
            copied.append(rel.replace("\\", "/"))

    shutil.copy2(os.path.join(REPO, "LICENSE"), os.path.join(dst_dir, "LICENSE"))

    with open(os.path.join(dst_dir, "一键安装.bat"), "w", encoding="utf-8") as f:
        f.write(INSTALLER.format(title=title, app=app, usage=usage["install"]))
    with open(os.path.join(dst_dir, "卸载.bat"), "w", encoding="utf-8") as f:
        f.write(UNINSTALLER.format(title=title, app=app, data=data_dir))
    with open(os.path.join(dst_dir, "使用说明.txt"), "w", encoding="utf-8") as f:
        f.write(usage["text"].format(title=title, app=app, data=data_dir, version=version))

    return sorted(copied)


def zip_dir(src, dst):
    if os.path.exists(dst):
        os.remove(dst)
    with zipfile.ZipFile(dst, "w", zipfile.ZIP_DEFLATED) as z:
        for root, _, files in os.walk(src):
            for f in files:
                full = os.path.join(root, f)
                z.write(full, os.path.relpath(full, src))
    return os.path.getsize(dst)


os.makedirs(RELEASE, exist_ok=True)

FULL_USAGE = {
    "text": USAGE_FULL,
    "install": (
        "echo     · 左键点击托盘图标：展开亚克力电量详情面板\n"
        "echo     · 右键点击托盘图标：切换图标样式 / 外观主题 / 设置\n"
    ),
}
LITE_USAGE = {
    "text": USAGE_LITE,
    "install": (
        "echo     · 左键点击托盘图标：展开电量详情卡片\n"
        "echo     · 右键点击托盘图标：显示详情 / 设置 / 退出\n"
    ),
}

# ---------- full edition ----------
print("\n=== full edition ===")
if not build(os.path.join("wpf", "MouseBatteryTray.csproj")):
    raise SystemExit(1)

full_src = os.path.join(REPO, "wpf", "bin", "Release", "net8.0-windows")
full_out = os.path.join(RELEASE, f"logi-tray-v{FULL_VER}")
shutil.rmtree(full_out, ignore_errors=True)
files = copy_app_body(full_src, full_out, "logi-tray", "logi-tray", "logi-tray",
                      FULL_VER, FULL_USAGE)
print(f"  packaged {len(files)} files -> {os.path.basename(full_out)}")

full_zip = os.path.join(RELEASE, f"logi-tray-v{FULL_VER}.zip")
size = zip_dir(full_out, full_zip)
print(f"  zip: {os.path.basename(full_zip)}  {size/1024:.0f} KB")

# ---------- lite edition ----------
print("\n=== lite edition ===")
if not build(os.path.join("lite", "LogiTrayLite.csproj")):
    raise SystemExit(1)

lite_src = os.path.join(REPO, "lite", "bin", "Release", "net8.0-windows")
lite_out = os.path.join(RELEASE, f"logi-tray-lite-v{LITE_VER}")
shutil.rmtree(lite_out, ignore_errors=True)
files = copy_app_body(lite_src, lite_out, "logi-tray-lite", "logi-tray-lite",
                      "logi-tray-lite", LITE_VER, LITE_USAGE)
print(f"  packaged {len(files)} files -> {os.path.basename(lite_out)}")

lite_zip = os.path.join(RELEASE, f"logi-tray-lite-v{LITE_VER}.zip")
size = zip_dir(lite_out, lite_zip)
print(f"  zip: {os.path.basename(lite_zip)}  {size/1024:.0f} KB")

print("\n=== summary ===")
for z in (full_zip, lite_zip):
    print(f"  {os.path.basename(z)}  {os.path.getsize(z)/1024:.0f} KB")
