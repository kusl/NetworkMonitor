using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Storage;
using NetworkMonitor.Core.Sync.Turso;
using NetworkMonitor.Tests.Fakes;
using Xunit;

namespace NetworkMonitor.Tests.Storage;

public sealed class SqliteStoreTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-storage-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new(Base);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private SqliteStore CreateStore(int pruneEvery = 0) => new(
        Options.Create(new StorageOptions { DataDirectoryOverride = _dir, DatabaseFileName = "test.db", RetentionDays = 30, PruneEveryNSaves = pruneEvery }),
        _time,
        NullLogger<SqliteStore>.Instance);

    private static NetworkStatus MakeStatus(DateTimeOffset ts, long routerMs, long internetMs, long customMs)
    {
        var dns = DnsResult.Succeeded([IPAddress.Parse("203.0.113.7")], 8);
        return new NetworkStatus(NetworkHealth.Good, "test", ts,
        [
            new(MonitorTarget.Router("192.168.1.1"), true, routerMs, routerMs, routerMs, 0, 0),
            new(MonitorTarget.Internet("8.8.8.8"), true, internetMs, internetMs, internetMs, 0, 0),
            new(new MonitorTarget("Svc", "host.example", TargetCategory.Custom), true, customMs, customMs, customMs, 1, 0, dns, "203.0.113.7"),
        ]);
    }

    [Fact]
    public async Task Save_ThenGetRollups_AggregatesPerTargetPerBucket()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = CreateStore();
        await store.SaveAsync(MakeStatus(Base.AddMinutes(10), 5, 10, 20), ct);
        await store.SaveAsync(MakeStatus(Base.AddMinutes(20), 6, 12, 22), ct);
        await store.SaveAsync(MakeStatus(Base.AddMinutes(70), 7, 14, 24), ct);

        var rollups = await store.GetRollupsAsync(Base.ToUnixTimeMilliseconds(), Base.AddHours(3).ToUnixTimeMilliseconds(), 60, 100, ct);

        Assert.Equal(6, rollups.Count);
        var router = rollups[0];
        Assert.Equal(("Router", "router", 2, 2), (router.TargetName, router.TargetCategory, router.Samples, router.Ok));
        Assert.Equal(5, router.MinRttMs);
        Assert.Equal(6, router.MaxRttMs);
        Assert.Equal(5.5, router.AvgRttMs!.Value, 3);
        Assert.Equal("internet", rollups[1].TargetCategory);
        Assert.Equal(8, rollups[2].AvgDnsMs!.Value, 3);
    }

    [Fact]
    public async Task Save_FailedTarget_StoresErrorAndNullLatency()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = CreateStore();
        var failed = TargetCheckResult.Failed(MonitorTarget.Internet("8.8.8.8"), "TimedOut");
        await store.SaveAsync(new NetworkStatus(NetworkHealth.Offline, "down", Base.AddMinutes(1), [failed]), ct);

        var rollup = Assert.Single(await store.GetRollupsAsync(0, Base.AddHours(1).ToUnixTimeMilliseconds(), 60, 10, ct));
        Assert.Equal(0, rollup.Ok);
        Assert.Null(rollup.AvgRttMs);
        Assert.Equal(100, rollup.AvgLossPct, 3);

        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath}");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT error_message FROM check_results";
        Assert.Equal("TimedOut", await command.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task GetRollups_UpperBoundIsExclusive()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = CreateStore();
        var ts = Base.AddMinutes(30);
        await store.SaveAsync(MakeStatus(ts, 5, 10, 20), ct);
        var tsMs = ts.ToUnixTimeMilliseconds();

        Assert.Empty(await store.GetRollupsAsync(0, tsMs, 60, 100, ct));
        Assert.NotEmpty(await store.GetRollupsAsync(0, tsMs + 1, 60, 100, ct));
    }

    [Fact]
    public async Task SyncState_RoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = CreateStore();

        Assert.Null(await store.GetSyncStateAsync("k", ct));
        await store.SetSyncStateAsync("k", "42", ct);
        Assert.Equal("42", await store.GetSyncStateAsync("k", ct));
        await store.SetSyncStateAsync("k", "99", ct);
        Assert.Equal("99", await store.GetSyncStateAsync("k", ct));
    }

    [Fact]
    public async Task Prune_RemovesCyclesOlderThanRetention()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = CreateStore(pruneEvery: 2);
        await store.SaveAsync(MakeStatus(Base, 5, 10, 20), ct);
        _time.Now = Base.AddDays(31);
        await store.SaveAsync(MakeStatus(_time.Now, 5, 10, 20), ct);

        var rollups = await store.GetRollupsAsync(0, _time.Now.AddHours(1).ToUnixTimeMilliseconds(), 60, 100, ct);

        Assert.All(rollups, r => Assert.True(r.BucketStartMs >= Base.AddDays(1).ToUnixTimeMilliseconds()));
        Assert.Equal(3, rollups.Count);
    }

    [Fact]
    public async Task ExistingDatabase_IsReadByNewStore()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var first = CreateStore())
        {
            await first.SaveAsync(MakeStatus(Base.AddMinutes(5), 5, 10, 20), ct);
            await first.SetSyncStateAsync(TursoRollupSink.CheckpointKeyName, "123", ct);
        }

        using var second = CreateStore();
        Assert.Equal("123", await second.GetSyncStateAsync(TursoRollupSink.CheckpointKeyName, ct));
        Assert.Equal(3, (await second.GetRollupsAsync(0, Base.AddHours(1).ToUnixTimeMilliseconds(), 60, 100, ct)).Count);
    }
}
