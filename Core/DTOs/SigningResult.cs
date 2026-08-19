using System.Text.Json.Serialization;

namespace SecureDeviceBridge.Core.DTOs;

/// <summary>
/// Response DTO for the POST /api/device/key/sign endpoint.
/// Contains the RSASSA-PKCS1-v1_5 digital signature.
/// </summary>
public sealed class SigningResult
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    /// <summary>
    /// The digital signature bytes, Base64-encoded.
    /// Produced by RSASSA-PKCS1-v1_5 with SHA-256.
    /// </summary>
    [JsonPropertyName("signatureBase64")]
    public string? SignatureBase64 { get; init; }

    /// <summary>
    /// The signing algorithm identifier (RS256).
    /// </summary>
    [JsonPropertyName("algorithm")]
    public string? Algorithm { get; init; }

    /// <summary>
    /// Error message if the operation failed. Null on success.
    /// </summary>
    [JsonPropertyName("errorMessage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Factory method for a successful result.
    /// </summary>
    public static SigningResult Ok(string signatureBase64) => new()
    {
        Success = true,
        SignatureBase64 = signatureBase64,
        Algorithm = "RS256"
    };

    /// <summary>
    /// Factory method for a failed result.
    /// </summary>
    public static SigningResult Failure(string errorMessage) => new()
    {
        Success = false,
        ErrorMessage = errorMessage
    };
}
