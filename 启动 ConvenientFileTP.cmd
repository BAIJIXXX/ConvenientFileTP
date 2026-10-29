@echo off
rem ConvenientFileTP launcher. Starts the signed PowerShell host hidden, which loads
rem app\ConvenientFileTP.dll from memory (Smart App Control blocks local exe/dll execution).
setlocal
set "ROOT=%~dp0"
start "" /b powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "%ROOT%launcher.ps1"
exit /b 0
