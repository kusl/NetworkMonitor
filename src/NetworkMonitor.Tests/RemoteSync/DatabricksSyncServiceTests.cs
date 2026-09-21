using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.RemoteSync;
using NetworkMonitor.Tests.Fakes;
using Xunit;

namespace NetworkMonitor.Tests.RemoteSync;

/// <summary>
/// Tests for DatabricksSyncService.SyncOnceAsync, driven with in-memory fakes so
/// no network access is required. The service is the Databricks counterpart of
/// RemoteSyncService and behaves identically at the sync-loop level: it
/// replicates per-target, per-bucket ROLLUPS for fully-elapsed buckets only, so
/// tests seed measurements into past buckets and expect one rollup row per
/// (bucket, target). It uses its OWN checkpoint key, independent of the Turso
/// sink.
/// </summary>
public sealed class DatabricksSyncServiceTests
{
    private const string CheckpointKey = "databricks_rollup_next_bucket_ms";
    private const long BucketMs = 60L * 60L * 1000L; // 60-minute buckets (the default)

    private readonly FakeStorageService _storage = new();
    private readonly FakeDatabricksClient _client = new();

    private static DatabricksSyncOptions ConfiguredOptions() => new()
    {
        WorkspaceUrl = "https://dbc-abc123.cloud.databricks.com",
        WarehouseId = "abc123warehouse",
        Catalog = "workspace",
        Schema = "default",
        TableName = "network_monitor_rollups",
        Token = "test-pat",
        InitialDelaySeconds = 0,
        BucketMinutes = 60,
        BatchSize = 200,
        MaxRowsPerSync = 25000
    };

    private DatabricksSyncService CreateService(DatabricksSyncOptions options)
    {
        return new DatabricksSyncService(
            _client,
            _storage,
            Options.Create(options),
            NullLogger<DatabricksSyncService>.Instance);
    }

