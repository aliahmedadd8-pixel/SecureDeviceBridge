using SecureDeviceBridge.Core.Interfaces;

namespace SecureDeviceBridge.Worker;

/// <summary>
/// Background service that monitors TPM health on a periodic interval.
/// Logs connectivity status and key availability for operational visibility.
/// 
/// This service does NOT perform TPM operations — it only reads the cached status
/// from <see cref="ITpmService.GetStatus"/> (lock-free, non-blocking).
/// </summary>
public sealed class TpmHealthMonitor : BackgroundService
{
    private readonly ITpmService _tpmService;
    private readonly ILogger<TpmHealthMonitor> _logger;
    private readonly TimeSpan _healthCheckInterval = TimeSpan.FromSeconds(60);

    public TpmHealthMonitor(
        ITpmService tpmService,
        ILogger<TpmHealthMonitor> logger)
    {
        _tpmService = tpmService ?? throw new ArgumentNullException(nameof(tpmService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "TpmHealthMonitor started. Health check interval: {Interval}s",
            _healthCheckInterval.TotalSeconds);

        // ── Initial startup probe ──
        PerformHealthCheck("Startup");

        // ── Periodic health probes ──
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_healthCheckInterval, stoppingToken).ConfigureAwait(false);
                PerformHealthCheck("Periodic");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown — expected, not an error.
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in TpmHealthMonitor loop");

                // Back off to avoid tight error loops
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("TpmHealthMonitor stopped gracefully.");
    }

    /// <summary>
    /// Reads the TPM service status and logs it.
    /// This is a non-blocking, lock-free operation.
    /// </summary>
    private void PerformHealthCheck(string checkType)
    {
        try
        {
            var status = _tpmService.GetStatus();

            if (status.TpmAvailable)
            {
                _logger.LogInformation(
                    "[{CheckType}] TPM Health OK — Available: {Available}, Key loaded: {KeyLoaded}",
                    checkType, status.TpmAvailable, status.KeyLoaded);
            }
            else
            {
                _logger.LogWarning(
                    "[{CheckType}] TPM Health DEGRADED — Available: {Available}, Key loaded: {KeyLoaded}, " +
                    "LastError: {LastError}",
                    checkType, status.TpmAvailable, status.KeyLoaded, status.LastError ?? "N/A");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{CheckType}] Failed to read TPM service status", checkType);
        }
    }
}
