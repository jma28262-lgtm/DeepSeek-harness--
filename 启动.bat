@echo off
chcp 65001 >nul
rem DeepSeek Harness - 一键启动（中文别名，效果同 start.bat）
setlocal
title DeepSeek Harness Launcher
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start.ps1" %*
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo.
  echo [launcher] 退出代码 %EXITCODE%，请查看上方错误信息。
  pause
)
endlocal
