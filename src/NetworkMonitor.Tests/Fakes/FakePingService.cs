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
