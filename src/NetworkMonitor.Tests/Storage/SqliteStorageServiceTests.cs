using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Storage;
using Xunit;

namespace NetworkMonitor.Tests.Storage;

/// <summary>
/// Round-trip tests for the normalized SQLite store against a real (temporary)
/// database. These validate the schema, the save path, per-target rollups,
/// per-target historical aggregation, and the sync-state store.
///
/// Timestamps are fixed and bucket-aligned so bucketing is fully deterministic.
///
/// Periodic retention pruning is DISABLED here (PruneEveryNSaves = 0). The
/// fixtures deliberately timestamp their cycles at <see cref="Base"/>
/// (2026-01-01), which is far outside the default 30-day retention window
/// relative to the wall clock. With pruning enabled, a prune firing between
/// saves would delete the freshly written rows and make these tests flaky
/// (e.g. yielding 3 or 0 rollup rows instead of 6). Disabling it keeps every
/// test in this class independent of the current date.
/// </summary>
public sealed class SqliteStorageServiceTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const long BucketMs = 60L * 60L * 1000L;

    private readonly string _dir;

    public SqliteStorageServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nm-storage-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        // Release pooled SQLite handles so the temp files can be removed.
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; a locked file must not fail the test run.
        }
    }

    private SqliteStorageService CreateStorage()
    {
        var options = new StorageOptions
        {
            DataDirectoryOverride = _dir,
            DatabaseFileName = "test.db",
            RetentionDays = 30,
            // Deterministic tests: never sweep fixture data that is intentionally
            // dated in the past. 0 disables the periodic retention prune.
            PruneEveryNSaves = 0
        };
        return new SqliteStorageService(Options.Create(options), NullLogger<SqliteStorageService>.Instance);
    }

    private static NetworkStatus MakeStatus(DateTimeOffset ts, long routerMs, long internetMs, long customMs)
    {
        var dns = DnsResult.Succeeded("host.example", ["203.0.113.7"], 8);
        var results = new List<TargetCheckResult>
        {
            new(new MonitorTarget("Router", "192.168.1.1", TargetCategory.Router),
                PingResult.Succeeded("192.168.1.1", routerMs), null, null, 0, ts, routerMs, routerMs, 0),
            new(new MonitorTarget("Internet", "8.8.8.8", TargetCategory.PublicDns),
                PingResult.Succeeded("8.8.8.8", internetMs), null, null, 0, ts, internetMs, internetMs, 0),
            new(new MonitorTarget("Svc", "host.example", TargetCategory.Custom),
                PingResult.Succeeded("host.example", customMs), null, dns, 0, ts, customMs, customMs, 1, "203.0.113.7"),
        };

        return new NetworkStatus(
            NetworkHealth.Good, results[0].PingResult, results[1].PingResult, ts, "test", results);
    }

    [Fact]
    public async Task SaveStatus_ThenGetRollups_AggregatesPerTargetPerBucket()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var storage = CreateStorage();

        // Two cycles in bucket 0, one in bucket 1.
        await storage.SaveStatusAsync(MakeStatus(Base.AddMinutes(10), 5, 10, 20), ct);
        await storage.SaveStatusAsync(MakeStatus(Base.AddMinutes(20), 6, 12, 22), ct);
        await storage.SaveStatusAsync(MakeStatus(Base.AddMinutes(70), 7, 14, 24), ct);

        var from = Base.ToUnixTimeMilliseconds();
        var to = Base.AddHours(3).ToUnixTimeMilliseconds();

        var rollups = await storage.GetRollupsAsync(from, to, 60, 100, ct);

        // 2 buckets × 3 targets = 6 rollup rows.
        Assert.Equal(6, rollups.Count);

        // Ordered by bucket then target id (Router, Internet, Svc were inserted
        // in that order), so the first row is bucket 0 / Router with 2 samples.
        var routerBucket0 = rollups[0];
        Assert.Equal("Router", routerBucket0.TargetName);
        Assert.Equal(2, routerBucket0.Samples);
        Assert.Equal(2, routerBucket0.Ok);
        Assert.Equal(5, routerBucket0.MinRttMs);
        Assert.Equal(6, routerBucket0.MaxRttMs);
        Assert.NotNull(routerBucket0.AvgRttMs);
        Assert.Equal(5.5, routerBucket0.AvgRttMs!.Value, 3);
        Assert.Equal(0, routerBucket0.AvgLossPct);

        // The custom target carried DNS timing; it should survive as a rollup.
        var svcBucket0 = rollups[2];
        Assert.Equal("Svc", svcBucket0.TargetName);
        Assert.NotNull(svcBucket0.AvgDnsMs);
        Assert.Equal(8, svcBucket0.AvgDnsMs!.Value, 3);
    }

    [Fact]
    public async Task GetRollups_UpperBoundIsExclusive_HoldsBackTheOpenBucket()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var storage = CreateStorage();

        var ts = Base.AddMinutes(30);
        await storage.SaveStatusAsync(MakeStatus(ts, 5, 10, 20), ct);

        var tsMs = ts.ToUnixTimeMilliseconds();
        var from = Base.ToUnixTimeMilliseconds();

        // Exclusive upper bound exactly at the sample's time excludes it.
        var excluded = await storage.GetRollupsAsync(from, tsMs, 60, 100, ct);
        Assert.Empty(excluded);

        // One millisecond later includes it.
        var included = await storage.GetRollupsAsync(from, tsMs + 1, 60, 100, ct);
        Assert.NotEmpty(included);
    }

    [Fact]
    public async Task GetHistoricalData_FilteredByTarget_AggregatesInDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var storage = CreateStorage();

        await storage.SaveStatusAsync(MakeStatus(Base.AddMinutes(10), 5, 10, 20), ct);
        await storage.SaveStatusAsync(MakeStatus(Base.AddMinutes(20), 6, 12, 22), ct);
        await storage.SaveStatusAsync(MakeStatus(Base.AddMinutes(70), 7, 14, 24), ct);

        var history = await storage.GetHistoricalDataAsync(
            Base, Base.AddHours(3), TimeGranularity.Hour, "192.168.1.1", ct);

        // Two hourly buckets for the router only.
        Assert.Equal(2, history.Count);
        Assert.Equal(5.5, history[0].AverageLatencyMs, 3); // (5 + 6) / 2
        Assert.Equal(7, history[1].AverageLatencyMs, 3);
        Assert.Equal(0, history[0].PacketLossPercent, 3);
    }

    [Fact]
    public async Task GetRecentPings_ReturnsMostRecentFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var storage = CreateStorage();

        await storage.SaveStatusAsync(MakeStatus(Base.AddMinutes(10), 5, 10, 20), ct);
        await storage.SaveStatusAsync(MakeStatus(Base.AddMinutes(20), 6, 12, 22), ct);

        var recent = await storage.GetRecentPingsAsync(3, ct);

        Assert.NotEmpty(recent);
        // Most recent cycle (minute 20) should appear before the earlier one.
        var firstTs = recent[0].Timestamp;
        Assert.True(firstTs >= Base.AddMinutes(20));
    }

    [Fact]
    public async Task SyncState_RoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var storage = CreateStorage();

        Assert.Null(await storage.GetSyncStateAsync("k", ct));

        await storage.SetSyncStateAsync("k", "42", ct);
        Assert.Equal("42", await storage.GetSyncStateAsync("k", ct));

        await storage.SetSyncStateAsync("k", "99", ct);
        Assert.Equal("99", await storage.GetSyncStateAsync("k", ct));
    }

    [Fact]
    public async Task SaveStatus_WithoutTargetResults_PersistsRouterAndInternet()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var storage = CreateStorage();

        var ts = Base.AddMinutes(10);
        var status = new NetworkStatus(
            NetworkHealth.Good,
            PingResult.Succeeded("192.168.1.1", 4),
            PingResult.Succeeded("8.8.8.8", 9),
            ts, "no-breakdown", TargetResults: null);

        await storage.SaveStatusAsync(status, ct);

        var rollups = await storage.GetRollupsAsync(
            Base.ToUnixTimeMilliseconds(), Base.AddHours(1).ToUnixTimeMilliseconds(), 60, 100, ct);

        // Router + Internet fallback rows were persisted.
        Assert.Equal(2, rollups.Count);
        Assert.Contains(rollups, r => r.TargetName == "Router");
        Assert.Contains(rollups, r => r.TargetName == "Internet");
    }
}
