param(
    [Parameter(Mandatory = $true)]
    [string]$Path,
    [int]$Delay = 1
)

# NOTE: keep this file ASCII-only. PowerShell 5.1 reads BOM-less scripts as ANSI.
$ErrorActionPreference = 'Stop'
if ($Delay -gt 0) { Start-Sleep -Seconds $Delay }
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$dir = Split-Path -Parent $Path
if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

$bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bitmap = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $bitmap.Size)
    $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "screenshot saved: $Path"
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}
