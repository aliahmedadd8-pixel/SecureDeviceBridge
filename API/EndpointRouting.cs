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
        // -- Health Check --------------------------------------------------------
        app.MapGet("/health", HandleHealthCheck)
            .WithName("HealthCheck")
            .WithTags("Health")
            .Produces<HealthResponse>(StatusCodes.Status200OK)
            .WithDescription("Returns service status, mode, and number of hardware components available.");

        // -- Device Identity -----------------------------------------------------
        app.MapGet("/api/device/identity", HandleDeviceIdentity)
            .WithName("DeviceIdentity")
            .WithTags("Identity")
            .Produces<DeviceIdentityResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status500InternalServerError)
            .WithDescription("Returns the unique Device ID generated from hardware component fingerprints.");

        return app;
    }

    // =========================================================================
    // Endpoint Handlers
    // =========================================================================

    /// <summary>
    /// GET /health - Returns service status and hardware component availability.
    /// </summary>
    private static IResult HandleHealthCheck(
        IHardwareFingerprintService fingerprintService,
        ILogger<Program> logger)
    {
        try
        {
            int componentCount = fingerprintService.GetAvailableComponentCount();

            var response = new HealthResponse
            {
                Status = componentCount > 0 ? "Healthy" : "Degraded",
                UtcTimestamp = DateTime.UtcNow,
                ComponentsAvailable = componentCount
            };

            logger.LogDebug("Health check responded: {Status}, Components: {Count}",
                response.Status, response.ComponentsAvailable);

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
    /// GET /api/device/identity - Reads hardware components and returns the composite Device ID.
    /// Results are cached after the first call (hardware doesn't change at runtime).
    /// </summary>
    private static async Task<IResult> HandleDeviceIdentity(
        IHardwareFingerprintService fingerprintService,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Device identity requested");

            var result = await fingerprintService.CollectFingerprintAsync(cancellationToken)
                .ConfigureAwait(false);

            if (result.Success)
            {
                logger.LogInformation("Device identity returned. DeviceId: {DeviceId}, Components: {Count}",
                    result.DeviceId, result.Components?.Count ?? 0);

                return Results.Ok(result);
            }

            logger.LogWarning("Device identity collection failed: {Error}", result.ErrorMessage);
            return Results.Problem(
                detail: result.ErrorMessage,
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Device Identity Failed");
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Device identity request was cancelled");
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error in device identity endpoint");
            return Results.Problem(
                detail: "An unexpected error occurred while collecting device identity.",
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Internal Server Error");
        }
    }
}
