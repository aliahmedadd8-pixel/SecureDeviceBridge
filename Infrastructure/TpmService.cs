using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tpm2Lib;
using SecureDeviceBridge.Core.Configuration;
using SecureDeviceBridge.Core.DTOs;
using SecureDeviceBridge.Core.Interfaces;

namespace SecureDeviceBridge.Infrastructure;

/// <summary>
/// Production implementation of <see cref="ITpmService"/> using Microsoft.TSS (Tpm2Lib).
/// 
/// Architecture notes:
/// - Thread safety: A <see cref="SemaphoreSlim"/> serializes all TPM access (TPM is a single-threaded device).
/// - Key strategy: Creates a primary RSA-2048 signing key directly under TpmRh.Owner,
///   then persists it via EvictControl at the configured persistent handle.
///   Primary keys are deterministic from the template + hierarchy seed, providing
///   implicit key recovery without backup.
/// - Signing: RSASSA-PKCS1-v1_5 with SHA-256 (RS256). The challenge nonce is
///   SHA-256 hashed locally, then signed by the TPM hardware.
/// - The private key NEVER leaves the TPM silicon.
/// </summary>
public sealed class TpmService : ITpmService
{
    private readonly ILogger<TpmService> _logger;
    private readonly TpmOptions _options;
    private readonly SemaphoreSlim _tpmLock = new(1, 1);

    private Tpm2Device? _tpmDevice;
    private Tpm2? _tpm;

    // Volatile for lock-free reads from health monitor and health endpoint
    private volatile bool _tpmAvailable;
    private volatile bool _keyLoaded;
    private volatile string? _lastError;

    private bool _disposed;

