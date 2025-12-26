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
public sealed class FakePingService : IPingService
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
    public FakePingService AlwaysFail(string error = "Network unreachable")
    {
        _defaultResult = PingResult.Failed("test", error);
        return this;
    }
    
    public Task<PingResult> PingAsync(
        string target, 
        int timeoutMs, 
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        
        if (_results.TryDequeue(out var result))
        {
            return Task.FromResult(result with { Target = target });
        }
        
        if (_defaultResult != null)
        {
            return Task.FromResult(_defaultResult with { Target = target });
        }
        
        return Task.FromResult(PingResult.Failed(target, "No result configured"));
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
}
