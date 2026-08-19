using SecureDeviceBridge.Core.DTOs;
using SecureDeviceBridge.Core.Interfaces;

namespace SecureDeviceBridge.API;

/// <summary>
/// Registers all Secure Device Bridge Minimal API endpoints.
/// All endpoints are CORS-protected and bound to 127.0.0.1 only.
/// </summary>
public static class EndpointRouting
{
    /// <summary>
    /// Maps all Secure Device Bridge endpoints to the application pipeline.
    /// </summary>
    public static WebApplication MapSecureDeviceBridgeEndpoints(this WebApplication app)
    {
        // ── Health Check ──────────────────────────────────────────────────────
        app.MapGet("/health", HandleHealthCheck)
            .WithName("HealthCheck")
            .WithTags("Health")
            .Produces<HealthResponse>(StatusCodes.Status200OK)
            .WithDescription("Returns service status, security mode, and TPM availability.");

        // ── Key Generation ────────────────────────────────────────────────────
        app.MapPost("/api/device/key/generate", HandleKeyGeneration)
            .WithName("GenerateKey")
            .WithTags("Cryptography")
            .Accepts<KeyGenerateRequest>("application/json")
            .Produces<KeyGenerationResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status500InternalServerError)
            .WithDescription("Generates an RSA-2048 signing keypair inside the TPM. Idempotent by default.");

        // ── Challenge Signing ─────────────────────────────────────────────────
        app.MapPost("/api/device/key/sign", HandleChallengeSign)
            .WithName("SignChallenge")
            .WithTags("Cryptography")
            .Accepts<SignChallengeRequest>("application/json")
            .Produces<SigningResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status500InternalServerError)
            .WithDescription("Signs a challenge nonce using the TPM-resident private key (RSASSA-PKCS1-v1_5 / SHA-256).");

        return app;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Endpoint Handlers
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// GET /health — Returns service identity, status, security mode, and TPM availability.
    /// </summary>
    private static IResult HandleHealthCheck(
        ITpmService tpmService,
        ILogger<Program> logger)
    {
        try
        {
            var status = tpmService.GetStatus();

            var response = new HealthResponse
            {
                ServiceName = "SecureDeviceBridge",
                Status = status.TpmAvailable ? "Healthy" : "Degraded",
                SecurityMode = "TPM_Asymmetric_PoP",
                UtcTimestamp = DateTime.UtcNow,
                TpmAvailable = status.TpmAvailable,
                KeyLoaded = status.KeyLoaded
            };

            logger.LogDebug("Health check responded: {Status}, TPM: {TpmAvailable}, Key: {KeyLoaded}",
                response.Status, response.TpmAvailable, response.KeyLoaded);

            return Results.Ok(response);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error in health check endpoint");
            return Results.Problem(
                detail: "An unexpected error occurred while checking service health.",
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Health Check Failed");
        }
    }

    /// <summary>
    /// POST /api/device/key/generate — Generates an asymmetric keypair in TPM hardware.
    /// Idempotent: returns the existing key unless "force": true is specified.
    /// </summary>
    private static async Task<IResult> HandleKeyGeneration(
        ITpmService tpmService,
        ILogger<Program> logger,
        HttpContext httpContext,
        CancellationToken cancellationToken,
        KeyGenerateRequest? request = null)
    {
        try
        {
            bool force = request?.Force ?? false;

            logger.LogInformation("Key generation requested. Force: {Force}, RemoteIP: {RemoteIP}",
                force, httpContext.Connection.RemoteIpAddress);

            var result = await tpmService.GenerateKeyAsync(force, cancellationToken)
                .ConfigureAwait(false);

            if (result.Success)
            {
                logger.LogInformation("Key generation succeeded. KeyId: {KeyId}, WasExisting: {WasExisting}",
                    result.KeyId, result.WasExisting);

                return Results.Ok(result);
            }

            logger.LogWarning("Key generation failed: {Error}", result.ErrorMessage);
            return Results.Problem(
                detail: result.ErrorMessage,
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Key Generation Failed");
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Key generation request was cancelled");
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error in key generation endpoint");
            return Results.Problem(
                detail: "An unexpected error occurred during key generation.",
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Internal Server Error");
        }
    }

    /// <summary>
    /// POST /api/device/key/sign — Signs a challenge nonce with the TPM-resident private key.
    /// Returns the RSASSA-PKCS1-v1_5 / SHA-256 signature as Base64.
    /// </summary>
    private static async Task<IResult> HandleChallengeSign(
        SignChallengeRequest? request,
        ITpmService tpmService,
        ILogger<Program> logger,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        try
        {
            // ── Input validation ──
            if (request is null || string.IsNullOrWhiteSpace(request.ChallengeNonce))
            {
                logger.LogWarning("Sign request rejected: missing or empty challengeNonce");
                return Results.BadRequest(new
                {
                    error = "ChallengeNonce is required and must not be empty.",
                    field = "challengeNonce"
                });
            }

            // Guard against excessively large nonces (DoS prevention)
            if (request.ChallengeNonce.Length > 4096)
            {
                logger.LogWarning("Sign request rejected: challengeNonce too large ({Length} chars)",
                    request.ChallengeNonce.Length);
                return Results.BadRequest(new
                {
                    error = "ChallengeNonce must not exceed 4096 characters.",
                    field = "challengeNonce"
                });
            }

            logger.LogInformation("Sign request received. Nonce length: {Length}, RemoteIP: {RemoteIP}",
                request.ChallengeNonce.Length, httpContext.Connection.RemoteIpAddress);

            var result = await tpmService.SignChallengeAsync(request.ChallengeNonce, cancellationToken)
                .ConfigureAwait(false);

            if (result.Success)
            {
                logger.LogInformation("Challenge signed successfully. Algorithm: {Algorithm}", result.Algorithm);
                return Results.Ok(result);
            }

            logger.LogWarning("Signing failed: {Error}", result.ErrorMessage);
            return Results.Problem(
                detail: result.ErrorMessage,
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Signing Failed");
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Sign request was cancelled");
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error in sign endpoint");
            return Results.Problem(
                detail: "An unexpected error occurred during signing.",
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Internal Server Error");
        }
    }
}
