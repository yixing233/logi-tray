@echo off
REM ============================================================================
REM  build.bat - Build native mouse-tray with the local MSVC toolchain
REM
REM  NOTE: This file is deliberately ASCII-only.
REM  cmd.exe parses .bat files using the OEM code page (GBK on this machine),
REM  so UTF-8 Chinese comments get split and the batch fails with
REM  "is not recognized as an internal or external command".
REM  Do NOT put non-ASCII text in this file.
REM
REM  Usage:  build.bat          build Release
REM          build.bat debug    build Debug
REM          build.bat clean    remove build output
REM ============================================================================

setlocal enabledelayedexpansion

set ROOT=%~dp0
set SRC=%ROOT%src
set OUT=%ROOT%build
set OBJ=%OUT%\obj

set CONFIG=%1
if /I "%CONFIG%"=="clean" goto :clean

if /I "%CONFIG%"=="debug" (
  set "OPT=/Od /Zi /MDd"
  set "OUTNAME=mouse-tray-d.exe"
) else (
  set "OPT=/O2 /Oi /MD /DNDEBUG"
  set "OUTNAME=mouse-tray.exe"
)

REM ---- Locate MSVC environment
set "VCVARS="
for %%P in (
  "C:\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
  "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
  "C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat"
  "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat"
) do if exist %%P set "VCVARS=%%~P"

if "%VCVARS%"=="" (
  echo [ERROR] vcvars64.bat not found. Install Visual Studio Build Tools.
  exit /b 1
)

if not exist "%OBJ%" mkdir "%OBJ%" 2>nul

echo ============================================================
echo  Building native mouse-tray  [%CONFIG%]
echo ============================================================
echo  MSVC : %VCVARS%
echo  Out  : %OUT%
echo.

call "%VCVARS%" >nul 2>&1
if errorlevel 1 (
  echo [ERROR] failed to initialise vcvars64
  exit /b 1
)

REM ---- Source files (added stage by stage)
set "SOURCES=%SRC%\main.cpp %SRC%\hidpp.cpp %SRC%\util.cpp %SRC%\battery_history.cpp %SRC%\alerts.cpp %SRC%\glass_renderer.cpp %SRC%\capture.cpp %SRC%\tray.cpp %SRC%\config.cpp %SRC%\app.cpp %SRC%\popup.cpp %SRC%\settings_window.cpp"

REM ---- Compiler flags
REM   /utf-8   sources are UTF-8 (Chinese comments); without this MSVC
REM            assumes GBK and fails on the string literals
REM   /W4      high warning level
REM   /EHsc    standard C++ exceptions

REM   NOMINMAX windows.h defines min/max macros which break

REM            std::min / std::max and even argument lists
set "CFLAGS=/nologo /utf-8 /W4 /std:c++17 /EHsc /DNOMINMAX /DWIN32_LEAN_AND_MEAN %OPT% /I"%SRC%""

REM ---- Libraries
set "LIBS=hid.lib setupapi.lib user32.lib gdi32.lib shell32.lib shlwapi.lib ole32.lib advapi32.lib d3d11.lib dxgi.lib d3dcompiler.lib dwmapi.lib msimg32.lib userenv.lib gdiplus.lib comctl32.lib"

echo [1/1] compiling...
cl %CFLAGS% /Fo"%OBJ%\\" /Fe"%OUT%\%OUTNAME%" %SOURCES% /link %LIBS%
if errorlevel 1 (
  echo.
  echo [FAILED] compilation errors
  exit /b 1
)

echo.
echo [OK] %OUT%\%OUTNAME%
for %%F in ("%OUT%\%OUTNAME%") do echo      size: %%~zF bytes
exit /b 0

:clean
if exist "%OUT%" rmdir /s /q "%OUT%"
echo cleaned %OUT%
exit /b 0
