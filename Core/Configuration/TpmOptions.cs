namespace SecureDeviceBridge.Core.Configuration;

/// <summary>
/// Strongly-typed configuration for TPM device settings.
/// Bound to the "Tpm" section of appsettings.json.
/// </summary>
public sealed class TpmOptions
{
    /// <summary>
    /// The persistent handle address for the RSA signing key (hex string without 0x prefix).
    /// Default: "81010001" (Owner hierarchy persistent range).
    /// </summary>
    public string PersistentHandle { get; set; } = "81010001";

    /// <summary>
    /// RSA key size in bits. Default: 2048.
    /// </summary>
    public int KeySizeBits { get; set; } = 2048;

    /// <summary>
    /// Hash algorithm used for signing. Default: "SHA256".
    /// </summary>
    public string HashAlgorithm { get; set; } = "SHA256";

    /// <summary>
    /// Optional TPM Owner hierarchy authorization value (hex string).
    /// Leave empty if the Owner auth is default (empty).
    /// </summary>
    public string OwnerAuth { get; set; } = string.Empty;

    /// <summary>
    /// Parses the hex persistent handle string into a uint value.
    /// </summary>
    public uint GetPersistentHandleValue()
    {
        string hex = PersistentHandle.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? PersistentHandle[2..]
            : PersistentHandle;

        return uint.Parse(hex, System.Globalization.NumberStyles.HexNumber);
    }

    /// <summary>
    /// Returns the Owner auth as a byte array. Empty array if not configured.
    /// </summary>
    public byte[] GetOwnerAuthBytes()
    {
        if (string.IsNullOrWhiteSpace(OwnerAuth))
            return Array.Empty<byte>();

        return Convert.FromHexString(OwnerAuth);
    }
}
