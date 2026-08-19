using System.Text.Json.Serialization;

namespace SecureDeviceBridge.Core.DTOs;

/// <summary>
/// Response DTO for the POST /api/device/key/generate endpoint.
/// Contains the TPM-generated public key in multiple formats.
/// </summary>
public sealed class KeyGenerationResult
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    /// <summary>
    /// The public key in PEM (SPKI) format.
    /// Example: "-----BEGIN PUBLIC KEY-----\nMIIBI..."
    /// </summary>
    [JsonPropertyName("publicKeyPem")]
    public string? PublicKeyPem { get; init; }

    /// <summary>
    /// The raw SubjectPublicKeyInfo (SPKI) DER bytes, Base64-encoded.
    /// </summary>
    [JsonPropertyName("publicKeyBase64")]
    public string? PublicKeyBase64 { get; init; }

    /// <summary>
    /// Unique key identifier: SHA-256 thumbprint of the SPKI DER, hex-encoded.
    /// </summary>
    [JsonPropertyName("keyId")]
    public string? KeyId { get; init; }

    /// <summary>
    /// Whether this is a newly generated key or an existing one that was returned.
    /// </summary>
    [JsonPropertyName("wasExisting")]
    public bool WasExisting { get; init; }

    /// <summary>
    /// Error message if the operation failed. Null on success.
    /// </summary>
    [JsonPropertyName("errorMessage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Factory method for a successful result.
    /// </summary>
    public static KeyGenerationResult Ok(string publicKeyPem, string publicKeyBase64, string keyId, bool wasExisting) => new()
    {
        Success = true,
        PublicKeyPem = publicKeyPem,
        PublicKeyBase64 = publicKeyBase64,
        KeyId = keyId,
        WasExisting = wasExisting
    };

    /// <summary>
    /// Factory method for a failed result.
    /// </summary>
    public static KeyGenerationResult Failure(string errorMessage) => new()
    {
        Success = false,
        ErrorMessage = errorMessage
    };
}