    private static long CurrentBucketStart()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return nowMs - (nowMs % BucketMs);
    }

    private async Task SeedStatusAsync(long tsMs, long routerLatency, long internetLatency)
    {
        var ts = DateTimeOffset.FromUnixTimeMilliseconds(tsMs);
        var results = new List<TargetCheckResult>
        {
            new(
                new MonitorTarget("Router", "192.168.1.1", TargetCategory.Router),
                PingResult.Succeeded("192.168.1.1", routerLatency),
                null, null, 0, ts, routerLatency, routerLatency, 0),
            new(
                new MonitorTarget("Internet", "8.8.8.8", TargetCategory.PublicDns),
                PingResult.Succeeded("8.8.8.8", internetLatency),
                null, null, 0, ts, internetLatency, internetLatency, 0),
        };

        var status = new NetworkStatus(
            NetworkHealth.Good, results[0].PingResult, results[1].PingResult,
            ts, "seed", results);

        await _storage.SaveStatusAsync(status, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Seeds 2 cycles in one past bucket and 1 in another, giving 2 buckets ×
    /// 2 targets = 4 rollup rows once both buckets are closed.
    /// </summary>
    private async Task SeedTwoClosedBucketsAsync()
    {
        var current = CurrentBucketStart();
        var bucketA = current - (3 * BucketMs); // 3 buckets ago
        var bucketB = current - (2 * BucketMs); // 2 buckets ago

        await SeedStatusAsync(bucketA + (10 * 60_000L), 5, 10);
        await SeedStatusAsync(bucketA + (20 * 60_000L), 6, 11);
        await SeedStatusAsync(bucketB + (15 * 60_000L), 7, 12);
    }

    [Fact]
    public async Task SyncOnceAsync_PushesClosedBucketRollups_AndAdvancesCheckpoint()
    {
        await SeedTwoClosedBucketsAsync();
        var service = CreateService(ConfiguredOptions());

        var synced = await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, synced);
        Assert.Equal(4, _client.TotalMergedRows);

        var checkpoint = await _storage.GetSyncStateAsync(CheckpointKey, TestContext.Current.CancellationToken);
        Assert.NotNull(checkpoint);
    }

    [Fact]
    public async Task SyncOnceAsync_SecondPass_PushesNothingNew()
    {
        await SeedTwoClosedBucketsAsync();
        var service = CreateService(ConfiguredOptions());

        var first = await service.SyncOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(4, first);

        var second = await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, second);
        Assert.Equal(4, _client.TotalMergedRows);
    }

    [Fact]
    public async Task SyncOnceAsync_WhenRemoteFails_LeavesCheckpointUnchanged()
    {
        await SeedTwoClosedBucketsAsync();
        _client.SucceedCalls = false;
        var service = CreateService(ConfiguredOptions());

        var synced = await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, synced);
        Assert.Empty(_client.ExecutedBatches);

        var checkpoint = await _storage.GetSyncStateAsync(CheckpointKey, TestContext.Current.CancellationToken);
        Assert.Null(checkpoint);
    }

    [Fact]
    public async Task SyncOnceAsync_ThenSucceeds_RetriesSameRollups()
    {
        // Fail first (even ensuring the schema fails), then recover.
        await SeedTwoClosedBucketsAsync();
        _client.SucceedCalls = false;
        var service = CreateService(ConfiguredOptions());

        var failed = await service.SyncOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, failed);

        _client.SucceedCalls = true;

        var recovered = await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, recovered);
        Assert.Equal(4, _client.TotalMergedRows);
    }

    [Fact]
    public async Task SyncOnceAsync_WhenNotConfigured_ReturnsZeroAndDoesNothing()
    {
        await SeedTwoClosedBucketsAsync();
        var options = new DatabricksSyncOptions { WorkspaceUrl = "", WarehouseId = "", Token = "" };
        var service = CreateService(options);

        var synced = await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, synced);
        Assert.Equal(0, _client.CallCount);
    }

    [Fact]
    public async Task SyncOnceAsync_WhenClientNotConfigured_ReturnsZero()
    {
        // Options look configured, but the client reports otherwise (e.g. the URL
        // failed to parse into a valid endpoint).
        await SeedTwoClosedBucketsAsync();
        _client.IsConfigured = false;
        var service = CreateService(ConfiguredOptions());

        var synced = await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, synced);
        Assert.Equal(0, _client.CallCount);
    }

    [Fact]
    public async Task SyncOnceAsync_WithNoRows_ReturnsZero()
    {
        var service = CreateService(ConfiguredOptions());

        var synced = await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, synced);
        Assert.Equal(0, _client.TotalMergedRows);
    }

    [Fact]
    public async Task SyncOnceAsync_EnsuresRemoteSchemaOnce()
    {
        await SeedTwoClosedBucketsAsync();
        var service = CreateService(ConfiguredOptions());

        await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        var allStatements = _client.ExecutedBatches.SelectMany(b => b).ToList();
        Assert.Contains(allStatements, s => s.Sql.Contains("CREATE TABLE IF NOT EXISTS", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(allStatements, s => s.Sql.Contains("USING DELTA", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SyncOnceAsync_DoesNotResendSchema_OnSecondPass()
    {
        await SeedTwoClosedBucketsAsync();
        var service = CreateService(ConfiguredOptions());
        await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        var createAfterFirst = _client.ExecutedBatches
            .SelectMany(b => b)
            .Count(s => s.Sql.Contains("CREATE TABLE IF NOT EXISTS", StringComparison.OrdinalIgnoreCase));

        await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        var createAfterSecond = _client.ExecutedBatches
            .SelectMany(b => b)
            .Count(s => s.Sql.Contains("CREATE TABLE IF NOT EXISTS", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(1, createAfterFirst);
        Assert.Equal(createAfterFirst, createAfterSecond);
    }

    [Fact]
    public async Task SyncOnceAsync_MergeUsesNamedParameters_AndIsIdempotentUpsert()
    {
        await SeedTwoClosedBucketsAsync();
        var service = CreateService(ConfiguredOptions());

        await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        var merge = _client.ExecutedBatches
            .SelectMany(b => b)
            .First(s => s.Sql.StartsWith("MERGE", StringComparison.OrdinalIgnoreCase));

        // Upsert semantics on the composite key.
        Assert.Contains("WHEN MATCHED THEN UPDATE SET *", merge.Sql, StringComparison.Ordinal);
        Assert.Contains("WHEN NOT MATCHED THEN INSERT *", merge.Sql, StringComparison.Ordinal);
        Assert.Contains("t.target_address = s.target_address", merge.Sql, StringComparison.Ordinal);

        // Every parameter is named and referenced by a :marker in the SQL, and
        // every marker has a matching parameter definition.
        Assert.NotEmpty(merge.Parameters);
        foreach (var p in merge.Parameters)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.Contains(":" + p.Name, merge.Sql, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(p.Type));
        }

        // 15 columns per row, and the row count divides evenly.
        Assert.Equal(0, merge.Parameters.Count % 15);
    }

    [Fact]
    public void TryBuildWorkspaceBase_NormalizesSchemelessHostToHttps()
    {
        var ok = DatabricksSqlClient.TryBuildWorkspaceBase("dbc-abc123.cloud.databricks.com", out var baseUri);

        Assert.True(ok);
        Assert.NotNull(baseUri);
        Assert.Equal("https", baseUri!.Scheme);
        Assert.Equal("/", baseUri.AbsolutePath);
    }

    [Fact]
    public void TryBuildWorkspaceBase_RejectsEmptyOrGarbage()
    {
        Assert.False(DatabricksSqlClient.TryBuildWorkspaceBase("", out _));
        Assert.False(DatabricksSqlClient.TryBuildWorkspaceBase("   ", out _));
        Assert.False(DatabricksSqlClient.TryBuildWorkspaceBase("ftp://nope", out _));
    }

    [Fact]
    public void SanitizeIdentifier_StripsUnsafeCharsAndFallsBack()
    {
        Assert.Equal("my_table", DatabricksSqlClient.SanitizeIdentifier("my-table!", "fallback"));
        Assert.Equal("fallback", DatabricksSqlClient.SanitizeIdentifier("", "fallback"));
        Assert.Equal("fallback", DatabricksSqlClient.SanitizeIdentifier("123", "fallback"));
    }
}
