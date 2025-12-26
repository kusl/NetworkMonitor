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
