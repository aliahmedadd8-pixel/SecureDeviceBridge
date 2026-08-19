using System.Text.Json.Serialization;

namespace SecureDeviceBridge.Core.DTOs;

/// <summary>
/// Response DTO for the GET /api/device/identity endpoint.
/// Contains the composite Device ID, the hardware components used to generate it,
/// and the timestamp of generation.
/// </summary>
public sealed class DeviceIdentityResult
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    /// <summary>
    /// The deterministic Device ID: lowercase hex-encoded SHA-256 hash
    /// of all hardware component values combined.
    /// Example: "a3f8c2d1e7b49f0a..."
    /// </summary>
    [JsonPropertyName("deviceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceId { get; init; }

    /// <summary>
    /// The list of hardware components that were read and used to compute the Device ID.
    /// </summary>
    [JsonPropertyName("components")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<HardwareComponent>? Components { get; init; }

    /// <summary>
    /// UTC timestamp of when the fingerprint was generated.
    /// </summary>
    [JsonPropertyName("generatedAt")]
    public DateTime GeneratedAt { get; init; }

    /// <summary>
    /// Error message if the operation failed. Null on success.
    /// </summary>
    [JsonPropertyName("errorMessage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Factory method for a successful result.
    /// </summary>
    public static DeviceIdentityResult Ok(string deviceId, List<HardwareComponent> components) => new()
    {
        Success = true,
        DeviceId = deviceId,
        Components = components,
        GeneratedAt = DateTime.UtcNow
    };

    /// <summary>
    /// Factory method for a failed result.
    /// </summary>
    public static DeviceIdentityResult Failure(string errorMessage) => new()
    {
        Success = false,
        ErrorMessage = errorMessage,
        GeneratedAt = DateTime.UtcNow
    };
}
