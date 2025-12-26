using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Storage;

namespace NetworkMonitor.Tests.Fakes;

/// <summary>
/// In-memory storage for testing.
/// Stores data in memory without any I/O.
/// </summary>
public sealed class FakeStorageService : IStorageService
{
    private readonly List<NetworkStatus> _statuses = new();
    private readonly List<PingResult> _pings = new();
    
    public IReadOnlyList<NetworkStatus> SavedStatuses => _statuses;
    public IReadOnlyList<PingResult> SavedPings => _pings;
    
    public Task SaveStatusAsync(NetworkStatus status, CancellationToken cancellationToken = default)
    {
        _statuses.Add(status);
        
        if (status.RouterResult != null)
        {
            _pings.Add(status.RouterResult);
        }
        
        if (status.InternetResult != null)
        {
            _pings.Add(status.InternetResult);
        }
        
        return Task.CompletedTask;
    }
    
    public Task<IReadOnlyList<HistoricalData>> GetHistoricalDataAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        TimeGranularity granularity,
        CancellationToken cancellationToken = default)
    {
        // Simple implementation for testing
        return Task.FromResult<IReadOnlyList<HistoricalData>>(Array.Empty<HistoricalData>());
    }
    
    public Task<IReadOnlyList<PingResult>> GetRecentPingsAsync(
        int count,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<PingResult>>(
            _pings.TakeLast(count).Reverse().ToList());
    }
    
    public void Clear()
    {
        _statuses.Clear();
        _pings.Clear();
    }
}
