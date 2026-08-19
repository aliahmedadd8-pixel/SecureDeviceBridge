#Requires -Version 5.1
<#
.SYNOPSIS
    Builds and packages Secure Device Bridge into a Windows installer (Setup.exe).

.DESCRIPTION
    This script:
    1. Publishes the .NET 8 project as a self-contained win-x64 deployment
    2. Compiles the Inno Setup script into a professional installer EXE
    3. Outputs: Installer\Output\SecureDeviceBridge_Setup_1.0.0.exe

    The resulting installer can be distributed to end users who can install
    the service with a few clicks - no .NET SDK or technical knowledge required.

.PARAMETER Configuration
    Build configuration. Default: Release.

.PARAMETER SelfContained
    If true (default), bundles the .NET runtime so users don't need it installed.
    If false, requires .NET 8 runtime on the target machine.

.PARAMETER SkipBuild
    If true, skips the dotnet publish step and uses existing publish output.

.EXAMPLE
    .\Build-Installer.ps1
    .\Build-Installer.ps1 -SelfContained $false
    .\Build-Installer.ps1 -SkipBuild

.NOTES
    Prerequisites:
    - .NET 8 SDK (for building)
    - Inno Setup 6.x (for packaging) - https://jrsoftware.org/isdl.php
#>

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [bool]$SelfContained = $true,

    [switch]$SkipBuild
)

# --- Constants ------------------------------------------------------------------
$ErrorActionPreference = 'Stop'
$ProjectDir  = $PSScriptRoot
$ProjectFile = Join-Path $ProjectDir 'SecureDeviceBridge.csproj'
$InstallerDir = Join-Path $ProjectDir 'Installer'
$PublishDir  = Join-Path $InstallerDir 'publish'
$IssFile     = Join-Path $InstallerDir 'SecureDeviceBridge.iss'
$OutputDir   = Join-Path $InstallerDir 'Output'
$Runtime     = 'win-x64'

# --- Banner ---------------------------------------------------------------------
Write-Host ''
Write-Host '===========================================================' -ForegroundColor Cyan
Write-Host '  Secure Device Bridge - Installer Builder v1.0.0'         -ForegroundColor Cyan
Write-Host '===========================================================' -ForegroundColor Cyan
Write-Host ''

# --- Step 1: Locate Inno Setup Compiler -----------------------------------------
Write-Host '[1/3] Locating Inno Setup compiler...' -ForegroundColor Yellow

$IsccPaths = @(
    # Default Inno Setup 6 install locations
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    # Inno Setup 5 (fallback)
    "${env:ProgramFiles(x86)}\Inno Setup 5\ISCC.exe"
    "$env:ProgramFiles\Inno Setup 5\ISCC.exe"
    # Chocolatey / Scoop / Winget installs
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)

$IsccExe = $null
foreach ($path in $IsccPaths) {
    if (Test-Path $path) {
        $IsccExe = $path
        break
    }
}

# Also check PATH
if (-not $IsccExe) {
    $IsccExe = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
}

if (-not $IsccExe) {
    Write-Host ''
    Write-Host '[!] Inno Setup compiler (ISCC.exe) not found!' -ForegroundColor Red
    Write-Host ''
    Write-Host '    Inno Setup is required to build the installer.' -ForegroundColor White
    Write-Host '    Download it free from: https://jrsoftware.org/isdl.php' -ForegroundColor White
    Write-Host ''
    Write-Host '    Install options:' -ForegroundColor Gray
    Write-Host '      1. Download from website (recommended):' -ForegroundColor Gray
    Write-Host '         https://jrsoftware.org/isdl.php' -ForegroundColor DarkCyan
    Write-Host ''
    Write-Host '      2. Via Chocolatey:' -ForegroundColor Gray
    Write-Host '         choco install innosetup' -ForegroundColor DarkCyan
    Write-Host ''
    Write-Host '      3. Via Winget:' -ForegroundColor Gray
    Write-Host '         winget install JRSoftware.InnoSetup' -ForegroundColor DarkCyan
    Write-Host ''
    Write-Host '    After installing, re-run this script.' -ForegroundColor White
    Write-Host ''
    exit 1
}

