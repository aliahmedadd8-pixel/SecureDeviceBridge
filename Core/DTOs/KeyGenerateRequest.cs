using System.Text.Json.Serialization;

namespace SecureDeviceBridge.Core.DTOs;

/// <summary>
/// Request DTO for the POST /api/device/key/generate endpoint.
/// Controls key generation behavior (idempotent vs force regeneration).
/// </summary>
public sealed class KeyGenerateRequest
{
    /// <summary>
    /// If true, destroys the existing key at the persistent handle and generates a new one.
    /// If false (default), returns the existing key if one is already present.
    /// </summary>
    [JsonPropertyName("force")]
    public bool Force { get; set; } = false;
}
