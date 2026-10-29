param(
    [string]$ProfilePrefix = 'e2e',
    [int]$ReceiverAutoExit = 90,
    [int]$SenderAutoExit = 45,
    [switch]$Keep,
    [switch]$NoScreenshots
)

# NOTE: keep this file ASCII-only. PowerShell 5.1 reads BOM-less scripts as ANSI.
# End-to-end check: discovery -> auto-select -> floating widget -> TCP transfer -> received files.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$launcher = Join-Path $root 'launcher.ps1'
$dll = Join-Path $root 'app\ConvenientFileTP.dll'
$psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

if (-not (Test-Path -LiteralPath $dll)) {
    throw "app\ConvenientFileTP.dll not found. Run tools\build.ps1 first."
}

$scratch = Join-Path $root '.scratch\e2e'
$srcDir = Join-Path $scratch 'src'
$recvDir = Join-Path $scratch 'recv'
$downloads = Join-Path $recvDir 'downloads'
$artifacts = Join-Path $root 'artifacts'
$recvProfile = "$ProfilePrefix-recv"
$sendProfile = "$ProfilePrefix-send"

function Get-ProfileLog([string]$profile) {
    $base = Join-Path $env:APPDATA 'ConvenientFileTP'
    $candidates = @(
        (Join-Path $base "profiles\$profile\logs\app.log"),
        (Join-Path $base "$profile\logs\app.log"),
        (Join-Path $base 'logs\app.log')
    )
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c) { return $c }
    }
    return $candidates[0]
}

function Wait-ForLog([string]$path, [string]$text, [int]$timeoutSec) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $path) {
            $content = Get-Content -LiteralPath $path -Raw -ErrorAction SilentlyContinue
            if ($content -and $content.Contains($text)) { return $true }
        }
        Start-Sleep -Milliseconds 300
    }
    return $false
}

function Start-App([string]$profile, [string]$name, [string[]]$extra) {
    $argList = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-STA',
        '-File', $launcher,
        '--profile', $profile,
        '--name', $name,
        '--debug'
    ) + $extra
    return Start-Process -FilePath $psExe -ArgumentList $argList -WindowStyle Hidden -PassThru
}

function Take-Shot([string]$name) {
    if ($NoScreenshots) { return }
    $path = Join-Path $artifacts $name
    & $psExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\screenshot.ps1') -Path $path -Delay 0 | Out-Host
}

