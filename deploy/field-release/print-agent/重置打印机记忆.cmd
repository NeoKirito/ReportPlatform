@echo off
chcp 65001 >nul
setlocal
set "selectionFile=%ProgramData%\PEIS\PrintAgent\printer-selections.json"
if exist "%selectionFile%" (
    del /F /Q "%selectionFile%"
    echo [OK] 已清空打印机记忆：%selectionFile%
) else (
    echo [INFO] 未检测到历史打印机记忆文件。
)
echo 下次打印该体检单时将重新弹出打印机选择框。
pause
