@echo off
setlocal
taskkill /F /IM PEIS.PrintAgent.exe >nul 2>&1
set "selectionFile=%ProgramData%\PEIS\PrintAgent\printer-selections.json"
if exist "%selectionFile%" del /F /Q "%selectionFile%"
echo Printer selections cleared. Restart PEIS.PrintAgent; each Djid will ask for a printer on its next interactive print.
pause
