using System.Net;
using System.Linq;
using SecureDeviceBridge.API;
using SecureDeviceBridge.Core.Configuration;
using SecureDeviceBridge.Core.Interfaces;
using SecureDeviceBridge.Infrastructure;
using SecureDeviceBridge.Worker;
using Serilog;
using Serilog.Events;

// ═══════════════════════════════════════════════════════════════════════════════
// Secure Device Bridge — Composition Root
//
// Universal hardware bridge between local web applications and TPM 2.0.
// Listens STRICTLY on http://127.0.0.1:{port} — never exposed to the network.
//
// Device Authentication Philosophy:
//   This system does NOT require or generate a separate DeviceId.
//   Device authentication and registration rely ENTIRELY on the TPM-resident
//   asymmetric public key. Cryptographic proof-of-possession of the hardware-bound
//   private key is the sole source of trust.
//
// Architecture: Logical Clean Architecture in a single deployable.
//   Core/           → Interfaces, DTOs, Configuration
//   Infrastructure/ → TPM implementation (Microsoft.TSS / Tpm2Lib)
//   API/            → Minimal API endpoints with CORS
//   Worker/         → Background health monitor
// ═══════════════════════════════════════════════════════════════════════════════

// ── CLI Command Handler (Uninstall/Eviction Support) ──────────────────────────
if (args.Contains("--remove-tpm-key"))
{
    var tempBuilder = WebApplication.CreateBuilder(args);
    var tpmOptions = new TpmOptions();
    tempBuilder.Configuration.GetSection("Tpm").Bind(tpmOptions);
    
    using var loggerFactory = LoggerFactory.Create(logBuilder =>
    {
        logBuilder.AddConsole();
    });
    var logger = loggerFactory.CreateLogger<TpmService>();
    var options = Microsoft.Extensions.Options.Options.Create(tpmOptions);

    using var tpmService = new TpmService(logger, options);
    Console.WriteLine("[*] Evicting signing key from TPM persistent handle...");
    bool success = await tpmService.RemoveKeyAsync(CancellationToken.None);
    if (success)
    {
        Console.WriteLine("[+] SUCCESS: TPM-resident key was successfully evicted.");
        Environment.Exit(0);
    }
    else
    {
        Console.WriteLine("[-] FAILED: Key could not be removed (it may not exist, or TPM is unreachable).");
        Environment.Exit(1);
    }
}

// ── Configure Logger (Serilog) ───────────────────────────────────────────────
string logDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), // %ProgramData% on Windows
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
    // Fallback to local logs directory if ProgramData is not writable
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

// ── Host Configuration ────────────────────────────────────────────────────────
// Windows Service hosting (no-op on Linux). Enables install via sc.exe or New-Service.
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "SecureDeviceBridge";
});

// Systemd integration for Linux daemon hosting (no-op on Windows).
builder.Host.UseSystemd();

// ── Kestrel: Enforce localhost-only binding (Security by Isolation) ────────────
// The IP address 127.0.0.1 is hardcoded for security — only the port is configurable.
// This ensures the service is NEVER accidentally exposed to the network, regardless
// of appsettings.json misconfiguration.
int servicePort = builder.Configuration.GetValue("Service:Port", 5050);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, servicePort);
});

// ── Configuration Binding ─────────────────────────────────────────────────────
builder.Services.Configure<TpmOptions>(builder.Configuration.GetSection("Tpm"));
builder.Services.Configure<CorsSettings>(builder.Configuration.GetSection("Cors"));

// ── CORS Policy: Dynamic Origins from appsettings.json ────────────────────────
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
            // If no origins are configured, deny all cross-origin requests.
            // Direct localhost requests (same-origin) are never blocked by CORS.
            policy.WithOrigins("https://no-origin-configured.invalid")
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        }
    });
});

// ── Dependency Injection ──────────────────────────────────────────────────────
// Singleton: TPM is a hardware device — one connection for the entire service lifetime.
// Thread safety is handled internally via SemaphoreSlim.
builder.Services.AddSingleton<ITpmService, TpmService>();

// Background health monitor for operational visibility.
builder.Services.AddHostedService<TpmHealthMonitor>();

// ── Build Application ─────────────────────────────────────────────────────────
var app = builder.Build();

// ── Middleware Pipeline ───────────────────────────────────────────────────────
app.UseCors("AllowConfiguredOrigins");

// ── Map Endpoints ─────────────────────────────────────────────────────────────
app.MapSecureDeviceBridgeEndpoints();

// ── Startup Banner ────────────────────────────────────────────────────────────
Log.Information("Source of Trust: Device Identity is based solely on the hardware TPM Public Key.");
Log.Information("═══════════════════════════════════════════════════════════");
Log.Information("  Secure Device Bridge v1.0.0");
Log.Information("  Listening on: http://127.0.0.1:{Port}", servicePort);
Log.Information("  Security Mode: TPM_Asymmetric_PoP (RSA-2048 / SHA-256)");
Log.Information("  CORS Origins: {Origins}",
    corsSettings.AllowedOrigins.Count > 0
        ? string.Join(", ", corsSettings.AllowedOrigins)
        : "(none configured)");
Log.Information("  Platform: {Platform}", Environment.OSVersion);
Log.Information("  Logs Directory: {LogDir}", logDirectory);
Log.Information("═══════════════════════════════════════════════════════════");

// ── Run ───────────────────────────────────────────────────────────────────────
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
