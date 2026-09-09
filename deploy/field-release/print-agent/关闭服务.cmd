@echo off
chcp 65001 >nul
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\AgentService.ps1" -Action Stop
set "result=%errorlevel%"
if /I not "%~1"=="--no-pause" pause
exit /b %result%
