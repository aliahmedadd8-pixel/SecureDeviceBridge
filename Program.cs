using System.Net;
using SecureDeviceBridge.API;
using SecureDeviceBridge.Core.Configuration;
using SecureDeviceBridge.Core.Interfaces;
using SecureDeviceBridge.Infrastructure;
using SecureDeviceBridge.Worker;

// ═══════════════════════════════════════════════════════════════════════════════
// Secure Device Bridge — Composition Root
//
// Universal hardware bridge between local web applications and TPM 2.0.
// Listens STRICTLY on http://127.0.0.1:{port} — never exposed to the network.
//
// Architecture: Logical Clean Architecture in a single deployable.
//   Core/           → Interfaces, DTOs, Configuration
//   Infrastructure/ → TPM implementation (Microsoft.TSS / Tpm2Lib)
//   API/            → Minimal API endpoints with CORS
//   Worker/         → Background health monitor
// ═══════════════════════════════════════════════════════════════════════════════

var builder = WebApplication.CreateBuilder(args);

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
var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("═══════════════════════════════════════════════════════════");
logger.LogInformation("  Secure Device Bridge v1.0.0");
logger.LogInformation("  Listening on: http://127.0.0.1:{Port}", servicePort);
logger.LogInformation("  Security Mode: TPM_Asymmetric_PoP (RSA-2048 / SHA-256)");
logger.LogInformation("  CORS Origins: {Origins}",
    corsSettings.AllowedOrigins.Count > 0
        ? string.Join(", ", corsSettings.AllowedOrigins)
        : "(none configured)");
logger.LogInformation("  Platform: {Platform}", Environment.OSVersion);
logger.LogInformation("═══════════════════════════════════════════════════════════");

// ── Run ───────────────────────────────────────────────────────────────────────
app.Run();
