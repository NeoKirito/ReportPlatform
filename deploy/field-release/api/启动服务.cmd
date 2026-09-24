@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"
set "ACTION=Start"
if /I "%~1"=="--alwaysup" set "ACTION=Run"
if /I "%~1"=="--run" set "ACTION=Run"
if /I "%~1"=="--console" set "ACTION=Run"
if /I "%~1"=="--foreground" set "ACTION=Run"

"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Service.ps1" -Action %ACTION%
set "result=%errorlevel%"
if /I not "%ACTION%"=="Run" if /I not "%~1"=="--no-pause" pause
exit /b %result%
