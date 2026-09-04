using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Storage;

namespace NetworkMonitor.Tests.Fakes;

/// <summary>
/// In-memory storage for testing. Stores cycles and per-target measurements in
/// memory without any I/O, and computes per-target, per-bucket rollups the same
/// way the real SQLite service does, so the remote-sync tests can drive
/// rollup replication with no database.
/// </summary>
internal sealed class FakeStorageService : IStorageService
{
    private sealed record CheckRow(
        long TsMs,
        string Name,
        string Address,
        string Category,
        bool Success,
        long? RttMs,
        long? MinMs,
        long? MaxMs,
        long? JitterMs,
        long? DnsMs,
        int LossPct);

    private readonly List<NetworkStatus> _statuses = new();
    private readonly List<CheckRow> _checks = new();
    private readonly Dictionary<string, string> _syncState = new(StringComparer.Ordinal);

    public IReadOnlyList<NetworkStatus> SavedStatuses => _statuses;
    public IReadOnlyDictionary<string, string> SyncState => _syncState;

    public Task SaveStatusAsync(NetworkStatus status, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);

        _statuses.Add(status);
        var tsMs = status.Timestamp.ToUnixTimeMilliseconds();

        if (status.TargetResults is { Count: > 0 })
        {
            foreach (var result in status.TargetResults)
            {
                var ping = result.PingResult ?? result.PingResultV6;
                var success = ping?.Success == true;
                _checks.Add(new CheckRow(
                    tsMs,
                    result.Target.Name,
                    result.Target.Address,
                    MapCategory(result.Target.Category),
                    success,
                    success ? ping?.RoundtripTimeMs : null,
                    success ? result.MinLatencyMs : null,
                    success ? result.MaxLatencyMs : null,
                    success ? result.JitterMs : null,
                    result.DnsResult?.ResolutionTimeMs,
                    ClampLoss(result.PacketLossPercent)));
            }
        }
        else
        {
            if (status.RouterResult != null)
            {
                AddSimple(tsMs, "Router", status.RouterResult, "router");
            }

            if (status.InternetResult != null)
            {
                AddSimple(tsMs, "Internet", status.InternetResult, "internet");
            }
        }

        return Task.CompletedTask;
    }

    private void AddSimple(long tsMs, string name, PingResult ping, string category)
    {
        _checks.Add(new CheckRow(
            tsMs, name, ping.Target, category,
            ping.Success, ping.Success ? ping.RoundtripTimeMs : null,
            null, null, null, null, 0));
    }

    private static int ClampLoss(double pct) =>
        Math.Clamp((int)Math.Round(pct, MidpointRounding.AwayFromZero), 0, 100);

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
        string? targetAddress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<HistoricalData>>(Array.Empty<HistoricalData>());
    }

    public Task<IReadOnlyList<PingResult>> GetRecentPingsAsync(
        int count,
        CancellationToken cancellationToken = default)
    {
        var pings = _checks
            .OrderByDescending(c => c.TsMs)
            .Take(count)
            .Select(c => new PingResult(
                c.Address, c.Success, c.RttMs,
                DateTimeOffset.FromUnixTimeMilliseconds(c.TsMs), null))
            .ToList();

        return Task.FromResult<IReadOnlyList<PingResult>>(pings);
    }

    public Task<IReadOnlyList<CheckRollup>> GetRollupsAsync(
        long fromBucketStartMsInclusive,
        long toExclusiveMs,
        int bucketMinutes,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var bucketMs = Math.Max(1, bucketMinutes) * 60_000L;

        var rollups = _checks
            .Where(c => c.TsMs >= fromBucketStartMsInclusive && c.TsMs < toExclusiveMs)
            .GroupBy(c => (Bucket: c.TsMs - (c.TsMs % bucketMs), c.Address))
            .OrderBy(g => g.Key.Bucket)
            .ThenBy(g => g.Key.Address, StringComparer.Ordinal)
            .Take(limit)
            .Select(g =>
            {
                var list = g.ToList();
                var first = list[0];
                var succ = list.Where(x => x.Success).ToList();

                var rtts = succ.Where(x => x.RttMs.HasValue).Select(x => x.RttMs!.Value).ToList();
                var mins = succ.Where(x => x.MinMs.HasValue).Select(x => x.MinMs!.Value).ToList();
                var maxs = succ.Where(x => x.MaxMs.HasValue).Select(x => x.MaxMs!.Value).ToList();
                var jitters = list.Where(x => x.JitterMs.HasValue).Select(x => (double)x.JitterMs!.Value).ToList();
                var dnss = list.Where(x => x.DnsMs.HasValue).Select(x => (double)x.DnsMs!.Value).ToList();

                return new CheckRollup(
                    BucketStartMs: g.Key.Bucket,
                    BucketMinutes: Math.Max(1, bucketMinutes),
                    TargetName: first.Name,
                    TargetAddress: first.Address,
                    TargetCategory: first.Category,
                    Samples: list.Count,
                    Ok: succ.Count,
                    AvgRttMs: rtts.Count > 0 ? rtts.Average() : null,
                    MinRttMs: mins.Count > 0 ? mins.Min() : null,
                    MaxRttMs: maxs.Count > 0 ? maxs.Max() : null,
                    AvgJitterMs: jitters.Count > 0 ? jitters.Average() : null,
                    AvgDnsMs: dnss.Count > 0 ? dnss.Average() : null,
                    AvgLossPct: list.Count > 0 ? list.Average(x => (double)x.LossPct) : 0);
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<CheckRollup>>(rollups);
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
        _checks.Clear();
        _syncState.Clear();
    }
}
