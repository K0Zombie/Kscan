<#
.SYNOPSIS
    One-shot build for Kscan: compile -> test -> portable package -> optional sync.

.DESCRIPTION
    Same as running by hand:
        dotnet build Kscan\Kscan.slnx -c Release
        dotnet test  Kscan\Kscan.slnx -c Release
        powershell -File Kscan\packaging\publish-windows.ps1
    Stops at the first failing step and prints which step failed.

    NOTE: this file is intentionally ASCII-only. Windows PowerShell 5.1 reads
    .ps1 files without a BOM as ANSI, so non-ASCII text here would break parsing.

.PARAMETER NoTest
    Skip the test run (fast path when you only need a package).

.PARAMETER NoPackage
    Only compile (and test unless -NoTest); do not build the portable package.

.PARAMETER Sync
    After packaging, copy the app files into this folder, e.g.
    C:\Tools\kscan-win-x64-portable . The target's data\ folder is kept.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File Kscan\packaging\build.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File Kscan\packaging\build.ps1 -Sync C:\Tools\kscan-win-x64-portable
#>
[CmdletBinding()]
param(
    [switch]$NoTest,
    [switch]$NoPackage,
    [string]$Sync
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $projectRoot 'Kscan.slnx'
$props = Join-Path $projectRoot 'Directory.Build.props'
$version = ([xml](Get-Content -LiteralPath $props -Raw)).Project.PropertyGroup.Version

function Write-Step([string]$text) {
    Write-Host ''
    Write-Host "=== $text ===" -ForegroundColor Cyan
}

function Assert-LastExit([string]$name) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $name (exit $LASTEXITCODE)" -ForegroundColor Red
        exit 1
    }
}

# A running app locks the exe (MSB3026), so bail out early with a clear message.
$running = Get-Process -Name 'kscan-gui', 'kscan' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host 'Kscan is running. Quit it first (tray icon -> Exit), otherwise the exe is locked.' -ForegroundColor Yellow
    $running | Select-Object ProcessName, Id | Format-Table -AutoSize | Out-String | Write-Host
    exit 1
}

Write-Step "Kscan $version"

$watch = [System.Diagnostics.Stopwatch]::StartNew()
& dotnet build $solution -c Release
Assert-LastExit 'dotnet build'
Write-Host ("OK: build ({0:N1}s)" -f $watch.Elapsed.TotalSeconds) -ForegroundColor Green

if (-not $NoTest) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    & dotnet test $solution -c Release --no-build
    Assert-LastExit 'dotnet test'
    Write-Host ("OK: test ({0:N1}s)" -f $watch.Elapsed.TotalSeconds) -ForegroundColor Green
}

if (-not $NoPackage) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'publish-windows.ps1')
    Assert-LastExit 'publish-windows.ps1'
    Write-Host ("OK: package ({0:N1}s)" -f $watch.Elapsed.TotalSeconds) -ForegroundColor Green

    $zip = Join-Path $projectRoot "artifacts/kscan-$version-win-x64-portable.zip"
    if (-not (Test-Path -LiteralPath $zip)) {
        Write-Host "FAILED: package not found: $zip" -ForegroundColor Red
        exit 1
    }

    if ($Sync) {
        Write-Step "Sync to $Sync"
        if (-not (Test-Path -LiteralPath $Sync)) {
            Write-Host "FAILED: target folder does not exist: $Sync" -ForegroundColor Red
            exit 1
        }

        $source = Join-Path $projectRoot "artifacts/kscan-$version-win-x64-portable"
        Get-ChildItem -LiteralPath $source -File | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $Sync $_.Name) -Force
        }
        if (Test-Path -LiteralPath (Join-Path $source 'docs')) {
            Copy-Item -LiteralPath (Join-Path $source 'docs') -Destination $Sync -Recurse -Force
        }
        Write-Host 'OK: synced (data\ left untouched)' -ForegroundColor Green
    }
}

Write-Step 'Done'
$hashFile = Join-Path $projectRoot "artifacts/kscan-$version-win-x64-portable.zip.sha256"
if (Test-Path -LiteralPath $hashFile) {
    Get-Content -LiteralPath $hashFile
}
