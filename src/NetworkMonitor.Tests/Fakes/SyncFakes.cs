using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Storage;
using NetworkMonitor.Core.Sync;
using NetworkMonitor.Core.Sync.Databricks;
using NetworkMonitor.Core.Sync.Turso;

namespace NetworkMonitor.Tests.Fakes;

internal sealed class FakeRollupStore : IRollupStore
{
    public List<CheckRollup> Rollups { get; } = [];

    public Dictionary<string, string> State { get; } = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<CheckRollup>> GetRollupsAsync(long fromBucketStartMsInclusive, long toExclusiveMs, int bucketMinutes, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CheckRollup>>(
        [
            .. Rollups
                .Where(r => r.BucketStartMs >= fromBucketStartMsInclusive && r.BucketStartMs < toExclusiveMs)
                .OrderBy(r => r.BucketStartMs)
                .ThenBy(r => r.TargetAddress, StringComparer.Ordinal)
                .Take(limit),
        ]);

    public Task<string?> GetSyncStateAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(State.GetValueOrDefault(key));

    public Task SetSyncStateAsync(string key, string value, CancellationToken cancellationToken)
    {
        State[key] = value;
        return Task.CompletedTask;
    }
}

internal sealed class FakeRollupSink(RollupSyncOptions options) : IRollupSink
{
    public string Name => "Fake";

    public string Destination => "fake";

    public string CheckpointKey => "fake_checkpoint";

    public RollupSyncOptions Options => options;

    public bool IsEnabled { get; set; } = true;

    public bool Succeed { get; set; } = true;

    public int SchemaCalls { get; private set; }

    public List<IReadOnlyList<CheckRollup>> Pushes { get; } = [];

    public Task<bool> EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        SchemaCalls++;
        return Task.FromResult(Succeed);
    }

    public Task<bool> PushAsync(IReadOnlyList<CheckRollup> rows, long syncedAtMs, CancellationToken cancellationToken)
    {
        if (Succeed)
        {
            Pushes.Add(rows);
        }

        return Task.FromResult(Succeed);
    }
}

internal sealed class FakeHranaClient : IHranaClient
{
    public bool IsConfigured { get; set; } = true;

    public List<IReadOnlyList<HranaStatement>> Batches { get; } = [];

    public Task<bool> ExecuteAsync(IReadOnlyList<HranaStatement> statements, CancellationToken cancellationToken)
    {
        Batches.Add(statements);
        return Task.FromResult(true);
    }
}

internal sealed class FakeDatabricksClient : IDatabricksClient
{
    public bool IsConfigured { get; set; } = true;

    public List<IReadOnlyList<DatabricksStatement>> Batches { get; } = [];

    public Task<bool> ExecuteAsync(IReadOnlyList<DatabricksStatement> statements, CancellationToken cancellationToken)
    {
        Batches.Add(statements);
        return Task.FromResult(true);
    }
}

internal static class Rollups
{
    public const long HourMs = 3_600_000;

    public static CheckRollup Make(long bucketStartMs, string address, double? avgRtt = 10) =>
        new(bucketStartMs, 60, address, address, "custom", 12, 11, avgRtt, 5, 20, null, 3.5, 8.3);
}