function Stop-Own([System.Diagnostics.Process]$proc) {
    if ($null -eq $proc) { return }
    try {
        if (-not $proc.HasExited) {
            Write-Host "stopping process $($proc.Id) ..."
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
    } catch { }
}

function New-TestFiles {
    Remove-Item -LiteralPath $srcDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $srcDir | Out-Null

    $small = Join-Path $srcDir 'e2e-small.txt'
    $lines = [string[]](1..400 | ForEach-Object { "line $_ - ConvenientFileTP e2e payload 0123456789" })
    [System.IO.File]::WriteAllLines($small, $lines, (New-Object System.Text.UTF8Encoding($false)))

    $large = Join-Path $srcDir 'e2e-large.bin'
    $size = 40MB
    $buffer = New-Object byte[] (1MB)
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $stream = [System.IO.File]::Open($large, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
    try {
        $written = 0
        while ($written -lt $size) {
            $rng.GetBytes($buffer)
            $toWrite = [Math]::Min($buffer.Length, $size - $written)
            $stream.Write($buffer, 0, $toWrite)
            $written += $toWrite
        }
    }
    finally {
        $stream.Dispose()
        $rng.Dispose()
    }

    $medium = Join-Path $srcDir 'e2e-medium.bin'
    $mbytes = New-Object byte[] (1536 * 1024)
    $rng2 = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $rng2.GetBytes($mbytes)
    $rng2.Dispose()
    [System.IO.File]::WriteAllBytes($medium, $mbytes)

    return @($small, $medium, $large)
}

Write-Host '=== ConvenientFileTP end-to-end test ==='
Write-Host "project: $root"
Write-Host "profiles: $recvProfile / $sendProfile"

Remove-Item -LiteralPath $recvDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $downloads | Out-Null
if (-not (Test-Path -LiteralPath $artifacts)) { New-Item -ItemType Directory -Force -Path $artifacts | Out-Null }

$files = New-TestFiles
Write-Host ('test files: ' + (($files | ForEach-Object { [System.IO.Path]::GetFileName($_) }) -join ', '))

$recvProc = $null
$sendProc = $null
$pass = $false
try {
    $recvLog = Get-ProfileLog $recvProfile
    $sendLog = Get-ProfileLog $sendProfile
    Remove-Item -LiteralPath (Split-Path -Parent $recvLog) -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Split-Path -Parent $sendLog) -Recurse -Force -ErrorAction SilentlyContinue

    Write-Host 'starting receiver ...'
    $recvProc = Start-App $recvProfile 'E2E-Receiver' @('--download-dir', $downloads, '--auto-exit', "$ReceiverAutoExit")
    $recvLog = Get-ProfileLog $recvProfile
    # Wait for the receiver window to come up; the log file is created during startup.
    $initDeadline = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $initDeadline -and -not (Test-Path -LiteralPath $recvLog)) {
        Start-Sleep -Milliseconds 300
    }
    Start-Sleep -Seconds 4
    Take-Shot 'e2e-01-receiver.png'

    Write-Host 'starting sender ...'
    $sendList = ($files -join ';')
    $sendProc = Start-App $sendProfile 'E2E-Sender' @('--auto-select', 'E2E-Receiver', '--auto-send', $sendList, '--auto-exit', "$SenderAutoExit")

    # Wait until all files show up in the download directory (or 60s timeout).
    $expected = @{}
    foreach ($f in $files) {
        $key = [System.IO.Path]::GetFileName($f)
        $expected[$key] = (Get-Item -LiteralPath $f).Length
    }
    $deadline = (Get-Date).AddSeconds(60)
    $lastCount = -1
    while ((Get-Date) -lt $deadline) {
        $got = @(Get-ChildItem -LiteralPath $downloads -File -ErrorAction SilentlyContinue)
        $complete = $true
        foreach ($name in $expected.Keys) {
            $match = $got | Where-Object { $_.Name -eq $name -and $_.Length -eq $expected[$name] } | Select-Object -First 1
            if ($null -eq $match) { $complete = $false; break }
        }
        if ($complete) { break }
        if ($got.Count -ne $lastCount) {
            $lastCount = $got.Count
            Write-Host ("waiting... files in download dir: " + $got.Count)
        }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Seconds 1
    Take-Shot 'e2e-02-received.png'

    if ($null -ne $sendProc) {
        try { $sendProc.WaitForExit(20000) | Out-Null } catch { }
    }
    Start-Sleep -Seconds 1
    Take-Shot 'e2e-03-after.png'

    Write-Host '=== verifying files ==='
    $allOk = $true
    foreach ($f in $files) {
        $name = [System.IO.Path]::GetFileName($f)
        $srcHash = (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash
        $dest = Join-Path $downloads $name
        if (-not (Test-Path -LiteralPath $dest)) {
            Write-Host "MISSING: $name"
            $allOk = $false
            continue
        }
        $destHash = (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash
        $destLen = (Get-Item -LiteralPath $dest).Length
        $srcLen = (Get-Item -LiteralPath $f).Length
        if ($srcHash -ne $destHash -or $destLen -ne $srcLen) {
            Write-Host "MISMATCH: $name src=$srcLen dst=$destLen"
            $allOk = $false
        }
        else {
            Write-Host "OK: $name ($destLen bytes) sha256=$($srcHash.Substring(0,16))..."
        }
    }

    # Any other files in the download directory are unexpected for a clean run.
    $extra = @(Get-ChildItem -LiteralPath $downloads -File | Where-Object { -not $expected.ContainsKey($_.Name) })
    if ($extra.Count -gt 0) {
        Write-Host ("unexpected files: " + (($extra | ForEach-Object { $_.Name }) -join ', '))
        $allOk = $false
    }

    Write-Host '=== log check ==='
    $errorHits = 0
    foreach ($pair in @(@('receiver', (Get-ProfileLog $recvProfile)), @('sender', (Get-ProfileLog $sendProfile)))) {
        $label = $pair[0]
        $log = $pair[1]
        if (Test-Path -LiteralPath $log) {
            Copy-Item -LiteralPath $log -Destination (Join-Path $artifacts "e2e-$label.log") -Force
            $hits = @(Select-String -LiteralPath $log -Pattern '\[ERROR\]|ERROR:|Unhandled' -ErrorAction SilentlyContinue)
            if ($hits.Count -gt 0) {
                $errorHits += $hits.Count
                Write-Host "$label log errors: $($hits.Count) (see artifacts\e2e-$label.log)"
            }
            else {
                Write-Host "$label log: no error lines"
            }
        }
        else {
            Write-Host "${label}: log not found at $log"
            $allOk = $false
        }
    }

    if ($errorHits -gt 0) { $allOk = $false }
    $pass = $allOk
}
catch {
    Write-Host "E2E FAILED with exception: $($_.Exception.Message)"
    $pass = $false
}
finally {
    if (-not $Keep) {
        Stop-Own $sendProc
        Stop-Own $recvProc
    }
    else {
        Write-Host 'processes kept alive (use -Keep then stop them manually)'
    }
}

if ($pass) {
    Write-Host '=== E2E RESULT: PASS ===' -ForegroundColor Green
    exit 0
}
Write-Host '=== E2E RESULT: FAIL ===' -ForegroundColor Red
exit 1
