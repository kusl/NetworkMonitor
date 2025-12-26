using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Storage;

namespace NetworkMonitor.Core.Services;

/// <summary>
/// Background service that runs the continuous monitoring loop.
/// Implements IHostedService for proper lifecycle management.
/// </summary>
public sealed class MonitorBackgroundService : BackgroundService
{
    private readonly INetworkMonitorService _monitorService;
    private readonly IStatusDisplay _display;
    private readonly IStorageService _storage;
    private readonly MonitorOptions _options;
    private readonly ILogger<MonitorBackgroundService> _logger;

    public MonitorBackgroundService(
        INetworkMonitorService monitorService,
        IStatusDisplay display,
        IStorageService storage,
        IOptions<MonitorOptions> options,
        ILogger<MonitorBackgroundService> logger)
    {
        _monitorService = monitorService;
        _display = display;
        _storage = storage;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Network Monitor starting. Interval: {IntervalMs}ms, Router: {Router}, Internet: {Internet}",
            _options.IntervalMs,
            _options.RouterAddress,
            _options.InternetTarget);

        // Subscribe to status changes for logging significant events
        _monitorService.StatusChanged += OnStatusChanged;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var status = await _monitorService.CheckNetworkAsync(stoppingToken).ConfigureAwait(false);

                    // Update display
                    _display.UpdateStatus(status);

                    // Persist results
                    await _storage.SaveStatusAsync(status, stoppingToken).ConfigureAwait(false);

                    // Wait for next cycle
                    await Task.Delay(_options.IntervalMs, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Normal shutdown
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during monitoring cycle");

                    // Continue monitoring even if one cycle fails
                    await Task.Delay(_options.IntervalMs, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _monitorService.StatusChanged -= OnStatusChanged;
            _display.Clear();
        }

        _logger.LogInformation("Network Monitor stopped");
    }

    private void OnStatusChanged(object? sender, NetworkStatus status)
    {
        // Log significant status changes
        if (status.Health == NetworkHealth.Offline)
        {
            _logger.LogWarning("Network is OFFLINE: {Message}", status.Message);
        }
        else if (status.Health == NetworkHealth.Poor)
        {
            _logger.LogWarning("Network is POOR: {Message}", status.Message);
        }
    }
}
