using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Services;

namespace NetworkMonitor.Tests.Fakes;

/// <summary>
/// Fake ping service for testing.
/// Allows controlled responses without actual network calls.
/// </summary>
public sealed class FakePingService : IPingService
{
    private readonly Queue<PingResult> _queuedResults = new();
    private Func<string, PingResult>? _resultFactory;

    /// <summary>
    /// Queues a specific result to be returned.
    /// Results are dequeued in FIFO order.
    /// </summary>
    public FakePingService QueueResult(PingResult result)
    {
        _queuedResults.Enqueue(result);
        return this;
    }

    /// <summary>
    /// Configures the service to always succeed with the given latency.
    /// </summary>
    public FakePingService AlwaysSucceed(long latencyMs = 10)
    {
        _resultFactory = target => PingResult.Succeeded(target, latencyMs);
        return this;
    }

    /// <summary>
    /// Configures the service to always fail with the given error.
    /// </summary>
    public FakePingService AlwaysFail(string error = "Timeout")
    {
        _resultFactory = target => PingResult.Failed(target, error);
        return this;
    }

    /// <summary>
    /// Configures a custom factory for generating results.
    /// </summary>
    public FakePingService WithFactory(Func<string, PingResult> factory)
    {
        _resultFactory = factory;
        return this;
    }

    /// <summary>
    /// Clears all queued results and resets the factory.
    /// </summary>
    public FakePingService Reset()
    {
        _queuedResults.Clear();
        _resultFactory = null;
        return this;
    }

    public Task<PingResult> PingAsync(
        string target,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_queuedResults.Count > 0)
        {
            return Task.FromResult(_queuedResults.Dequeue());
        }

        if (_resultFactory != null)
        {
            return Task.FromResult(_resultFactory(target));
        }

        // Default: succeed with 10ms latency
        return Task.FromResult(PingResult.Succeeded(target, 10));
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
            results.Add(await PingAsync(target, timeoutMs, cancellationToken));
        }

        return results;
    }
    
    public void Reset() 
{
    _queuedResults.Clear();
    _specificResults.Clear();
    _alwaysSucceed = false;
}
}
