@echo off
rem ===================================================================
rem  启动鼠标电量托盘程序（无控制台窗口）
rem  双击本文件即可运行。首次运行会安装依赖。
rem ===================================================================

setlocal
cd /d "%~dp0"

rem 优先用 pythonw，避免留下黑色控制台窗口
set PY=pythonw
where pythonw >nul 2>nul
if errorlevel 1 set PY=python

rem 检查依赖，缺失则自动安装
python -c "import pystray, PIL, hid" >nul 2>nul
if errorlevel 1 (
    echo 正在安装依赖，请稍候...
    python -m pip install --quiet pystray Pillow hidapi
    if errorlevel 1 (
        echo.
        echo 依赖安装失败。请手动执行：
        echo     pip install pystray Pillow hidapi
        echo.
        pause
        exit /b 1
    )
)

rem 后台启动，不等待
start "" %PY% "%~dp0mouse_tray.py"

echo 鼠标电量已在任务栏启动。
echo 若图标藏在折叠区，请右键任务栏 -> 任务栏设置 -> 其他系统托盘图标，
echo 把「鼠标电量」打开；或运行 promote_icon.py 将其固定到可见区。
timeout /t 3 >nul
endlocal
