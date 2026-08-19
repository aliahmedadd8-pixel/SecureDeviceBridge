namespace SecureDeviceBridge.Core.Configuration;

/// <summary>
/// Strongly-typed configuration for CORS settings.
/// Bound to the "Cors" section of appsettings.json.
/// </summary>
public sealed class CorsSettings
{
    /// <summary>
    /// List of allowed CORS origins. Any web application URL can be added here.
    /// Origins are loaded dynamically from appsettings.json at startup.
    /// </summary>
    public List<string> AllowedOrigins { get; set; } = new();
}
