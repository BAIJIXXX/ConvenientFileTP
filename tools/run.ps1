param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$AppArgs
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File (Join-Path $root 'launcher.ps1') @AppArgs
exit $LASTEXITCODE
