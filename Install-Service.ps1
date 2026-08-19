#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs, uninstalls, starts, stops, or checks status of the Secure Device Bridge Windows Service.

.DESCRIPTION
    Professional PowerShell installer for SecureDeviceBridge.
    Requires Administrator privileges.

    Supported actions:
      Install   - Publishes the project, creates the Windows Service with auto-start and recovery options.
      Uninstall - Stops and removes the Windows Service.
      Start     - Starts the service.
      Stop      - Stops the service.
      Status    - Displays the current service status.
      Publish   - Only publishes the project without installing.

.PARAMETER Action
    The action to perform. Default: Install.

.PARAMETER Configuration
    Build configuration. Default: Release.

.PARAMETER Runtime
    Target runtime identifier. Default: win-x64.

.PARAMETER RemoveTpmKey
    If true, evicts the TPM-bound key during uninstall without prompting.

.EXAMPLE
    .\Install-Service.ps1 -Action Install
    .\Install-Service.ps1 -Action Uninstall
    .\Install-Service.ps1 -Action Status
#>

[CmdletBinding()]
param(
    [ValidateSet('Install', 'Uninstall', 'Start', 'Stop', 'Status', 'Publish')]
    [string]$Action = 'Install',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Runtime = 'win-x64',

    [switch]$RemoveTpmKey
)

# --- Constants ------------------------------------------------------------------
$ServiceName    = 'SecureDeviceBridge'
$DisplayName    = 'Secure Device Bridge'
$Description    = 'Universal hardware bridge for TPM 2.0 cryptographic operations via local Minimal APIs.'
$ProjectDir     = $PSScriptRoot
$PublishDir     = Join-Path $ProjectDir "bin\$Configuration\net8.0\$Runtime\publish"
$ExePath        = Join-Path $PublishDir 'SecureDeviceBridge.exe'

# --- Helper Functions -----------------------------------------------------------

function Write-Banner {
    Write-Host ''
    Write-Host '===========================================================' -ForegroundColor Cyan
    Write-Host '  Secure Device Bridge - Service Installer v1.0.0'         -ForegroundColor Cyan
    Write-Host '===========================================================' -ForegroundColor Cyan
    Write-Host ''
}

function Publish-Project {
    Write-Host "[*] Publishing project ($Configuration | $Runtime)..." -ForegroundColor Yellow

    $publishArgs = @(
        'publish'
        $ProjectDir
        '--configuration', $Configuration
        '--runtime', $Runtime
        '--self-contained', 'false'
        '--output', $PublishDir
    )

    & dotnet @publishArgs

    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
        exit 1
    }

    if (-not (Test-Path $ExePath)) {
        Write-Error "Published executable not found at: $ExePath"
        exit 1
    }

    Write-Host "[+] Published successfully to: $PublishDir" -ForegroundColor Green
}

