using System.Text.Json.Serialization;

namespace SecureDeviceBridge.Core.DTOs;

/// <summary>
/// Represents a single hardware component reading used in the Device ID fingerprint.
/// </summary>
public sealed class HardwareComponent
{
    /// <summary>
    /// Canonical name of the hardware component (e.g., "CPU_ID", "SMBIOS_UUID").
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// The value read from the hardware (e.g., "BFEBFBFF000906A4").
    /// </summary>
    [JsonPropertyName("value")]
    public required string Value { get; init; }

    /// <summary>
    /// The data source used to read this value (e.g., "Win32_Processor.ProcessorId").
    /// </summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }
}
