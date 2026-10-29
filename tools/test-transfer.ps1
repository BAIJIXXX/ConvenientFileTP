# End-to-end TCP transfer test for ConvenientFileTP.
#
# Starts two app instances (receiver + sender) over the loopback interface,
# sends a ~12 MB random binary plus a small text file, then verifies the
# received files against the source (count / size / SHA256) and scans both
# profile logs for errors and progress evidence.
#
# NOTE: keep this file ASCII-only. PowerShell 5.1 reads BOM-less scripts as ANSI,
# so non-ASCII text in .ps1 files gets corrupted and can break parsing.
# (The optional non-ASCII test file name is built from [char] codes below.)
#
# Usage:
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\test-transfer.ps1
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\test-transfer.ps1 -Keep
#
# Exit code: 0 = PASS, 1 = FAIL.

param(
    [switch]$Keep,
    [int]$ReceiveAutoExit = 90,
    [int]$SendAutoExit = 45
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$launcher = Join-Path $root 'launcher.ps1'
$appDll = Join-Path $root 'app\ConvenientFileTP.dll'
$scratch = Join-Path $root '.scratch'
$srcDir = Join-Path $scratch 'net-src'
$recvRoot = Join-Path $scratch 'net-recv'
$recvDownloads = Join-Path $recvRoot 'downloads'
$recvProfile = 'net-recv'
$sendProfile = 'net-send'
$recvLog = "$env:APPDATA\ConvenientFileTP\profiles\$recvProfile\logs\app.log"
$sendLog = "$env:APPDATA\ConvenientFileTP\profiles\$sendProfile\logs\app.log"
$resultReceiver = $null
$resultSender = $null

function Write-Section([string]$text) {
    Write-Host ''
    Write-Host ('==== ' + $text + ' ====')
}

function Write-Info([string]$text) {
    Write-Host ('[test] ' + $text)
}

function Read-LogText([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return '' }
    try {
        return [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
    } catch {
        return ''
    }
}

function Wait-ForPattern([string]$path, [string]$pattern, [int]$timeoutSec) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $text = Read-LogText $path
        if ($text -match $pattern) { return $true }
        Start-Sleep -Milliseconds 300
    }
    return $false
}

function Wait-ForFileCount([string]$dir, [int]$expected, [int]$timeoutSec) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $files = @()
        if (Test-Path -LiteralPath $dir) { $files = @(Get-ChildItem -LiteralPath $dir -File) }
        if ($files.Count -ge $expected) { return $true }
        Start-Sleep -Milliseconds 300
    }
    return $false
}

# Stop only instances started with our test profiles; never touch other sessions.
function Stop-TestProcesses {
    try {
        $procs = Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue
    } catch {
        return
    }
    foreach ($p in $procs) {
        $cmd = $p.CommandLine
        if ($null -eq $cmd) { continue }
        if ($cmd -like '*--profile net-recv*' -or $cmd -like '*--profile net-send*') {
            Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
        }
    }
}