Write-Host "      Found: $IsccExe" -ForegroundColor Green

# --- Step 2: Publish .NET Project ----------------------------------------------
if ($SkipBuild) {
    Write-Host '[2/3] Skipping build (using existing publish output)...' -ForegroundColor Yellow

    if (-not (Test-Path (Join-Path $PublishDir 'SecureDeviceBridge.exe'))) {
        Write-Error "No published output found at: $PublishDir. Run without -SkipBuild first."
        exit 1
    }
} else {
    Write-Host "[2/3] Publishing project ($Configuration | $Runtime | SelfContained=$SelfContained)..." -ForegroundColor Yellow

    # Clean previous publish output
    if (Test-Path $PublishDir) {
        Remove-Item $PublishDir -Recurse -Force
    }

    $publishArgs = @(
        'publish', $ProjectFile
        '--configuration', $Configuration
        '--runtime', $Runtime
        '--self-contained', $SelfContained.ToString().ToLower()
        '--output', $PublishDir
        '-p:PublishReadyToRun=true'
        '-p:DebugType=none'
        '-p:DebugSymbols=false'
    )

    Write-Host "      dotnet $($publishArgs -join ' ')" -ForegroundColor DarkGray
    & dotnet @publishArgs

    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
        exit 1
    }

    # Verify the executable was produced
    $exePath = Join-Path $PublishDir 'SecureDeviceBridge.exe'
    if (-not (Test-Path $exePath)) {
        Write-Error "Published executable not found at: $exePath"
        exit 1
    }

    $exeSize = (Get-Item $exePath).Length / 1MB
    $totalFiles = (Get-ChildItem $PublishDir -Recurse -File).Count
    Write-Host "      Published: $totalFiles files, EXE size: $([math]::Round($exeSize, 1)) MB" -ForegroundColor Green
}

# --- Step 3: Compile Inno Setup Installer --------------------------------------
Write-Host '[3/3] Compiling installer...' -ForegroundColor Yellow

# Ensure output directory exists
if (-not (Test-Path $OutputDir)) {
    New-Item -Path $OutputDir -ItemType Directory -Force | Out-Null
}

# Run Inno Setup Compiler
Write-Host "      ISCC.exe $IssFile" -ForegroundColor DarkGray
& $IsccExe $IssFile

if ($LASTEXITCODE -ne 0) {
    Write-Error "Inno Setup compilation failed with exit code $LASTEXITCODE"
    exit 1
}

# --- Done -----------------------------------------------------------------------
$installerFiles = Get-ChildItem $OutputDir -Filter '*.exe' | Sort-Object LastWriteTime -Descending
if ($installerFiles.Count -gt 0) {
    $installer = $installerFiles[0]
    $installerSize = $installer.Length / 1MB

    Write-Host ''
    Write-Host '===========================================================' -ForegroundColor Green
    Write-Host '  Installer built successfully!'                            -ForegroundColor Green
    Write-Host '===========================================================' -ForegroundColor Green
    Write-Host ''
    Write-Host "  File: $($installer.FullName)" -ForegroundColor White
    Write-Host "  Size: $([math]::Round($installerSize, 1)) MB" -ForegroundColor White
    Write-Host ''
    Write-Host '  Distribution:' -ForegroundColor Gray
    Write-Host '    Send this single EXE to end users.' -ForegroundColor Gray
    Write-Host '    They right-click -> Run as Administrator -> Next -> Install.' -ForegroundColor Gray
    Write-Host ''

    # Open the output folder in Explorer
    Start-Process explorer.exe -ArgumentList "/select,`"$($installer.FullName)`""
} else {
    Write-Warning "Installer built but output file not found in: $OutputDir"
}

Write-Host ''
