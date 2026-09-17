@echo off
rem ============================================================
rem  DeepSeek Harness - portable one-click launcher (Windows)
rem  Double-click this file. Everything else is automatic:
rem    - first run: downloads portable Node.js + installs dsh
rem    - every run: detects local model servers, updates config,
rem                 starts the Web UI and opens the browser
rem  Works from any drive letter / any computer.
rem ============================================================
setlocal
title DeepSeek Harness Launcher
cd /d "%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start.ps1" %*

set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo.
  echo [start.bat] Launcher exited with code %EXITCODE%. See messages above.
  pause
)
endlocal
