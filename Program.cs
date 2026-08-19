using System.Net;
using SecureDeviceBridge.API;
using SecureDeviceBridge.Core.Configuration;
using SecureDeviceBridge.Core.Interfaces;
using SecureDeviceBridge.Infrastructure;
using Serilog;
using Serilog.Events;

// =============================================================================
// Secure Device Bridge v2.0 - Composition Root
//
// Universal hardware bridge that generates a unique, deterministic Device ID
// by reading physical hardware component serial numbers (CPU, Motherboard,
// BIOS, SMBIOS UUID, Machine GUID).
//
// Listens STRICTLY on http://127.0.0.1:{port} - never exposed to the network.
//
// Architecture: Logical Clean Architecture in a single deployable.
//   Core/           - Interfaces, DTOs, Configuration
//   Infrastructure/ - Hardware fingerprint implementation (WMI / CIM)
//   API/            - Minimal API endpoints with CORS
// =============================================================================

// -- Configure Logger (Serilog) -----------------------------------------------
string logDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "SecureDeviceBridge",
    "logs");

try
{
    if (!Directory.Exists(logDirectory))
    {
        Directory.CreateDirectory(logDirectory);
    }
}
catch
{
    logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
}

string logPath = Path.Combine(logDirectory, "secure-device-bridge-.log");

var logConfig = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        logPath,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}");

if (OperatingSystem.IsWindows())
{
    logConfig.WriteTo.EventLog(
        source: "SecureDeviceBridge",
        manageEventSource: true);
}

Log.Logger = logConfig.CreateLogger();

var builder = WebApplication.CreateBuilder(args);

// Replace default logger with Serilog
builder.Host.UseSerilog();

// -- Host Configuration -------------------------------------------------------
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "SecureDeviceBridge";
});

builder.Host.UseSystemd();

// -- Kestrel: Enforce localhost-only binding -----------------------------------
int servicePort = builder.Configuration.GetValue("Service:Port", 5050);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, servicePort);
});

// -- Configuration Binding ----------------------------------------------------
builder.Services.Configure<CorsSettings>(builder.Configuration.GetSection("Cors"));

// -- CORS Policy: Dynamic Origins from appsettings.json -----------------------
var corsSettings = builder.Configuration
    .GetSection("Cors")
    .Get<CorsSettings>() ?? new CorsSettings();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowConfiguredOrigins", policy =>
    {
        if (corsSettings.AllowedOrigins.Count > 0)
        {
            policy.WithOrigins(corsSettings.AllowedOrigins.ToArray())
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials();
        }
        else
        {
            policy.WithOrigins("https://no-origin-configured.invalid")
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        }
    });
});

// -- Dependency Injection -----------------------------------------------------
// Singleton: hardware identifiers are constant for the lifetime of the machine.
builder.Services.AddSingleton<IHardwareFingerprintService, HardwareFingerprintService>();

// -- Build Application --------------------------------------------------------
var app = builder.Build();

// -- Middleware Pipeline ------------------------------------------------------
app.UseCors("AllowConfiguredOrigins");

// -- Map Endpoints ------------------------------------------------------------
app.MapSecureDeviceBridgeEndpoints();

// -- Eagerly collect the fingerprint on startup for health endpoint ------------
var fingerprintService = app.Services.GetRequiredService<IHardwareFingerprintService>();
_ = await fingerprintService.CollectFingerprintAsync(CancellationToken.None);

// -- Startup Banner -----------------------------------------------------------
Log.Information("===========================================================");
Log.Information("  Secure Device Bridge v2.0.0 (Hardware Fingerprint Mode)");
Log.Information("  Listening on: http://127.0.0.1:{Port}", servicePort);
Log.Information("  Mode: HardwareFingerprint (CPU + MB + BIOS + UUID + GUID)");
Log.Information("  Hardware Components Available: {Count}", fingerprintService.GetAvailableComponentCount());
Log.Information("  CORS Origins: {Origins}",
    corsSettings.AllowedOrigins.Count > 0
        ? string.Join(", ", corsSettings.AllowedOrigins)
        : "(none configured)");
Log.Information("  Platform: {Platform}", Environment.OSVersion);
Log.Information("  Logs Directory: {LogDir}", logDirectory);
Log.Information("===========================================================");

// -- Run ----------------------------------------------------------------------
try
{
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
