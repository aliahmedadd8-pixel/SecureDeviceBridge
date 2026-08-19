namespace SecureDeviceBridge.Core.DTOs;

/// <summary>
/// Internal status snapshot of the TPM service.
/// Used by the health monitor and health endpoint.
/// </summary>
public sealed class TpmServiceStatus
{
    /// <summary>
    /// Whether the TPM device is connected and responsive.
    /// </summary>
    public bool TpmAvailable { get; init; }

    /// <summary>
    /// Whether a signing key is loaded at the persistent handle.
    /// </summary>
    public bool KeyLoaded { get; init; }

    /// <summary>
    /// Last error message, if any. Null when healthy.
    /// </summary>
    public string? LastError { get; init; }
}
