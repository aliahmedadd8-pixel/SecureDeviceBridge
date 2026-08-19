using System.Text.Json.Serialization;

namespace SecureDeviceBridge.Core.DTOs;

/// <summary>
/// Response DTO for the GET /health endpoint.
/// </summary>
public sealed class HealthResponse
{
    [JsonPropertyName("serviceName")]
    public string ServiceName { get; init; } = "SecureDeviceBridge";

    /// <summary>
    /// "Healthy" if hardware components were read successfully, "Degraded" otherwise.
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = "Healthy";

    /// <summary>
    /// The identity mode used by the service.
    /// </summary>
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "HardwareFingerprint";

    [JsonPropertyName("utcTimestamp")]
    public DateTime UtcTimestamp { get; init; }

    /// <summary>
    /// The number of hardware components successfully read from the host machine.
    /// </summary>
    [JsonPropertyName("componentsAvailable")]
    public int ComponentsAvailable { get; init; }

    /// <summary>
    /// Service version.
    /// </summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = "2.0.0";
}
