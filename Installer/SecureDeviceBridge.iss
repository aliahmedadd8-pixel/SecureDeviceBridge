; ═══════════════════════════════════════════════════════════════════════════════
; Secure Device Bridge — Inno Setup Installer Script
;
; Produces a professional Windows installer (SecureDeviceBridge_Setup.exe)
; that installs the service, configures auto-start with recovery, and starts it.
;
; Requirements: Inno Setup 6.x (https://jrsoftware.org/isinfo.php)
; Build with:   .\Build-Installer.ps1
; ═══════════════════════════════════════════════════════════════════════════════

#define MyAppName      "Secure Device Bridge"
#define MyAppVersion   "1.0.0"
#define MyAppPublisher "SecureDeviceBridge"
#define MyAppURL       "http://127.0.0.1:5050/health"
#define MyServiceName  "SecureDeviceBridge"
#define MyServiceExe   "SecureDeviceBridge.exe"

[Setup]
AppId={{8F4E9A2B-3C7D-4E5F-A1B2-6D8E9F0A1B2C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppPublisher}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=SecureDeviceBridge_Setup_{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
; Require admin for service installation
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
; Modern wizard style
WizardStyle=modern
WizardSizePercent=120,120
; Installer metadata
VersionInfoVersion={#MyAppVersion}
VersionInfoDescription={#MyAppName} Installer
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
; Minimum Windows version (Windows 10+)
MinVersion=10.0
; Architecture
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Allow user to see what will happen
AlwaysShowComponentsList=no
; Uninstall
UninstallDisplayName={#MyAppName}
; Prevent running multiple installers
SetupMutex=SecureDeviceBridgeSetupMutex

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=This will install [name/ver] on your computer.%n%n{#MyAppName} is a local background service that generates a unique, deterministic Device ID by reading physical hardware component serial numbers (CPU, Motherboard, BIOS, SMBIOS UUID, Machine GUID).%n%nThe service listens on http://127.0.0.1:5050 (localhost only).%n%nIt is recommended that you close all other applications before continuing.

[Files]
; Application files from dotnet publish output
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; Config file: don't overwrite on upgrade so user's CORS settings are preserved
Source: "publish\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
; Start Menu: health check shortcut (opens in default browser)
Name: "{group}\{#MyAppName} — Health Check"; Filename: "{#MyAppURL}"
Name: "{group}\{#MyAppName} — Configuration"; Filename: "{app}\appsettings.json"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Run]
; Post-install: optionally open health check in browser
Filename: "{#MyAppURL}"; Description: "Open health check in browser"; \
  Flags: postinstall nowait shellexec skipifsilent unchecked

[Code]
// ═══════════════════════════════════════════════════════════════════════════════
// Pascal Script — Service Lifecycle Management
// ═══════════════════════════════════════════════════════════════════════════════

const
  SERVICE_NAME = 'SecureDeviceBridge';

// Runs sc.exe with given arguments. Returns True if exit code = 0.
function RunSC(Params: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec('sc.exe', Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
            and (ResultCode = 0);
end;

// Runs net.exe (for net stop which waits for the service to actually stop).
function RunNet(Params: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec('net.exe', Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
            and (ResultCode = 0);
end;

// Check if the service already exists by querying it.
function ServiceExists(): Boolean;
var
  ResultCode: Integer;
begin
  Exec('sc.exe', 'query ' + SERVICE_NAME, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := (ResultCode = 0);
end;

// ── Pre-Install: Stop existing service if upgrading ──────────────────────────
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if ServiceExists() then
  begin
    Log('Existing service detected — stopping for upgrade...');
    RunNet('stop ' + SERVICE_NAME);
    // Brief pause to let the service fully release file handles
    Sleep(2000);
    // Delete old service (will be re-created after file copy)
    RunSC('delete ' + SERVICE_NAME);
    Sleep(1000);
  end;
end;

// ── Post-Install: Create, configure, and start the service ───────────────────
procedure CurStepChanged(CurStep: TSetupStep);
var
  ExePath: String;
begin
  if CurStep = ssPostInstall then
  begin
    ExePath := ExpandConstant('{app}\{#MyServiceExe}');
    Log('Installing Windows Service...');

    // 1. Create the service with delayed auto-start, running under the LocalSystem account explicitly
    if not RunSC(Format('create %s binPath= "\"%s\"" start= delayed-auto DisplayName= "%s" obj= LocalSystem',
                        [SERVICE_NAME, ExePath, '{#MyAppName}'])) then
    begin
      Log('WARNING: Failed to create service via sc.exe');
      MsgBox('Warning: Could not create the Windows Service automatically.' + #13#10 +
             'You may need to run Install-Service.ps1 manually as Administrator.',
             mbInformation, MB_OK);
      Exit;
    end;

    // 2. Set service description
    RunSC(Format('description %s "%s"',
                 [SERVICE_NAME,
                  'Generates a unique, deterministic Device ID from hardware component serial numbers via local Minimal APIs.']));

    // 3. Configure recovery: restart after 5s / 15s / 60s, reset counter after 24h
    RunSC(Format('failure %s reset= 86400 actions= restart/5000/restart/15000/restart/60000',
                 [SERVICE_NAME]));

    // 4. Start the service
    Log('Starting service...');
    if RunSC('start ' + SERVICE_NAME) then
      Log('Service started successfully.')
    else
      Log('WARNING: Service created but failed to start. Check Event Viewer for details.');
  end;
end;

// ── Uninstall: Stop and remove the service before deleting files ─────────────
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    Log('Stopping service for uninstall...');

    // Stop the service (net stop waits for full stop)
    RunNet('stop ' + SERVICE_NAME);
    Sleep(2000);

    // Delete the service
    Log('Removing service...');
    if RunSC('delete ' + SERVICE_NAME) then
      Log('Service removed successfully.')
    else
      Log('WARNING: Failed to remove service. It may need manual removal.');

    Sleep(1000);
  end;
end;

// ── Init: Setup initialization ──────────────────────────────────────────────
function InitializeSetup(): Boolean;
begin
  Result := True;
end;
