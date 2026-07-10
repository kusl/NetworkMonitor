using System.Diagnostics;
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
/// <remarks>
/// Cadence is measured start-to-start: the configured interval is the time
/// between the beginning of successive cycles, not an extra gap tacked on after
/// each cycle finishes. This keeps the effective period stable even as the work
/// per cycle varies. If a cycle runs longer than the interval, the next one
/// starts immediately and a one-time warning is logged.
/// </remarks>
public sealed class MonitorBackgroundService : BackgroundService
{
    private readonly INetworkMonitorService _monitorService;
    private readonly IStatusDisplay _display;
    private readonly IStorageService _storage;
    private readonly MonitorOptions _options;
    private readonly ILogger<MonitorBackgroundService> _logger;

    /// <summary>
    /// Creates a new monitor background service.
    /// </summary>
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

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Network Monitor starting. Interval: {IntervalMs}ms, Router: {Router}, Internet: {Internet}, " +
            "IPv6: {IPv6}, DNS: {Dns}, CustomTargets: {CustomCount}, MaxConcurrentChecks: {MaxConcurrent}",
            _options.IntervalMs,
            _options.RouterAddress,
            _options.InternetTarget,
            _options.EnableIPv6,
            _options.EnableDnsChecks,
            _options.CustomTargets.Count,
            _options.MaxConcurrentChecks);

        // Subscribe to status changes for logging significant events
        _monitorService.StatusChanged += OnStatusChanged;

        var interval = TimeSpan.FromMilliseconds(Math.Max(250, _options.IntervalMs));
        var overrunWarned = false;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var startedAt = Stopwatch.GetTimestamp();

                try
                {
                    var status = await _monitorService.CheckNetworkAsync(stoppingToken);

                    // Update display
                    _display.UpdateStatus(status);

                    // Persist results (never throws - storage failures are swallowed)
                    await _storage.SaveStatusAsync(status, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Continue monitoring even if one cycle fails
                    _logger.LogError(ex, "Error during monitoring cycle");
                }

                var elapsed = Stopwatch.GetElapsedTime(startedAt);
                var remaining = interval - elapsed;

                if (remaining <= TimeSpan.Zero)
                {
                    if (!overrunWarned)
                    {
                        _logger.LogWarning(
                            "A monitoring cycle took {Elapsed:F0}ms, exceeding the configured interval of {Interval}ms. " +
                            "Cycles will run back-to-back. Consider raising IntervalMs, lowering PingsPerCycle, " +
                            "or trimming CustomTargets.",
                            elapsed.TotalMilliseconds,
                            _options.IntervalMs);
                        overrunWarned = true;
                    }

                    continue; // start the next cycle immediately
                }

                try
                {
                    await Task.Delay(remaining, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
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

    private void OnStatusChanged(object? sender, NetworkStatusEventArgs e)
    {
        // Log significant status changes
        if (e.Status.Health == NetworkHealth.Offline)
        {
            _logger.LogWarning("Network is OFFLINE: {Message}", e.Status.Message);
        }
        else if (e.Status.Health == NetworkHealth.Poor)
        {
            _logger.LogWarning("Network is POOR: {Message}", e.Status.Message);
        }
    }
}
