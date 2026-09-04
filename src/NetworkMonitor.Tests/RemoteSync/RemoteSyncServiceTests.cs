using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.RemoteSync;
using NetworkMonitor.Tests.Fakes;
using Xunit;

namespace NetworkMonitor.Tests.RemoteSync;

/// <summary>
/// Tests for RemoteSyncService.SyncOnceAsync, driven with in-memory fakes so no
/// network access is required.
///
/// The service replicates per-target, per-bucket ROLLUPS for fully-elapsed
/// buckets only, so tests seed measurements into buckets safely in the past
/// (a few hours ago) and expect one rollup row per (bucket, target).
/// </summary>
public sealed class RemoteSyncServiceTests
{
    private const string CheckpointKey = "remote_rollup_next_bucket_ms";
    private const long BucketMs = 60L * 60L * 1000L; // 60-minute buckets (the default)

    private readonly FakeStorageService _storage = new();
    private readonly FakeRemoteDatabaseClient _client = new();

    private static RemoteSyncOptions ConfiguredOptions() => new()
    {
        Url = "libsql://example.turso.io",
        AuthToken = "test-token",
        InitialDelaySeconds = 0,
        BucketMinutes = 60,
        BatchSize = 500,
        MaxRowsPerSync = 25000
    };

    private RemoteSyncService CreateService(RemoteSyncOptions options)
    {
        return new RemoteSyncService(
            _client,
            _storage,
            Options.Create(options),
            NullLogger<RemoteSyncService>.Instance);
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
        // Arrange - 2 closed buckets × 2 targets = 4 rollup rows.
        await SeedTwoClosedBucketsAsync();
        var service = CreateService(ConfiguredOptions());

        // Act
        var synced = await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(4, synced);
        Assert.Equal(4, _client.TotalInsertedRows);

        var checkpoint = await _storage.GetSyncStateAsync(CheckpointKey, TestContext.Current.CancellationToken);
        Assert.NotNull(checkpoint);
    }

    [Fact]
    public async Task SyncOnceAsync_SecondPass_PushesNothingNew()
    {
        // Arrange
        await SeedTwoClosedBucketsAsync();
        var service = CreateService(ConfiguredOptions());

        var first = await service.SyncOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(4, first);

        // Act - nothing new has closed since.
        var second = await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, second);
        Assert.Equal(4, _client.TotalInsertedRows);
    }

    [Fact]
    public async Task SyncOnceAsync_WhenRemoteFails_LeavesCheckpointUnchanged()
    {
        // Arrange
        await SeedTwoClosedBucketsAsync();
        _client.SucceedCalls = false;
        var service = CreateService(ConfiguredOptions());

        // Act
        var synced = await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        // Assert - nothing counted or recorded, checkpoint never written.
        Assert.Equal(0, synced);
        Assert.Empty(_client.ExecutedPipelines);

        var checkpoint = await _storage.GetSyncStateAsync(CheckpointKey, TestContext.Current.CancellationToken);
        Assert.Null(checkpoint);
    }

    [Fact]
    public async Task SyncOnceAsync_ThenSucceeds_RetriesSameRollups()
    {
        // Arrange - fail first (even ensuring the schema fails), then recover.
        await SeedTwoClosedBucketsAsync();
        _client.SucceedCalls = false;
        var service = CreateService(ConfiguredOptions());

        var failed = await service.SyncOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, failed);

        _client.SucceedCalls = true;

        // Act
        var recovered = await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        // Assert - the 4 previously-failed rollup rows are pushed now.
        Assert.Equal(4, recovered);
        Assert.Equal(4, _client.TotalInsertedRows);
    }

    [Fact]
    public async Task SyncOnceAsync_WhenNotConfigured_ReturnsZeroAndDoesNothing()
    {
        // Arrange - rows exist, but there is no URL/token.
        await SeedTwoClosedBucketsAsync();
        var options = new RemoteSyncOptions { Url = "", AuthToken = "" };
        var service = CreateService(options);

        // Act
        var synced = await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, synced);
        Assert.Equal(0, _client.CallCount);
    }

    [Fact]
    public async Task SyncOnceAsync_WhenClientNotConfigured_ReturnsZero()
    {
        // Arrange - options look configured, but the client reports otherwise
        // (e.g. the URL failed to parse into a valid endpoint).
        await SeedTwoClosedBucketsAsync();
        _client.IsConfigured = false;
        var service = CreateService(ConfiguredOptions());

        // Act
        var synced = await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, synced);
        Assert.Equal(0, _client.CallCount);
    }

    [Fact]
    public async Task SyncOnceAsync_WithNoRows_ReturnsZero()
    {
        // Arrange - configured, but nothing has been stored.
        var service = CreateService(ConfiguredOptions());

        // Act
        var synced = await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, synced);
        Assert.Equal(0, _client.TotalInsertedRows);
    }

    [Fact]
    public async Task SyncOnceAsync_EnsuresRemoteSchemaOnce()
    {
        // Arrange
        await SeedTwoClosedBucketsAsync();
        var service = CreateService(ConfiguredOptions());

        // Act
        await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        // Assert - the remote is never assumed to exist: a pipeline creates the
        // table and index with IF NOT EXISTS guards.
        var allStatements = _client.ExecutedPipelines.SelectMany(p => p).ToList();
        Assert.Contains(allStatements, s => s.Sql.Contains("CREATE TABLE IF NOT EXISTS", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(allStatements, s => s.Sql.Contains("CREATE INDEX IF NOT EXISTS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SyncOnceAsync_DoesNotResendSchema_OnSecondPass()
    {
        // Arrange
        await SeedTwoClosedBucketsAsync();
        var service = CreateService(ConfiguredOptions());
        await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        var createStatementsAfterFirst = _client.ExecutedPipelines
            .SelectMany(p => p)
            .Count(s => s.Sql.Contains("CREATE TABLE IF NOT EXISTS", StringComparison.OrdinalIgnoreCase));

        // Act - a second pass with nothing new should not re-issue DDL.
        await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        var createStatementsAfterSecond = _client.ExecutedPipelines
            .SelectMany(p => p)
            .Count(s => s.Sql.Contains("CREATE TABLE IF NOT EXISTS", StringComparison.OrdinalIgnoreCase));

        // Assert - schema was created exactly once, not on every run.
        Assert.Equal(1, createStatementsAfterFirst);
        Assert.Equal(createStatementsAfterFirst, createStatementsAfterSecond);
    }
}
