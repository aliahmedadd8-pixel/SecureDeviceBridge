using SecureDeviceBridge.Core.DTOs;

namespace SecureDeviceBridge.Core.Interfaces;

/// <summary>
/// Defines the contract for TPM 2.0 cryptographic operations.
/// Implementations must be thread-safe (TPM is a serial device).
/// </summary>
public interface ITpmService : IDisposable
{
    /// <summary>
    /// Generates an RSA-2048 signing keypair inside the TPM hardware.
    /// Idempotent by default: returns the existing key if one is present.
    /// Pass <paramref name="force"/> = true to destroy and recreate the key.
    /// </summary>
    /// <param name="force">If true, destroys the existing key and generates a new one.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the public key in PEM and Base64 formats.</returns>
    Task<KeyGenerationResult> GenerateKeyAsync(bool force, CancellationToken cancellationToken);

    /// <summary>
    /// Signs a challenge nonce using the TPM-resident private key.
    /// The nonce is hashed with SHA-256 and signed using RSASSA-PKCS1-v1_5.
    /// </summary>
    /// <param name="challengeNonce">The challenge string to sign.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the digital signature in Base64 format.</returns>
    Task<SigningResult> SignChallengeAsync(string challengeNonce, CancellationToken cancellationToken);

    /// <summary>
    /// Checks whether a signing key is present at the configured persistent handle.
    /// </summary>
    Task<bool> IsKeyPresentAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns the current TPM service status for health monitoring.
    /// This method is non-blocking and does not acquire the TPM lock.
    /// </summary>
    TpmServiceStatus GetStatus();

    /// <summary>
    /// Permanently removes the signing key from the TPM persistent handle.
    /// Used during uninstall when the user opts to delete the device identity.
    /// WARNING: This operation is irreversible — the private key is destroyed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the key was successfully removed, false otherwise.</returns>
    Task<bool> RemoveKeyAsync(CancellationToken cancellationToken);
}
