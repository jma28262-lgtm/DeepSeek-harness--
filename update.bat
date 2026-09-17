@echo off
rem DeepSeek Harness - update dsh to the latest npm release
setlocal
title DeepSeek Harness Updater
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0update.ps1" %*
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo.
  echo [update.bat] Update exited with code %EXITCODE%. See messages above.
  pause
)
endlocal
