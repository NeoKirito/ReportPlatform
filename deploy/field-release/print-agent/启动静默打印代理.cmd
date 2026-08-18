@echo off
cd /d "%~dp0"
set DOTNET_ENVIRONMENT=Production
PEIS.PrintAgent.exe
pause