function Get-Sha256([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}

# Windows PowerShell swallows "--debug" when the launcher is started with
#   powershell.exe ... -File launcher.ps1 ... --debug
# (it is consumed as the host's -Debug switch), so the app never receives it and
# Log.Debug lines are missing. Calling the launcher through -Command keeps the flag.
function Start-TestApp([string]$profile, [string]$name, [string[]]$extra) {
    $cmd = '& ''' + $launcher + ''' --profile ''' + $profile + ''' --name ''' + $name + ''''
    foreach ($value in $extra) { $cmd += ' ''' + $value + '''' }
    $cmdLine = '-NoProfile -ExecutionPolicy Bypass -STA -Command "' + $cmd + '"'
    return Start-Process -FilePath 'powershell.exe' -ArgumentList $cmdLine `
        -WorkingDirectory $root -WindowStyle Hidden -PassThru
}

function New-TestFiles {
    if (Test-Path -LiteralPath $srcDir) {
        Remove-Item -LiteralPath $srcDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $srcDir -Force | Out-Null

    $made = @()

    # ~12 MB pseudo-random binary, written in 1 MB chunks.
    $binPath = Join-Path $srcDir 'net-large.bin'
    $chunk = New-Object byte[] (1024 * 1024)
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $fs = [System.IO.File]::Create($binPath)
    try {
        for ($i = 0; $i -lt 12; $i++) {
            $rng.GetBytes($chunk)
            $fs.Write($chunk, 0, $chunk.Length)
        }
        $fs.Flush($true)
    } finally {
        $fs.Dispose()
        $rng.Dispose()
    }
    $made += $binPath

    # Small UTF-8 text file.
    $txtPath = Join-Path $srcDir 'net-small.txt'
    $sb = New-Object System.Text.StringBuilder
    for ($i = 1; $i -le 200; $i++) {
        [void]$sb.AppendLine('ConvenientFileTP transfer test line ' + $i)
    }
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($txtPath, $sb.ToString(), $utf8)
    $made += $txtPath

    # Optional file with a non-ASCII name: 0x6D4B 0x8BD5 0x6587 0x4EF6 = "test file".
    try {
        $cn = -join @([char]0x6D4B, [char]0x8BD5, [char]0x6587, [char]0x4EF6)
        $cnPath = Join-Path $srcDir ($cn + '.txt')
        [System.IO.File]::WriteAllText($cnPath, 'unicode file name payload', $utf8)
        $made += $cnPath
    } catch {
        Write-Info 'SKIP unicode file name (could not create it)'
    }

    return $made
}

function Show-Evidence([string]$label, [string]$text, [string]$pattern, [int]$maxLines) {
    $matches = @($text -split "`r?`n" | Where-Object { $_ -match $pattern })
    Write-Info ($label + ': ' + $matches.Count + ' matching log line(s)')
    $take = $maxLines
    if ($matches.Count -lt $take) { $take = $matches.Count }
    for ($i = 0; $i -lt $take; $i++) {
        Write-Host ('    ' + $matches[$i].Trim())
    }
}

$pass = $true
$issues = @()

function Add-Issue([string]$text) {
    $script:pass = $false
    $script:issues += $text
    Write-Host ('[FAIL] ' + $text)
}

try {
    Write-Section 'Prepare'
    if (-not (Test-Path -LiteralPath $launcher)) { throw "launcher not found: $launcher" }
    if (-not (Test-Path -LiteralPath $appDll)) { throw "app payload not built: $appDll (run tools\build.ps1)" }

    Write-Info 'Stopping stale test instances (only --profile net-recv / net-send)'
    Stop-TestProcesses
    Start-Sleep -Milliseconds 500

    foreach ($profile in @($recvProfile, $sendProfile)) {
        $profileDir = "$env:APPDATA\ConvenientFileTP\profiles\$profile"
        if (Test-Path -LiteralPath $profileDir) {
            Remove-Item -LiteralPath $profileDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    if (Test-Path -LiteralPath $recvRoot) {
        Remove-Item -LiteralPath $recvRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Path $recvDownloads -Force | Out-Null

    $sourceFiles = New-TestFiles
    Write-Info ('Created ' + $sourceFiles.Count + ' source file(s) in ' + $srcDir)
    foreach ($f in $sourceFiles) {
        $fi = Get-Item -LiteralPath $f
        Write-Info ('  ' + $fi.Name + '  ' + $fi.Length + ' bytes  sha256=' + (Get-Sha256 $f))
    }
    $sendArg = ($sourceFiles -join ';')

    Write-Section 'Start receiver'
    $recvArgs = @(
        '--download-dir', $recvDownloads,
        '--auto-exit', [string]$ReceiveAutoExit,
        '--minimized', '--no-tray', '--debug'
    )
    $resultReceiver = Start-TestApp $recvProfile 'NetRecv' $recvArgs
    Write-Info ('Receiver PID ' + $resultReceiver.Id + ', waiting for listening port...')
    if (-not (Wait-ForPattern $recvLog 'Transfer server listening on' 40)) {
        Add-Issue 'receiver did not log a listening port within 40s'
    }
    $recvText = Read-LogText $recvLog
    $listen = @($recvText -split "`r?`n" | Where-Object { $_ -match 'Transfer server listening on' })
    if ($listen.Count -gt 0) { Write-Info $listen[0].Trim() }

    Write-Section 'Start sender'
    $sendArgs = @(
        '--auto-select', 'NetRecv',
        '--auto-send', $sendArg,
        '--auto-exit', [string]$SendAutoExit,
        '--minimized', '--no-tray', '--debug'
    )
    $resultSender = Start-TestApp $sendProfile 'NetSend' $sendArgs
    Write-Info ('Sender PID ' + $resultSender.Id)

    Write-Section 'Wait for transfer'
    $senderDone = Wait-ForPattern $sendLog 'SENDER DONE session=' 180
    $receiverDone = Wait-ForPattern $recvLog 'SESSION DONE session=' 180
    if (-not $senderDone) { Add-Issue 'sender did not reach SENDER DONE within 180s' }
    if (-not $receiverDone) { Add-Issue 'receiver did not reach SESSION DONE within 180s' }

    if (-not (Wait-ForFileCount $recvDownloads $sourceFiles.Count 30)) {
        Add-Issue ('receiver download dir has fewer than ' + $sourceFiles.Count + ' file(s)')
    }
    Start-Sleep -Milliseconds 800

    $recvText = Read-LogText $recvLog
    $sendText = Read-LogText $sendLog

    foreach ($data in @(@('receiver', $recvLog, $recvText), @('sender', $sendLog, $sendText))) {
        if ([string]::IsNullOrWhiteSpace($data[2])) {
            Add-Issue ($data[0] + ' log is empty or missing: ' + $data[1])
        }
    }

    Write-Section 'Verify received files'
    $downloaded = @()
    if (Test-Path -LiteralPath $recvDownloads) {
        $downloaded = @(Get-ChildItem -LiteralPath $recvDownloads -File)
    }
    Write-Info ('downloads: ' + $recvDownloads)
    Write-Info ('source files: ' + $sourceFiles.Count + ', received files: ' + $downloaded.Count)
    if ($downloaded.Count -ne $sourceFiles.Count) {
        Add-Issue ('received file count ' + $downloaded.Count + ' != source count ' + $sourceFiles.Count)
    }

    foreach ($src in $sourceFiles) {
        $srcItem = Get-Item -LiteralPath $src
        $srcHash = Get-Sha256 $src
        $target = Join-Path $recvDownloads $srcItem.Name
        $match = $null
        if (Test-Path -LiteralPath $target) {
            $match = Get-Item -LiteralPath $target
        } else {
            foreach ($d in $downloaded) {
                if ((Get-Sha256 $d.FullName) -eq $srcHash) { $match = $d; break }
            }
            if ($null -ne $match) {
                Add-Issue ('name mismatch: source "' + $srcItem.Name + '" received as "' + $match.Name + '"')
            }
        }
        if ($null -eq $match) {
            Add-Issue ('missing received file for "' + $srcItem.Name + '"')
            continue
        }
        if ($match.Length -ne $srcItem.Length) {
            Add-Issue ('size mismatch for "' + $srcItem.Name + '": ' + $match.Length + ' != ' + $srcItem.Length)
            continue
        }
        $dstHash = Get-Sha256 $match.FullName
        if ($dstHash -ne $srcHash) {
            Add-Issue ('sha256 mismatch for "' + $srcItem.Name + '"')
        } else {
            Write-Info ('OK  ' + $srcItem.Name + '  ' + $srcItem.Length + ' bytes  sha256 matches')
        }
    }

    Write-Section 'Scan logs'
    foreach ($data in @(@('receiver', $recvText), @('sender', $sendText))) {
        if ($data[1] -match '\[ERROR\]') { Add-Issue ($data[0] + ' log contains [ERROR]') }
        if ($data[1] -match 'Unhandled') { Add-Issue ($data[0] + ' log contains "Unhandled"') }
        if ($data[1] -match 'SESSION FAILED|SESSION CANCELED') {
            Add-Issue ($data[0] + ' log contains a failed or canceled session')
        }
    }
    if ($recvText -notmatch 'SESSION ACCEPTED session=') { Add-Issue 'receiver log has no SESSION ACCEPTED' }
    if ($recvText -match 'SESSION REJECTED') { Add-Issue 'receiver log has SESSION REJECTED' }
    if ($sendText -notmatch 'SESSION ACCEPTED session=') { Add-Issue 'sender log has no SESSION ACCEPTED' }

    $savedCount = @($recvText -split "`r?`n" | Where-Object { $_ -match 'FILE SAVED session=' }).Count
    if ($savedCount -ne $sourceFiles.Count) {
        Add-Issue ('FILE SAVED lines ' + $savedCount + ' != source count ' + $sourceFiles.Count)
    }
    $sentCount = @($sendText -split "`r?`n" | Where-Object { $_ -match 'FILE SENT session=' }).Count
    if ($sentCount -ne $sourceFiles.Count) {
        Add-Issue ('FILE SENT lines ' + $sentCount + ' != source count ' + $sourceFiles.Count)
    }

    Show-Evidence 'receiver FILE SAVED' $recvText 'FILE SAVED session=' 6
    Show-Evidence 'sender FILE SENT' $sendText 'FILE SENT session=' 6
    Show-Evidence 'sender PROGRESS' $sendText 'PROGRESS session=.*dir=Send' 8
    Show-Evidence 'receiver PROGRESS' $recvText 'PROGRESS session=.*dir=Receive' 8
    Show-Evidence 'sender COMPLETED' $sendText 'COMPLETED session=.*dir=Send' 3
    Show-Evidence 'receiver COMPLETED' $recvText 'COMPLETED session=.*dir=Receive' 3

    if ($sendText -notmatch 'PROGRESS session=.*dir=Send') { Add-Issue 'sender log has no PROGRESS lines' }
    if ($recvText -notmatch 'PROGRESS session=.*dir=Receive') { Add-Issue 'receiver log has no PROGRESS lines' }
    if ($sendText -notmatch 'PROGRESS session=[0-9a-f]{32} dir=Send') { Add-Issue 'sender PROGRESS lines carry no session id' }
    if ($recvText -notmatch 'PROGRESS session=[0-9a-f]{32} dir=Receive') { Add-Issue 'receiver PROGRESS lines carry no session id' }
    if ($sendText -notmatch 'PROGRESS session=[0-9a-f]{32} dir=Send .*speed=[1-9][0-9]*B/s') { Add-Issue 'sender PROGRESS never reports a non-zero speed' }
    if ($recvText -notmatch 'PROGRESS session=[0-9a-f]{32} dir=Receive .*speed=[1-9][0-9]*B/s') { Add-Issue 'receiver PROGRESS never reports a non-zero speed' }
    if ($sendText -notmatch 'COMPLETED session=.*dir=Send success=True') { Add-Issue 'sender COMPLETED success=True not found' }
    if ($recvText -notmatch 'COMPLETED session=.*dir=Receive success=True') { Add-Issue 'receiver COMPLETED success=True not found' }
}
catch {
    Add-Issue ('exception: ' + $_.Exception.Message)
}
finally {
    Write-Section 'Cleanup'
    if ($Keep) {
        Write-Info 'Keep switch set: leaving test processes running'
        if ($null -ne $resultReceiver) { Write-Info ('receiver PID ' + $resultReceiver.Id) }
        if ($null -ne $resultSender) { Write-Info ('sender PID ' + $resultSender.Id) }
    } else {
        Stop-TestProcesses
        Write-Info 'Test instances stopped'
    }
}

Write-Section 'Result'
if ($pass) {
    Write-Host 'TRANSFER TEST: PASS'
    Write-Host ('receiver log: ' + $recvLog)
    Write-Host ('sender log:   ' + $sendLog)
    Write-Host ('downloads:    ' + $recvDownloads)
    exit 0
}

Write-Host 'TRANSFER TEST: FAIL'
foreach ($issue in $issues) { Write-Host ('  - ' + $issue) }
Write-Host ('receiver log: ' + $recvLog)
Write-Host ('sender log:   ' + $sendLog)
Write-Host '--- receiver log tail ---'
$recvTail = Read-LogText $recvLog
$recvLines = @($recvTail -split "`r?`n")
$startR = $recvLines.Count - 25
if ($startR -lt 0) { $startR = 0 }
for ($i = $startR; $i -lt $recvLines.Count; $i++) { Write-Host $recvLines[$i] }
Write-Host '--- sender log tail ---'
$sendTail = Read-LogText $sendLog
$sendLines = @($sendTail -split "`r?`n")
$startS = $sendLines.Count - 25
if ($startS -lt 0) { $startS = 0 }
for ($i = $startS; $i -lt $sendLines.Count; $i++) { Write-Host $sendLines[$i] }
exit 1
