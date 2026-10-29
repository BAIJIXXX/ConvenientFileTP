@echo off
rem Creates a desktop shortcut with the app icon.
setlocal
set "ROOT=%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%ROOT%tools\create-shortcut.ps1"
if errorlevel 1 (
    echo Failed to create the shortcut. See README.
) else (
    echo Desktop shortcut created: ConvenientFileTP
)
pause
