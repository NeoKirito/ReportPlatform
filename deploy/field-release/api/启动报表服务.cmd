@echo off
cd /d "%~dp0"
set ASPNETCORE_ENVIRONMENT=Production
PEIS.Report.Api.exe
pause