function Install-SecureDeviceBridge {
    # Check if already installed
    $existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existingService) {
        Write-Host "[!] Service '$ServiceName' is already installed (Status: $($existingService.Status))." -ForegroundColor Yellow
        Write-Host "[*] To reinstall, run: .\Install-Service.ps1 -Action Uninstall" -ForegroundColor Yellow
        return
    }

    # Publish first
    Publish-Project

    Write-Host "[*] Creating Windows Service..." -ForegroundColor Yellow

    # Create the service with delayed auto-start (defaults to LocalSystem)
    New-Service `
        -Name $ServiceName `
        -BinaryPathName "`"$ExePath`"" `
        -DisplayName $DisplayName `
        -Description $Description `
        -StartupType Automatic `
        -ErrorAction Stop | Out-Null

    Write-Host "[+] Service created: $ServiceName" -ForegroundColor Green

    # Configure recovery options: restart on 1st, 2nd, and subsequent failures
    # Reset failure count after 86400 seconds (24 hours)
    Write-Host "[*] Configuring recovery options (restart on failure: 5s / 15s / 60s)..." -ForegroundColor Yellow
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null

    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Failed to set recovery options (non-critical). sc.exe exit code: $LASTEXITCODE"
    } else {
        Write-Host "[+] Recovery options configured: restart after 5s / 15s / 60s" -ForegroundColor Green
    }

    # Configure delayed auto-start
    & sc.exe config $ServiceName start= delayed-auto | Out-Null

    # Start the service
    Write-Host "[*] Starting service..." -ForegroundColor Yellow
    Start-Service -Name $ServiceName -ErrorAction Stop

    $svc = Get-Service -Name $ServiceName
    Write-Host "[+] Service started. Status: $($svc.Status)" -ForegroundColor Green
    Write-Host "[+] Listening on: http://127.0.0.1:5050" -ForegroundColor Green
    Write-Host ''
    Write-Host "    Test: curl http://127.0.0.1:5050/health" -ForegroundColor DarkGray
}

function Uninstall-SecureDeviceBridge {
    $existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $existingService) {
        Write-Host "[!] Service '$ServiceName' is not installed." -ForegroundColor Yellow
        return
    }

    # Evict TPM key if requested or confirmed by user
    $evict = $RemoveTpmKey
    if (-not $evict -and [Environment]::UserInteractive) {
        $response = Read-Host "Do you want to permanently delete the TPM-resident private key (Device Identity) from this hardware? (y/N)"
        if ($response -match '^[yY](es)?$') {
            $evict = $true
        }
    }

    if ($evict) {
        Write-Host "[*] Evicting TPM key..." -ForegroundColor Yellow
        if (Test-Path $ExePath) {
            & $ExePath --remove-tpm-key
        } else {
            Write-Warning "Could not find $ExePath to execute key eviction. TPM key remains intact."
        }
    }

    # Stop the service if running
    if ($existingService.Status -eq 'Running') {
        Write-Host "[*] Stopping service..." -ForegroundColor Yellow
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
        Write-Host "[+] Service stopped." -ForegroundColor Green
    }

    # Remove the service
    Write-Host "[*] Removing service..." -ForegroundColor Yellow

    if (Get-Command 'Remove-Service' -ErrorAction SilentlyContinue) {
        # PowerShell 6+ / .NET 6+
        Remove-Service -Name $ServiceName -ErrorAction Stop
    } else {
        # Fallback for Windows PowerShell 5.1
        & sc.exe delete $ServiceName | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to delete service. sc.exe exit code: $LASTEXITCODE"
            exit 1
        }
    }

    Write-Host "[+] Service '$ServiceName' removed successfully." -ForegroundColor Green
}

function Get-SecureDeviceBridgeStatus {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) {
        Write-Host "[!] Service '$ServiceName' is not installed." -ForegroundColor Yellow
        return
    }

    Write-Host "[*] Service Status:" -ForegroundColor Cyan
    Write-Host "    Name:         $($svc.Name)"
    Write-Host "    Display Name: $($svc.DisplayName)"
    Write-Host "    Status:       $($svc.Status)"
    Write-Host "    Start Type:   $($svc.StartType)"
    Write-Host ''

    # Quick health check
    if ($svc.Status -eq 'Running') {
        Write-Host "[*] Running health check..." -ForegroundColor Yellow
        try {
            $response = Invoke-RestMethod -Uri 'http://127.0.0.1:5050/health' -TimeoutSec 5
            Write-Host "[+] Health: $($response.status)" -ForegroundColor Green
            Write-Host "    Security Mode: $($response.securityMode)"
            Write-Host "    TPM Available: $($response.tpmAvailable)"
            Write-Host "    Key Loaded:    $($response.keyLoaded)"
            Write-Host "    UTC Time:      $($response.utcTimestamp)"
        }
        catch {
            Write-Warning "Health check failed: $_"
        }
    }
}

# --- Main -----------------------------------------------------------------------

Write-Banner

switch ($Action) {
    'Install'   { Install-SecureDeviceBridge }
    'Uninstall' { Uninstall-SecureDeviceBridge }
    'Start'     {
        Write-Host "[*] Starting service..." -ForegroundColor Yellow
        Start-Service -Name $ServiceName -ErrorAction Stop
        Write-Host "[+] Service started." -ForegroundColor Green
    }
    'Stop'      {
        Write-Host "[*] Stopping service..." -ForegroundColor Yellow
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
        Write-Host "[+] Service stopped." -ForegroundColor Green
    }
    'Status'    { Get-SecureDeviceBridgeStatus }
    'Publish'   { Publish-Project }
}

Write-Host ''
