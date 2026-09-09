@echo off
if exist "%~dp0config.ini" (
    "%SystemRoot%\System32\notepad.exe" "%~dp0config.ini"
) else if exist "%~dp0agent.ini" (
    "%SystemRoot%\System32\notepad.exe" "%~dp0agent.ini"
) else (
    "%SystemRoot%\System32\notepad.exe" "%~dp0config.ini"
)
