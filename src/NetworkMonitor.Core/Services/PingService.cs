using System.Diagnostics;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Services;

/// <summary>
/// Cross-platform ping implementation using System.Net.NetworkInformation.
/// Works on Windows, macOS, and Linux without external dependencies.
/// </summary>
public sealed class PingService : IPingService, IDisposable
{
    private readonly ILogger<PingService> _logger;
    private readonly Ping _ping;
    private bool _disposed;

    public PingService(ILogger<PingService> logger)
    {
        _logger = logger;
        _ping = new Ping();
    }

    public async Task<PingResult> PingAsync(
        string target,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PingService));
        }

        try
        {
            _logger.LogDebug("Pinging {Target} with timeout {TimeoutMs}ms", target, timeoutMs);

            // Create a linked token that respects both the caller's token and our timeout
            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCts.Token);

            var stopwatch = Stopwatch.StartNew();

            // Note: PingAsync doesn't accept CancellationToken directly,
            // but we can use the timeout parameter
            var reply = await _ping.SendPingAsync(target, timeoutMs).ConfigureAwait(false);

            stopwatch.Stop();

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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Ping to {Target} cancelled", target);
            return PingResult.Failed(target, "Cancelled");
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

        for (var i = 0; i < count && !cancellationToken.IsCancellationRequested; i++)
        {
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

    public void Dispose()
    {
        if (!_disposed)
        {
            _ping.Dispose();
            _disposed = true;
        }
    }
}
