@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Service.ps1" -Action Stop
set "result=%errorlevel%"
if /I not "%~1"=="--no-pause" pause
exit /b %result%
