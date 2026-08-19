using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SecureDeviceBridge.Core.DTOs;

/// <summary>
/// Request DTO for the POST /api/device/key/sign endpoint.
/// Contains the challenge nonce to be signed by the TPM.
/// </summary>
public sealed class SignChallengeRequest
{
    /// <summary>
    /// The challenge nonce string to sign. Must not be null or empty.
    /// This value is UTF-8 encoded, hashed with SHA-256, then signed with RSASSA-PKCS1-v1_5.
    /// </summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "ChallengeNonce is required and must not be empty.")]
    [MinLength(1, ErrorMessage = "ChallengeNonce must be at least 1 character long.")]
    [JsonPropertyName("challengeNonce")]
    public string ChallengeNonce { get; set; } = string.Empty;
}
