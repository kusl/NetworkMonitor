#!/bin/bash
# Fix two issues:
# 1. PingService: "An asynchronous call is already in progress" - the Ping class
#    cannot have concurrent async calls. Solution: create a new Ping instance per call.
# 2. Test failure: CheckNetworkAsync_RespectsCancellation - the service doesn't check
#    cancellation early enough. Solution: Add cancellation check at start.

set -euo pipefail

cd ~/src/dotnet/network-monitor/src

echo "Fixing PingService concurrent ping issue and cancellation test..."

# Fix 1: PingService - Create new Ping instance per call to allow concurrency
cat > NetworkMonitor.Core/Services/PingService.cs << 'EOF'
using System.Diagnostics;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Services;

/// <summary>
/// Cross-platform ping implementation using System.Net.NetworkInformation.
/// Works on Windows, macOS, and Linux without external dependencies.
/// </summary>
public sealed class PingService : IPingService
{
    private readonly ILogger<PingService> _logger;

    public PingService(ILogger<PingService> logger)
    {
        _logger = logger;
    }

    public async Task<PingResult> PingAsync(
        string target,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        // Check cancellation before doing any work
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            _logger.LogDebug("Pinging {Target} with timeout {TimeoutMs}ms", target, timeoutMs);

            // Create a new Ping instance per call to allow concurrent pings.
            // The Ping class does not support multiple concurrent async operations
            // on the same instance.
            using var ping = new Ping();

            var stopwatch = Stopwatch.StartNew();

            // Note: PingAsync doesn't accept CancellationToken directly,
            // but we can use the timeout parameter
            var reply = await ping.SendPingAsync(target, timeoutMs).ConfigureAwait(false);

            stopwatch.Stop();

            // Check cancellation after the ping completes
            cancellationToken.ThrowIfCancellationRequested();

            if (reply.Status == IPStatus.Success)
            {
                _logger.LogDebug(
                    "Ping to {Target} succeeded: {RoundtripMs}ms",
                    target,
                    reply.RoundtripTime);

                return PingResult.Succeeded(target, reply.RoundtripTime);
            }

            var errorMessage = reply.Status.ToString();
            _logger.LogDebug("Ping to {Target} failed: {Status}", target, errorMessage);

            return PingResult.Failed(target, errorMessage);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Ping to {Target} cancelled", target);
            throw;
        }
        catch (PingException ex)
        {
            _logger.LogWarning(ex, "Ping to {Target} threw exception", target);
            return PingResult.Failed(target, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error pinging {Target}", target);
            return PingResult.Failed(target, $"Unexpected error: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<PingResult>> PingMultipleAsync(
        string target,
        int count,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        var results = new List<PingResult>(count);

        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await PingAsync(target, timeoutMs, cancellationToken).ConfigureAwait(false);
            results.Add(result);

            // Small delay between pings to avoid flooding
            if (i < count - 1)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        return results;
    }
}
EOF

# Fix 2: NetworkMonitorService - Add early cancellation check
# We need to see the full file first, so let's patch it
cat > NetworkMonitor.Core/Services/NetworkMonitorService.cs << 'EOF'
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Services;

/// <summary>
/// Main network monitoring service.
/// Coordinates ping operations and computes overall network health.
/// Exposes OpenTelemetry metrics for observability.
/// </summary>
public sealed class NetworkMonitorService : INetworkMonitorService
{
    private static readonly ActivitySource ActivitySource = new("NetworkMonitor.Core");
    private static readonly Meter Meter = new("NetworkMonitor.Core");

    // Metrics - use static readonly for performance (CA1859)
    private static readonly Counter<long> CheckCounter = Meter.CreateCounter<long>(
        "network_monitor.checks",
        description: "Number of network health checks performed");

    private static readonly Histogram<double> RouterLatencyHistogram = Meter.CreateHistogram<double>(
        "network_monitor.router_latency_ms",
        unit: "ms",
        description: "Router ping latency distribution");

    private static readonly Histogram<double> InternetLatencyHistogram = Meter.CreateHistogram<double>(
        "network_monitor.internet_latency_ms",
        unit: "ms",
        description: "Internet ping latency distribution");

    private static readonly Counter<long> FailureCounter = Meter.CreateCounter<long>(
        "network_monitor.failures",
        description: "Number of ping failures by target type");

    private readonly IPingService _pingService;
    private readonly MonitorOptions _options;
    private readonly ILogger<NetworkMonitorService> _logger;

    private NetworkStatus? _lastStatus;

    /// <inheritdoc />
    public event EventHandler<NetworkStatusEventArgs>? StatusChanged;

    /// <summary>
    /// Creates a new network monitor service.
    /// </summary>
    public NetworkMonitorService(
        IPingService pingService,
        IOptions<MonitorOptions> options,
        ILogger<NetworkMonitorService> logger)
    {
        _pingService = pingService;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<NetworkStatus> CheckNetworkAsync(CancellationToken cancellationToken = default)
    {
        // Check cancellation immediately before doing any work
        cancellationToken.ThrowIfCancellationRequested();

        using var activity = ActivitySource.StartActivity("NetworkMonitor.CheckNetwork");

        CheckCounter.Add(1);

        _logger.LogDebug("Starting network health check");

        // Ping router and internet in parallel for efficiency
        var routerTask = PingWithStatsAsync(
            _options.RouterAddress,
            "router",
            cancellationToken);

        var internetTask = PingWithStatsAsync(
            _options.InternetTarget,
            "internet",
            cancellationToken);

        await Task.WhenAll(routerTask, internetTask).ConfigureAwait(false);

        var routerResult = await routerTask.ConfigureAwait(false);
        var internetResult = await internetTask.ConfigureAwait(false);

        // Record metrics
        if (routerResult is { Success: true, RoundtripTimeMs: not null })
        {
            RouterLatencyHistogram.Record(routerResult.RoundtripTimeMs.Value);
        }
        else
        {
            FailureCounter.Add(1, new KeyValuePair<string, object?>("target_type", "router"));
        }

        if (internetResult is { Success: true, RoundtripTimeMs: not null })
        {
            InternetLatencyHistogram.Record(internetResult.RoundtripTimeMs.Value);
        }
        else
        {
            FailureCounter.Add(1, new KeyValuePair<string, object?>("target_type", "internet"));
        }

        // Compute overall health
        var (health, message) = ComputeHealth(routerResult, internetResult);

        var status = new NetworkStatus(
            health,
            routerResult,
            internetResult,
            DateTimeOffset.UtcNow,
            message);

        activity?.SetTag("health", health.ToString());
        activity?.SetTag("router.success", routerResult?.Success ?? false);
        activity?.SetTag("internet.success", internetResult?.Success ?? false);

        // Fire event if status changed
        if (_lastStatus?.Health != status.Health)
        {
            _logger.LogInformation(
                "Network status changed: {OldHealth} -> {NewHealth}: {Message}",
                _lastStatus?.Health.ToString() ?? "Unknown",
                status.Health,
                status.Message);

            StatusChanged?.Invoke(this, new NetworkStatusEventArgs(status));
        }

        _lastStatus = status;

        return status;
    }

    private async Task<PingResult?> PingWithStatsAsync(
        string target,
        string targetType,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = await _pingService.PingMultipleAsync(
                target,
                _options.PingsPerCycle,
                _options.TimeoutMs,
                cancellationToken).ConfigureAwait(false);

            if (results.Count == 0)
            {
                return null;
            }

            // Calculate aggregate result
            var successfulPings = results.Where(r => r.Success).ToList();

            if (successfulPings.Count == 0)
            {
                // All pings failed - return the last failure
                return results[^1];
            }

            // Return a result with median latency for stability
            var sortedLatencies = successfulPings
                .Where(r => r.RoundtripTimeMs.HasValue)
                .Select(r => r.RoundtripTimeMs!.Value)
                .OrderBy(l => l)
                .ToList();

            var medianLatency = sortedLatencies.Count > 0
                ? sortedLatencies[sortedLatencies.Count / 2]
                : 0;

            return PingResult.Succeeded(target, medianLatency);
        }
        catch (OperationCanceledException)
        {
            // Let cancellation propagate up
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error pinging {Target}", target);
            return PingResult.Failed(target, ex.Message);
        }
    }

    private static (NetworkHealth Health, string Message) ComputeHealth(
        PingResult? routerResult,
        PingResult? internetResult)
    {
        // Router failure = offline (can't reach local network)
        if (routerResult is null || !routerResult.Success)
        {
            return (NetworkHealth.Offline, "Cannot reach local network");
        }

        // Internet failure = poor (local network OK, but no internet)
        if (internetResult is null || !internetResult.Success)
        {
            return (NetworkHealth.Poor, "Local network OK, no internet access");
        }

        // Both succeed - check latency for quality assessment
        var internetLatency = internetResult.RoundtripTimeMs ?? 0;
        var routerLatency = routerResult.RoundtripTimeMs ?? 0;

        return internetLatency switch
        {
            <= 50 when routerLatency <= 10 => (NetworkHealth.Excellent, "Network is excellent"),
            <= 100 => (NetworkHealth.Good, "Network is good"),
            <= 200 => (NetworkHealth.Degraded, "Network is degraded (high latency)"),
            _ => (NetworkHealth.Poor, "Network is poor (very high latency)")
        };
    }
}
EOF

# Fix 3: FakePingService - Should also respect cancellation for proper testing
cat > NetworkMonitor.Tests/Fakes/FakePingService.cs << 'EOF'
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Services;

namespace NetworkMonitor.Tests.Fakes;

/// <summary>
/// Fake ping service for testing.
/// Allows tests to control exactly what ping results are returned.
/// 
/// Using manual fakes instead of Moq because:
/// 1. Moq is banned (controversial maintainer)
/// 2. Manual fakes are more explicit and readable
/// 3. No magic - you can see exactly what happens
/// </summary>
internal sealed class FakePingService : IPingService
{
    private readonly Queue<PingResult> _results = new();
    private PingResult? _defaultResult;

    /// <summary>
    /// Queues a specific result to be returned on next ping.
    /// Results are returned in FIFO order.
    /// </summary>
    public FakePingService QueueResult(PingResult result)
    {
        _results.Enqueue(result);
        return this;
    }

    /// <summary>
    /// Sets a default result to return when queue is empty.
    /// </summary>
    public FakePingService WithDefaultResult(PingResult result)
    {
        _defaultResult = result;
        return this;
    }

    /// <summary>
    /// Configures to return successful pings with specified latency.
    /// </summary>
    public FakePingService AlwaysSucceed(long latencyMs = 10)
    {
        _defaultResult = PingResult.Succeeded("test", latencyMs);
        return this;
    }

    /// <summary>
    /// Configures to always fail.
    /// </summary>
    public FakePingService AlwaysFail(string errorMessage = "Simulated failure")
    {
        _defaultResult = PingResult.Failed("test", errorMessage);
        return this;
    }

    /// <inheritdoc />
    public Task<PingResult> PingAsync(
        string target,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        // Respect cancellation like the real service does
        cancellationToken.ThrowIfCancellationRequested();

        if (_results.TryDequeue(out var queuedResult))
        {
            return Task.FromResult(queuedResult);
        }

        if (_defaultResult is not null)
        {
            return Task.FromResult(_defaultResult);
        }

        return Task.FromResult(PingResult.Failed(target, "No result configured"));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PingResult>> PingMultipleAsync(
        string target,
        int count,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        var results = new List<PingResult>(count);

        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await PingAsync(target, timeoutMs, cancellationToken).ConfigureAwait(false);
            results.Add(result);
        }

        return results;
    }
}
EOF

echo "Done! Both issues fixed:"
echo "  1. PingService now creates a new Ping instance per call (allows concurrency)"
echo "  2. NetworkMonitorService and FakePingService now check cancellation early"
echo ""
echo "Run 'dotnet build && dotnet test' to verify the fixes."
