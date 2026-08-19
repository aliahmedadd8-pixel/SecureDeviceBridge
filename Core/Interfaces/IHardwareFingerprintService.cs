using SecureDeviceBridge.Core.DTOs;

namespace SecureDeviceBridge.Core.Interfaces;

/// <summary>
/// Defines the contract for reading hardware component identifiers
/// and generating a deterministic, composite Device ID.
/// </summary>
public interface IHardwareFingerprintService
{
    /// <summary>
    /// Collects hardware serial numbers from the host machine and
    /// computes a deterministic SHA-256 Device ID from the combined values.
    /// Results are cached after the first call (hardware doesn't change at runtime).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Device identity containing the Device ID, component list, and timestamp.</returns>
    Task<DeviceIdentityResult> CollectFingerprintAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns the number of hardware components that were successfully read.
    /// Returns 0 if the fingerprint has not been collected yet.
    /// </summary>
    int GetAvailableComponentCount();
}
