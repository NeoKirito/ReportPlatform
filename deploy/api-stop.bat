@echo off
setlocal
echo Stopping PEIS Report API...
taskkill /F /IM PEIS.Report.Api.exe >nul 2>&1
if %errorlevel%==0 (
    echo [OK] API stopped.
) else (
    echo [INFO] API was not running.
)
pause
