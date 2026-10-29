param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$AppArgs
)

# NOTE: keep this file ASCII-only. PowerShell 5.1 reads BOM-less scripts as ANSI,
# so Chinese text in .ps1 files gets corrupted and can break parsing.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dllPath = Join-Path $root 'app\ConvenientFileTP.dll'

function Show-Error([string]$message) {
    try {
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show($message, 'ConvenientFileTP', 'OK', 'Error') | Out-Null
    } catch {
        Write-Error $message
    }
}

if (-not (Test-Path -LiteralPath $dllPath)) {
    Show-Error ("App payload not found:`r`n" + $dllPath + "`r`n`r`n" +
        "The package is incomplete or was extracted incorrectly.`r`n" +
        "1) Use the portable zip (artifacts\ConvenientFileTP-1.0.0.zip), not the GitHub source zip.`r`n" +
        "2) Extract it with Explorer or 7-Zip and keep the folder structure (an 'app' folder next to launcher.ps1).`r`n" +
        "3) If a security tool removed the DLL, restore/allow it and extract again.`r`n" +
        "4) Source-only checkout: run tools\build.ps1 first (requires .NET SDK).")
    exit 1
}

try {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    Add-Type -AssemblyName System.Web.Extensions

    $bytes = [System.IO.File]::ReadAllBytes($dllPath)
    $assembly = [System.Reflection.Assembly]::Load($bytes)

    $entryType = $assembly.GetType('ConvenientFileTP.AppEntry', $true)
    $runMethod = $entryType.GetMethod('Run', [System.Reflection.BindingFlags]'Public,Static')
    if ($null -eq $runMethod) {
        throw 'AppEntry.Run entry point not found.'
    }

    if ($null -eq $AppArgs) { $AppArgs = @() }
    if (-not ($AppArgs -contains '--root')) {
        $AppArgs = @('--root', $root) + $AppArgs
    }

    # Reflection needs a real object[]; otherwise PowerShell wraps the string[] in a PSObject.
    $result = $runMethod.Invoke($null, [object[]]@(, [string[]]$AppArgs))
    exit [int]$result
}
catch {
    Show-Error "Failed to start: $($_.Exception.Message)"
    exit 1
}
