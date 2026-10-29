param(
    [string]$Configuration = 'Release'
)

# NOTE: keep this file ASCII-only. PowerShell 5.1 reads BOM-less scripts as ANSI.
$ErrorActionPreference = 'Stop'

# Serialize concurrent builds from parallel workstreams; they share obj/ and app/.
$buildMutex = New-Object System.Threading.Mutex($false, 'Local\ConvenientFileTP_Build')
$lockTaken = $false
$exitCode = 0
try {
    try {
        $lockTaken = $buildMutex.WaitOne([TimeSpan]::FromMinutes(15))
    } catch [System.Threading.AbandonedMutexException] {
        $lockTaken = $true
    }
    if (-not $lockTaken) { throw 'Timed out waiting for another build to finish.' }

    $root = Split-Path -Parent $PSScriptRoot
    $projectDir = Join-Path $root 'src\ConvenientFileTP'
    $projectFile = Join-Path $projectDir 'ConvenientFileTP.csproj'
    $outputDir = Join-Path $root 'app'

    # Keep every writable dotnet location inside the repository: the build machine is offline
    # and the global package cache / temp dirs may not be writable.
    $env:DOTNET_CLI_HOME = Join-Path $root '.dotnet'
    $env:NUGET_PACKAGES = Join-Path $env:DOTNET_CLI_HOME 'packages'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $tmp = Join-Path $root '.tmp'
    New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES, $tmp, $outputDir | Out-Null
    $env:TEMP = $tmp
    $env:TMP = $tmp

    $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnet)) {
        $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
        if ($null -eq $cmd) { throw 'dotnet.exe not found. Install the .NET SDK.' }
        $dotnet = $cmd.Source
    }

    Write-Host "dotnet: $dotnet"
    Write-Host "build:  $projectFile -> $outputDir ($Configuration)"

    & $dotnet build $projectFile -c $Configuration -o $outputDir --nologo
    if ($LASTEXITCODE -ne 0) {
        $exitCode = $LASTEXITCODE
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }

    # Keep only what the in-memory launcher needs.
    Get-ChildItem -LiteralPath $outputDir -Filter '*.json' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    Get-ChildItem -LiteralPath $outputDir -Filter '*.pdb' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

    Copy-Item -LiteralPath (Join-Path $root 'launcher.ps1') -Destination $outputDir -Force -ErrorAction SilentlyContinue

    Write-Host "BUILD OK: $(Join-Path $outputDir 'ConvenientFileTP.dll')" -ForegroundColor Green
}
catch {
    Write-Host "BUILD FAILED: $($_.Exception.Message)" -ForegroundColor Red
    if ($exitCode -eq 0) { $exitCode = 1 }
}
finally {
    if ($lockTaken) {
        try { $buildMutex.ReleaseMutex() } catch { }
    }
    $buildMutex.Dispose()
}

exit $exitCode
