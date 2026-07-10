using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Storage;

namespace NetworkMonitor.Tests.Fakes;

/// <summary>
/// In-memory storage for testing. Stores data in memory without any I/O.
///
/// It mirrors the real service closely enough for the remote-sync tests:
/// each saved status contributes one stored ping row per target result (or the
/// router/internet results when there is no per-target breakdown), each with a
/// monotonically increasing id. It also provides the small key/value sync-state
/// store used to track the replication checkpoint.
/// </summary>
internal sealed class FakeStorageService : IStorageService
{
    private readonly List<NetworkStatus> _statuses = new();
    private readonly List<PingResult> _pings = new();
    private readonly List<StoredPingResult> _rows = new();
    private readonly Dictionary<string, string> _syncState = new(StringComparer.Ordinal);
    private long _nextId;

    public IReadOnlyList<NetworkStatus> SavedStatuses => _statuses;
    public IReadOnlyList<PingResult> SavedPings => _pings;
    public IReadOnlyList<StoredPingResult> StoredRows => _rows;
    public IReadOnlyDictionary<string, string> SyncState => _syncState;

    public Task SaveStatusAsync(NetworkStatus status, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);

        _statuses.Add(status);

        if (status.RouterResult != null)
        {
            _pings.Add(status.RouterResult);
        }

        if (status.InternetResult != null)
        {
            _pings.Add(status.InternetResult);
        }

        if (status.TargetResults is { Count: > 0 })
        {
            foreach (var result in status.TargetResults)
            {
                var ping = result.PingResult ?? result.PingResultV6;
                AddRow(
                    result.Target.Address,
                    result.Target.Name,
                    MapCategory(result.Target.Category),
                    ping?.Success == true,
                    ping?.Success == true ? ping?.RoundtripTimeMs : null,
                    result.PacketLossPercent,
                    result.Timestamp,
                    ping?.ErrorMessage);
            }
        }
        else
        {
            if (status.RouterResult != null)
            {
                AddRow(status.RouterResult.Target, "Router", "router",
                    status.RouterResult.Success, status.RouterResult.RoundtripTimeMs,
                    0, status.RouterResult.Timestamp, status.RouterResult.ErrorMessage);
            }

            if (status.InternetResult != null)
            {
                AddRow(status.InternetResult.Target, "Internet", "internet",
                    status.InternetResult.Success, status.InternetResult.RoundtripTimeMs,
                    0, status.InternetResult.Timestamp, status.InternetResult.ErrorMessage);
            }
        }

        return Task.CompletedTask;
    }

    private void AddRow(
        string target,
        string? targetName,
        string targetType,
        bool success,
        long? roundtripMs,
        double packetLoss,
        DateTimeOffset timestamp,
        string? errorMessage)
    {
        _rows.Add(new StoredPingResult(
            ++_nextId, target, targetName, targetType, success, roundtripMs, packetLoss, timestamp, errorMessage));
    }

    private static string MapCategory(TargetCategory category) => category switch
    {
        TargetCategory.Router => "router",
        TargetCategory.PublicDns => "internet",
        TargetCategory.Service => "service",
        TargetCategory.Custom => "custom",
        _ => "custom"
    };

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

    public Task<IReadOnlyList<StoredPingResult>> GetPingResultsAfterAsync(
        long afterId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var page = _rows
            .Where(r => r.Id > afterId)
            .OrderBy(r => r.Id)
            .Take(limit)
            .ToList();

        return Task.FromResult<IReadOnlyList<StoredPingResult>>(page);
    }

    public Task<string?> GetSyncStateAsync(string key, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_syncState.TryGetValue(key, out var value) ? value : null);
    }

    public Task SetSyncStateAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        _syncState[key] = value;
        return Task.CompletedTask;
    }

    public void Clear()
    {
        _statuses.Clear();
        _pings.Clear();
        _rows.Clear();
        _syncState.Clear();
        _nextId = 0;
    }
}
