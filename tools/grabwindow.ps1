param(
    [int]$ProcessId = 0,
    [string]$TitleLike = 'ConvenientFileTP',
    [string]$Path = 'artifacts\window.png'
)

# NOTE: keep this file ASCII-only. PowerShell 5.1 reads BOM-less scripts as ANSI.
# Captures a single top-level window with PrintWindow (works even if the window is partly covered).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class WinGrab
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
'@

$proc = $null
if ($ProcessId -gt 0) {
    $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
}
if ($null -eq $proc) {
    $proc = Get-Process | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -like "*$TitleLike*" } |
        Sort-Object StartTime -Descending | Select-Object -First 1
}
if ($null -eq $proc) {
    throw "No window found for process '$ProcessId' / title '$TitleLike'."
}
$hwnd = $proc.MainWindowHandle
if ($hwnd -eq [IntPtr]::Zero) {
    throw "Process $($proc.Id) has no main window handle."
}

$rect = New-Object WinGrab+RECT
if (-not [WinGrab]::GetWindowRect($hwnd, [ref]$rect)) {
    throw "GetWindowRect failed for hwnd $hwnd."
}
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
if ($width -le 0 -or $height -le 0) {
    throw "Invalid window size ${width}x${height}."
}

$bmp = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $graphics.GetHdc()
try {
    # 2 = PW_RENDERFULLCONTENT (captures DirectComposition/WPF content).
    if (-not [WinGrab]::PrintWindow($hwnd, $hdc, 2)) {
        throw "PrintWindow failed."
    }
}
finally {
    $graphics.ReleaseHdc($hdc)
    $graphics.Dispose()
}

$dir = Split-Path -Parent $Path
if ($dir -and -not (Test-Path -LiteralPath $dir)) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}
$bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "saved: $Path (${width}x${height}, pid=$($proc.Id))"
