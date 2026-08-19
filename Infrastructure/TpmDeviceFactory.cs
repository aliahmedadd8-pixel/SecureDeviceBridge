using System.Runtime.InteropServices;
using Tpm2Lib;

namespace SecureDeviceBridge.Infrastructure;

/// <summary>
/// Factory for creating the appropriate TPM device based on the runtime OS.
/// Windows uses TBS (TPM Base Services), Linux uses /dev/tpmrm0 (kernel resource manager).
/// </summary>
public static class TpmDeviceFactory
{
    /// <summary>
    /// Creates and returns a platform-appropriate TPM 2.0 device instance.
    /// </summary>
    /// <returns>An unconnected <see cref="Tpm2Device"/> instance. Caller must call Connect().</returns>
    /// <exception cref="PlatformNotSupportedException">
    /// Thrown when running on an unsupported operating system (not Windows or Linux).
    /// </exception>
    public static Tpm2Device CreateDevice()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // TbsDevice communicates with the TPM via Windows TPM Base Services (TBS) API.
            // TBS handles resource management and concurrent access automatically.
            return new TbsDevice();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // LinuxTpmDevice communicates with the TPM via the kernel resource manager device.
            // /dev/tpmrm0 is preferred over /dev/tpm0 as it supports concurrent access
            // and prevents "device busy" errors when other processes (systemd, tpm2-tools) use the TPM.
            return new LinuxTpmDevice();
        }

        throw new PlatformNotSupportedException(
            $"TPM device access is not supported on {RuntimeInformation.OSDescription}. " +
            "Supported platforms: Windows (TBS), Linux (/dev/tpmrm0).");
    }
}