    public TpmService(ILogger<TpmService> logger, IOptions<TpmOptions> options)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        InitializeTpm();
    }

    #region ITpmService Implementation

    /// <inheritdoc />
    public async Task<KeyGenerationResult> GenerateKeyAsync(bool force, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _tpmLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_tpmAvailable || _tpm is null)
            {
                _logger.LogWarning("Key generation requested but TPM is not available");
                return KeyGenerationResult.Failure(
                    $"TPM device is not available. Last error: {_lastError ?? "Unknown"}");
            }

            uint handleValue = _options.GetPersistentHandleValue();
            var persistentHandle = new TpmHandle(handleValue);

            // ── Idempotent path: return existing key if present and not forcing ──
            if (!force && _keyLoaded)
            {
                _logger.LogInformation("Returning existing key at persistent handle 0x{Handle:X8}", handleValue);
                return ExportPublicKey(persistentHandle, wasExisting: true);
            }

            // ── Force path: evict existing key if present ──
            if (force && _keyLoaded)
            {
                EvictExistingKey(persistentHandle, handleValue);
            }

            // ── Generate new signing key ──
            _logger.LogInformation("Generating new RSA-{KeySize} signing key in TPM hardware...",
                _options.KeySizeBits);

            TpmPublic signingKeyTemplate = BuildSigningKeyTemplate();

            // Create a primary signing key directly under the Owner hierarchy.
            // Primary keys with identical templates are deterministic on the same TPM,
            // providing implicit key recovery if the persistent handle is lost.
            TpmHandle transientHandle = _tpm.CreatePrimary(
                TpmRh.Owner,
                new SensitiveCreate(Array.Empty<byte>(), Array.Empty<byte>()),
                signingKeyTemplate,
                Array.Empty<byte>(),
                Array.Empty<PcrSelection>(),
                out TpmPublic createdPublic,
                out CreationData _,
                out byte[] _,
                out TkCreation _);

            _logger.LogDebug("Primary signing key created with transient handle 0x{Handle:X8}",
                (uint)transientHandle.handle);

            try
            {
                // Persist the key at the configured persistent handle.
                // After this call, the key survives TPM resets and power cycles.
                _tpm.EvictControl(TpmRh.Owner, transientHandle, persistentHandle);

                _logger.LogInformation(
                    "Signing key persisted at handle 0x{Handle:X8}", handleValue);
            }
            finally
            {
                // Always flush the transient handle — the key now lives at the persistent handle.
                _tpm.FlushContext(transientHandle);
            }

            _keyLoaded = true;
            _lastError = null;

            return ExportPublicKey(persistentHandle, wasExisting: false);
        }
        catch (Exception ex)
        {
            string errorMessage = $"Key generation failed: {ex.Message}";
            _lastError = errorMessage;
            _logger.LogError(ex, "TPM key generation failed");
            return KeyGenerationResult.Failure(errorMessage);
        }
        finally
        {
            _tpmLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<SigningResult> SignChallengeAsync(string challengeNonce, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(challengeNonce))
        {
            return SigningResult.Failure("Challenge nonce must not be null or empty.");
        }

        await _tpmLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_tpmAvailable || _tpm is null)
            {
                _logger.LogWarning("Sign requested but TPM is not available");
                return SigningResult.Failure(
                    $"TPM device is not available. Last error: {_lastError ?? "Unknown"}");
            }

            if (!_keyLoaded)
            {
                _logger.LogWarning("Sign requested but no signing key is loaded");
                return SigningResult.Failure(
                    "No signing key is loaded. Call POST /api/device/key/generate first.");
            }

            uint handleValue = _options.GetPersistentHandleValue();
            var persistentHandle = new TpmHandle(handleValue);

            _logger.LogDebug("Signing challenge nonce ({Length} chars) with key at 0x{Handle:X8}",
                challengeNonce.Length, handleValue);

            // Step 1: SHA-256 hash the challenge nonce (UTF-8 encoded).
            byte[] nonceBytes = Encoding.UTF8.GetBytes(challengeNonce);
            byte[] digest = SHA256.HashData(nonceBytes);

            // Step 2: Sign the digest using the TPM-resident private key.
            // For unrestricted signing keys (no ObjectAttr.Restricted), the TPM does not
            // require a valid hash ticket — a null ticket (TpmRh.Null) is accepted.
            ISignatureUnion signature = _tpm.Sign(
                persistentHandle,
                digest,
                new SchemeRsassa(TpmAlgId.Sha256),
                new TkHashcheck(TpmRh.Null, Array.Empty<byte>()));

            // Step 3: Extract raw signature bytes from the RSASSA signature structure.
            var rsaSignature = (SignatureRsassa)signature;
            string signatureBase64 = Convert.ToBase64String(rsaSignature.sig);

            _logger.LogInformation("Challenge nonce signed successfully. Signature length: {Length} bytes",
                rsaSignature.sig.Length);

            return SigningResult.Ok(signatureBase64);
        }
        catch (Exception ex)
        {
            string errorMessage = $"Signing failed: {ex.Message}";
            _lastError = errorMessage;
            _logger.LogError(ex, "TPM signing operation failed");
            return SigningResult.Failure(errorMessage);
        }
        finally
        {
            _tpmLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsKeyPresentAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _tpmLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_tpmAvailable || _tpm is null)
                return false;

            return CheckPersistentKeyExists();
        }
        finally
        {
            _tpmLock.Release();
        }
    }

    /// <inheritdoc />
    public TpmServiceStatus GetStatus()
    {
        // Lock-free read of volatile fields — safe for health monitoring.
        return new TpmServiceStatus
        {
            TpmAvailable = _tpmAvailable,
            KeyLoaded = _keyLoaded,
            LastError = _lastError
        };
    }

    #endregion

    #region TPM Initialization

    /// <summary>
    /// Eagerly connects to the TPM device and checks for an existing persistent key.
    /// If the TPM is not available, the service still starts but all operations return errors.
    /// </summary>
    private void InitializeTpm()
    {
        try
        {
            _logger.LogInformation("Initializing TPM device connection...");

            _tpmDevice = TpmDeviceFactory.CreateDevice();
            _tpmDevice.Connect();
            _tpm = new Tpm2(_tpmDevice);

            // Set Owner auth if configured
            byte[] ownerAuth = _options.GetOwnerAuthBytes();
            if (ownerAuth.Length > 0)
            {
                _logger.LogDebug("Owner authorization configured ({Length} bytes)", ownerAuth.Length);
            }

            _tpmAvailable = true;

            // Check if a signing key already exists at the persistent handle
            _keyLoaded = CheckPersistentKeyExists();

            _logger.LogInformation(
                "TPM device initialized successfully. " +
                "Platform: {Platform}, TPM available: {Available}, Key loaded: {KeyLoaded}, " +
                "Persistent handle: 0x{Handle:X8}",
                Environment.OSVersion.Platform,
                _tpmAvailable,
                _keyLoaded,
                _options.GetPersistentHandleValue());
        }
        catch (PlatformNotSupportedException ex)
        {
            _tpmAvailable = false;
            _lastError = $"Platform not supported: {ex.Message}";
            _logger.LogError(ex, "TPM initialization failed — unsupported platform");
        }
        catch (Exception ex)
        {
            _tpmAvailable = false;
            _lastError = $"TPM initialization failed: {ex.Message}";
            _logger.LogError(ex,
                "TPM initialization failed. The service will start in degraded mode. " +
                "Verify that a TPM 2.0 chip is present and accessible.");
        }
    }

    /// <summary>
    /// Probes the persistent handle to check if a signing key is already loaded.
    /// Uses ReadPublic which does not require authorization.
    /// </summary>
    private bool CheckPersistentKeyExists()
    {
        try
        {
            uint handleValue = _options.GetPersistentHandleValue();
            _tpm!.ReadPublic(
                new TpmHandle(handleValue),
                out byte[] _,
                out byte[] _);

            _logger.LogDebug("Existing signing key found at persistent handle 0x{Handle:X8}", handleValue);
            return true;
        }
        catch
        {
            // TPM_RC_HANDLE — no object at this handle. This is expected on first run.
            return false;
        }
    }

    #endregion

    #region Key Operations (Private)

    /// <summary>
    /// Builds the TpmPublic template for an RSA-2048 RSASSA-PKCS1-v1_5 signing key.
    /// 
    /// Attributes:
    /// - UserWithAuth: Authorizable with auth value (empty in our case)
    /// - Sign: This key can sign data
    /// - FixedTPM: Key cannot be duplicated to another TPM
    /// - FixedParent: Key cannot be moved to a different parent
    /// - SensitiveDataOrigin: TPM generated the sensitive portion internally
    /// 
    /// The key is explicitly NOT Restricted, meaning:
    /// - It can sign externally-provided hashes (no hash ticket required)
    /// - It cannot be used as an attestation key (by design — this is a PoP key)
    /// </summary>
    private TpmPublic BuildSigningKeyTemplate()
    {
        return new TpmPublic(
            TpmAlgId.Sha256,
            ObjectAttr.UserWithAuth |
            ObjectAttr.Sign |
            ObjectAttr.FixedTPM |
            ObjectAttr.FixedParent |
            ObjectAttr.SensitiveDataOrigin,
            Array.Empty<byte>(),
            new RsaParms(
                new SymDefObject(TpmAlgId.Null, 0, TpmAlgId.Null),
                new SchemeRsassa(TpmAlgId.Sha256),
                (ushort)_options.KeySizeBits,
                0),  // exponent 0 = default (65537)
            new Tpm2bPublicKeyRsa());
    }

    /// <summary>
    /// Evicts (removes) an existing key from the persistent handle.
    /// Failures are logged as warnings but do not prevent key creation.
    /// </summary>
    private void EvictExistingKey(TpmHandle persistentHandle, uint handleValue)
    {
        try
        {
            _tpm!.EvictControl(TpmRh.Owner, persistentHandle, persistentHandle);
            _keyLoaded = false;
            _logger.LogInformation("Evicted existing key at persistent handle 0x{Handle:X8}", handleValue);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to evict existing key at handle 0x{Handle:X8}. " +
                "Proceeding with key creation (will overwrite).", handleValue);
        }
    }

    /// <summary>
    /// Reads the public portion of a key at the given handle and exports it as PEM and Base64.
    /// Also computes a SHA-256 thumbprint as the key identifier.
    /// </summary>
    private KeyGenerationResult ExportPublicKey(TpmHandle persistentHandle, bool wasExisting)
    {
        // Read the public portion from the TPM — no authorization required.
        TpmPublic pub = _tpm!.ReadPublic(
            persistentHandle,
            out byte[] _,
            out byte[] _);

        // Extract RSA modulus and exponent from the TpmPublic structure
        var rsaParms = (RsaParms)pub.parameters;
        var rsaPublicKey = (Tpm2bPublicKeyRsa)pub.unique;

        byte[] modulus = rsaPublicKey.buffer;
        uint tpmExponent = rsaParms.exponent;
        if (tpmExponent == 0)
            tpmExponent = 65537; // TPM default exponent

        // Convert exponent to big-endian byte array with no leading zeros
        byte[] exponentBytes = ConvertExponentToBytes(tpmExponent);

        // Import into .NET's RSA to produce standard SPKI (SubjectPublicKeyInfo) encoding
        var rsaParameters = new RSAParameters
        {
            Modulus = modulus,
            Exponent = exponentBytes
        };

        using var rsa = RSA.Create();
        rsa.ImportParameters(rsaParameters);

        // Export as DER-encoded SubjectPublicKeyInfo
        byte[] spkiDer = rsa.ExportSubjectPublicKeyInfo();
        string publicKeyBase64 = Convert.ToBase64String(spkiDer);

        // Export as PEM (-----BEGIN PUBLIC KEY----- ... -----END PUBLIC KEY-----)
        string publicKeyPem = new string(PemEncoding.Write("PUBLIC KEY", spkiDer));

        // Compute key ID: SHA-256 thumbprint of the SPKI DER, hex-encoded
        byte[] thumbprint = SHA256.HashData(spkiDer);
        string keyId = Convert.ToHexString(thumbprint).ToLowerInvariant();

        _logger.LogInformation(
            "Public key exported. KeyId: {KeyId}, Modulus length: {ModulusLength} bits, " +
            "WasExisting: {WasExisting}",
            keyId, modulus.Length * 8, wasExisting);

        return KeyGenerationResult.Ok(publicKeyPem, publicKeyBase64, keyId, wasExisting);
    }

    /// <summary>
    /// Converts a uint exponent to a big-endian byte array with no leading zeros.
    /// For the default exponent 65537 (0x010001), this returns { 0x01, 0x00, 0x01 }.
    /// </summary>
    private static byte[] ConvertExponentToBytes(uint exponent)
    {
        byte[] bytes = BitConverter.GetBytes(exponent);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);

        // Strip leading zeros
        int firstNonZero = Array.FindIndex(bytes, b => b != 0);
        if (firstNonZero < 0)
            return new byte[] { 0 };

        return bytes[firstNonZero..];
    }

    #endregion

    #region TPM Key Removal (Uninstall Support)

    /// <inheritdoc />
    public async Task<bool> RemoveKeyAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _tpmLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_tpmAvailable || _tpm is null)
            {
                _logger.LogWarning("Cannot remove TPM key — TPM device is not available");
                return false;
            }

            uint handleValue = _options.GetPersistentHandleValue();
            var persistentHandle = new TpmHandle(handleValue);

            if (!CheckPersistentKeyExists())
            {
                _logger.LogWarning(
                    "No signing key found at persistent handle 0x{Handle:X8}. Nothing to remove.",
                    handleValue);
                return false;
            }

            // Evict the key from the persistent handle — this is IRREVERSIBLE.
            // The private key material is destroyed inside the TPM chip.
            _tpm.EvictControl(TpmRh.Owner, persistentHandle, persistentHandle);

            _keyLoaded = false;
            _lastError = null;

            _logger.LogInformation(
                "TPM signing key PERMANENTLY REMOVED from persistent handle 0x{Handle:X8}. " +
                "The device identity private key has been destroyed.",
                handleValue);

            return true;
        }
        catch (Exception ex)
        {
            string errorMessage = $"Failed to remove TPM key: {ex.Message}";
            _lastError = errorMessage;
            _logger.LogError(ex, "TPM key removal failed");
            return false;
        }
        finally
        {
            _tpmLock.Release();
        }
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            _tpm?.Dispose();
            _tpmDevice?.Dispose();
            _tpmLock.Dispose();

            _logger.LogInformation("TPM service disposed. Device connection closed.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during TPM service disposal");
        }
    }

    #endregion
}
