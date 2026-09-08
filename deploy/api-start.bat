@echo off
setlocal
title PEIS Report API
cd /d "%~dp0"
taskkill /F /IM PEIS.Report.Api.exe >nul 2>&1
echo Starting PEIS Report API...
start "" /B PEIS.Report.Api.exe --urls "http://0.0.0.0:82"
timeout /t 3 /nobreak >nul
curl -s http://127.0.0.1:82/health >nul 2>&1
if %errorlevel%==0 (
    echo [OK] API started on http://0.0.0.0:82
) else (
    echo [WARN] API may still be starting, check http://127.0.0.1:82/health
)
pause
