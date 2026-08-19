using System.Text.Json.Serialization;

namespace SecureDeviceBridge.Core.DTOs;

/// <summary>
/// Response DTO for the GET /health endpoint.
/// Provides service identity, status, security mode, and TPM availability.
/// </summary>
public sealed class HealthResponse
{
    [JsonPropertyName("serviceName")]
    public string ServiceName { get; init; } = "SecureDeviceBridge";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "Healthy";

    [JsonPropertyName("securityMode")]
    public string SecurityMode { get; init; } = "TPM_Asymmetric_PoP";

    [JsonPropertyName("utcTimestamp")]
    public DateTime UtcTimestamp { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("tpmAvailable")]
    public bool TpmAvailable { get; init; }

    [JsonPropertyName("keyLoaded")]
    public bool KeyLoaded { get; init; }

    [JsonPropertyName("version")]
    public string Version { get; init; } = "1.0.0";
}
