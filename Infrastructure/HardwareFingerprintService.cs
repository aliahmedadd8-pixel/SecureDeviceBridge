using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SecureDeviceBridge.Core.DTOs;
using SecureDeviceBridge.Core.Interfaces;

namespace SecureDeviceBridge.Infrastructure;

/// <summary>
/// Reads hardware component identifiers from the host machine and computes
/// a deterministic, composite Device ID (SHA-256 fingerprint).
///
/// Components read (5 sources):
///   1. CPU ProcessorId          (Win32_Processor)
///   2. Motherboard SerialNumber (Win32_BaseBoard)
///   3. BIOS SerialNumber        (Win32_BIOS)
///   4. SMBIOS UUID              (Win32_ComputerSystemProduct)
///   5. Windows Machine GUID     (Registry)
///
/// The result is cached after the first successful read because
/// hardware identifiers do not change at runtime.
/// </summary>
public sealed class HardwareFingerprintService : IHardwareFingerprintService
{
    private readonly ILogger<HardwareFingerprintService> _logger;
    private readonly object _cacheLock = new();
    private DeviceIdentityResult? _cachedResult;

    public HardwareFingerprintService(ILogger<HardwareFingerprintService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<DeviceIdentityResult> CollectFingerprintAsync(CancellationToken cancellationToken)
    {
        // Return cached result if available (hardware doesn't change at runtime)
        if (_cachedResult is not null)
            return Task.FromResult(_cachedResult);

        lock (_cacheLock)
        {
            // Double-check after acquiring lock
            if (_cachedResult is not null)
                return Task.FromResult(_cachedResult);

            try
            {
                _logger.LogInformation("Collecting hardware fingerprint...");

                var components = new List<HardwareComponent>();

                if (OperatingSystem.IsWindows())
                {
                    CollectWindowsComponents(components);
                }
                else if (OperatingSystem.IsLinux())
                {
                    CollectLinuxComponents(components);
                }
                else
                {
                    _logger.LogWarning("Unsupported operating system for hardware fingerprinting");
                    _cachedResult = DeviceIdentityResult.Failure(
                        "Hardware fingerprinting is not supported on this operating system.");
                    return Task.FromResult(_cachedResult);
                }

                if (components.Count == 0)
                {
                    _cachedResult = DeviceIdentityResult.Failure(
                        "No hardware components could be read from this machine.");
                    return Task.FromResult(_cachedResult);
                }

                // Compute deterministic Device ID from sorted components
                string deviceId = ComputeDeviceId(components);

                _cachedResult = DeviceIdentityResult.Ok(deviceId, components);

                _logger.LogInformation(
                    "Hardware fingerprint collected successfully. DeviceId: {DeviceId}, Components: {Count}",
                    deviceId, components.Count);

                return Task.FromResult(_cachedResult);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to collect hardware fingerprint");
                var errorResult = DeviceIdentityResult.Failure($"Hardware fingerprint collection failed: {ex.Message}");
                return Task.FromResult(errorResult);
            }
        }
    }

    /// <inheritdoc />
    public int GetAvailableComponentCount()
    {
        return _cachedResult?.Components?.Count ?? 0;
    }

    // =========================================================================
    // Windows: Read hardware via WMI (System.Management)
    // =========================================================================

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void CollectWindowsComponents(List<HardwareComponent> components)
    {
        // 1. CPU ProcessorId
        ReadWmiProperty(components,
            "CPU_ID",
            "SELECT ProcessorId FROM Win32_Processor",
            "ProcessorId",
            "Win32_Processor.ProcessorId");

        // 2. Motherboard SerialNumber
        ReadWmiProperty(components,
            "MOTHERBOARD_SERIAL",
            "SELECT SerialNumber FROM Win32_BaseBoard",
            "SerialNumber",
            "Win32_BaseBoard.SerialNumber");

        // 3. BIOS SerialNumber
        ReadWmiProperty(components,
            "BIOS_SERIAL",
            "SELECT SerialNumber FROM Win32_BIOS",
            "SerialNumber",
            "Win32_BIOS.SerialNumber");

        // 4. SMBIOS UUID (most reliable system-level unique ID)
        ReadWmiProperty(components,
            "SMBIOS_UUID",
            "SELECT UUID FROM Win32_ComputerSystemProduct",
            "UUID",
            "Win32_ComputerSystemProduct.UUID");

        // 5. Windows Machine GUID (from registry)
        ReadWindowsMachineGuid(components);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void ReadWmiProperty(
        List<HardwareComponent> components,
        string componentName,
        string wmiQuery,
        string propertyName,
        string sourceName)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(wmiQuery);
            using var results = searcher.Get();

            foreach (var obj in results)
            {
                string? value = obj[propertyName]?.ToString()?.Trim();

                if (!string.IsNullOrWhiteSpace(value) &&
                    !value.Equals("To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase) &&
                    !value.Equals("Default string", StringComparison.OrdinalIgnoreCase) &&
                    !value.Equals("None", StringComparison.OrdinalIgnoreCase))
                {
                    components.Add(new HardwareComponent
                    {
                        Name = componentName,
                        Value = value,
                        Source = sourceName
                    });

                    _logger.LogDebug("Read {Component}: {Value}", componentName, value);
                    return; // Take the first valid result only
                }
            }

            _logger.LogWarning("WMI query returned no valid value for {Component}", componentName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read {Component} via WMI", componentName);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void ReadWindowsMachineGuid(List<HardwareComponent> components)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography");

            string? machineGuid = key?.GetValue("MachineGuid")?.ToString();

            if (!string.IsNullOrWhiteSpace(machineGuid))
            {
                components.Add(new HardwareComponent
                {
                    Name = "MACHINE_GUID",
                    Value = machineGuid,
                    Source = "Registry.MachineGuid"
                });

                _logger.LogDebug("Read MACHINE_GUID: {Value}", machineGuid);
            }
            else
            {
                _logger.LogWarning("Windows MachineGuid not found in registry");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read Windows MachineGuid from registry");
        }
    }

    // =========================================================================
    // Linux: Read hardware from /sys/class/dmi/id/ and /etc/machine-id
    // =========================================================================

    private void CollectLinuxComponents(List<HardwareComponent> components)
    {
        // 1. SMBIOS UUID
        ReadLinuxFile(components, "SMBIOS_UUID",
            "/sys/class/dmi/id/product_uuid",
            "dmi.product_uuid");

        // 2. Motherboard Serial
        ReadLinuxFile(components, "MOTHERBOARD_SERIAL",
            "/sys/class/dmi/id/board_serial",
            "dmi.board_serial");

        // 3. BIOS Serial (vendor)
        ReadLinuxFile(components, "BIOS_SERIAL",
            "/sys/class/dmi/id/bios_vendor",
            "dmi.bios_vendor");

        // 4. Product Serial
        ReadLinuxFile(components, "PRODUCT_SERIAL",
            "/sys/class/dmi/id/product_serial",
            "dmi.product_serial");

        // 5. Machine ID
        ReadLinuxFile(components, "MACHINE_GUID",
            "/etc/machine-id",
            "etc.machine-id");
    }

    private void ReadLinuxFile(
        List<HardwareComponent> components,
        string componentName,
        string filePath,
        string sourceName)
    {
        try
        {
            if (File.Exists(filePath))
            {
                string? value = File.ReadAllText(filePath).Trim();

                if (!string.IsNullOrWhiteSpace(value) &&
                    !value.Equals("None", StringComparison.OrdinalIgnoreCase))
                {
                    components.Add(new HardwareComponent
                    {
                        Name = componentName,
                        Value = value,
                        Source = sourceName
                    });

                    _logger.LogDebug("Read {Component}: {Value}", componentName, value);
                }
            }
            else
            {
                _logger.LogWarning("File not found: {FilePath}", filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read {Component} from {FilePath}", componentName, filePath);
        }
    }

    // =========================================================================
    // Device ID Computation
    // =========================================================================

    /// <summary>
    /// Computes a deterministic SHA-256 hash from all hardware components.
    /// Components are sorted by name to ensure the same hash regardless of read order.
    /// Format: "NAME1:VALUE1|NAME2:VALUE2|..."
    /// </summary>
    private static string ComputeDeviceId(List<HardwareComponent> components)
    {
        var sortedInput = string.Join("|",
            components
                .OrderBy(c => c.Name, StringComparer.Ordinal)
                .Select(c => $"{c.Name}:{c.Value}"));

        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(sortedInput));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
