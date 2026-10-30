# Builds the portable zip (runtime + source + docs + tools + screenshots). ASCII-only.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path -Parent $PSScriptRoot
$dll = Join-Path $root 'app\ConvenientFileTP.dll'
if (-not (Test-Path -LiteralPath $dll)) { throw "Missing build output: $dll - run tools\build.ps1 first." }

$stage = Join-Path $root '.tmp\pack'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

function Copy-Into($sourcePath, $relative) {
    $dest = Join-Path $stage $relative
    $dir = Split-Path -Parent $dest
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    Copy-Item -LiteralPath $sourcePath -Destination $dest -Force
}

Copy-Into $dll 'app\ConvenientFileTP.dll'
foreach ($name in @('app.ico', 'icon-source.png')) {
    $p = Join-Path $root ('assets\' + $name)
    if (Test-Path -LiteralPath $p) { Copy-Into $p ('assets\' + $name) }
}
foreach ($name in @('launcher.ps1', 'README.md', '.gitignore')) {
    $p = Join-Path $root $name
    if (Test-Path -LiteralPath $p) { Copy-Into $p $name }
}
# Entry points and icon source have Chinese file names; enumerate them instead of
# embedding non-ASCII literals (PowerShell 5.1 reads BOM-less scripts as ANSI).
Get-ChildItem -LiteralPath $root -File |
    Where-Object { $_.Extension -in @('.cmd', '.png') } |
    ForEach-Object { Copy-Into $_.FullName $_.Name }
foreach ($sub in @('tools', 'docs', 'artifacts')) {
    $d = Join-Path $root $sub
    if (Test-Path -LiteralPath $d) {
        Get-ChildItem -LiteralPath $d -File -Recurse |
            Where-Object { $_.FullName -notmatch '\\artifacts\\.+\.zip$' } |
            ForEach-Object {
                Copy-Into $_.FullName $_.FullName.Substring($root.Length + 1)
            }
    }
}
$srcRoot = Join-Path $root 'src'
if (Test-Path -LiteralPath $srcRoot) {
    Get-ChildItem -LiteralPath $srcRoot -File -Recurse |
        Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
        ForEach-Object { Copy-Into $_.FullName $_.FullName.Substring($root.Length + 1) }
}

$zip = Join-Path $root 'artifacts\ConvenientFileTP-0.1.0-beta.zip'
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
# Build the archive entry by entry with FORWARD SLASH separators.
# .NET Framework's ZipFile.CreateFromDirectory stores Windows backslashes in entry
# names, which violates the ZIP spec: Explorer usually copes, but many third-party
# extractors (and other platforms) flatten "app\ConvenientFileTP.dll" into a single
# bogus file name, after which launcher.ps1 reports "App payload not found".
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in (Get-ChildItem -LiteralPath $stage -File -Recurse)) {
        $relative = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        $entry = $archive.CreateEntry($relative, [System.IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = $file.LastWriteTime
        $input = [System.IO.File]::OpenRead($file.FullName)
        try {
            $output = $entry.Open()
            try { $input.CopyTo($output) } finally { $output.Dispose() }
        } finally { $input.Dispose() }
    }
} finally { $archive.Dispose() }
Remove-Item -LiteralPath $stage -Recurse -Force

$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    $count = $archive.Entries.Count
    $badNames = @($archive.Entries | Where-Object { $_.FullName.Contains('\') })
} finally { $archive.Dispose() }
if ($badNames.Count -gt 0) { throw "Package contains backslash entry names: " + $badNames[0].FullName }
Write-Host ("package written: " + $zip)
Write-Host ("entries: " + $count + " size: " + (Get-Item -LiteralPath $zip).Length)
