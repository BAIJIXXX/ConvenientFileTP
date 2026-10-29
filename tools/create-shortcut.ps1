# Creates a desktop shortcut that silently starts ConvenientFileTP. ASCII-only.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$launcher = Join-Path $root 'launcher.ps1'
$icon = Join-Path $root 'assets\app.ico'
if (-not (Test-Path -LiteralPath $launcher)) { throw "launcher.ps1 not found: $launcher" }

# The shell may refuse to read icons inside an ACL-restricted workspace folder
# (DSH sandbox marks the project directory), which makes the shortcut fall back to
# a generic icon. Keep a per-user copy and point the shortcut at that copy.
$iconStore = $null
if (Test-Path -LiteralPath $icon) {
    $iconDir = Join-Path $env:LOCALAPPDATA 'ConvenientFileTP'
    if (-not (Test-Path -LiteralPath $iconDir)) { New-Item -ItemType Directory -Force -Path $iconDir | Out-Null }
    $iconStore = Join-Path $iconDir 'app.ico'
    Copy-Item -LiteralPath $icon -Destination $iconStore -Force
}

$ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$desktop = [Environment]::GetFolderPath('Desktop')
$linkPath = Join-Path $desktop 'ConvenientFileTP.lnk'

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($linkPath)
$shortcut.TargetPath = $ps
$shortcut.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "' + $launcher + '"'
$shortcut.WorkingDirectory = $root
$shortcut.Description = 'ConvenientFileTP - LAN file transfer'
if ($iconStore) { $shortcut.IconLocation = $iconStore + ',0' }
$shortcut.WindowStyle = 7
$shortcut.Save()

Write-Host "shortcut created: $linkPath"
if ($iconStore) { Write-Host "shortcut icon: $iconStore" }
