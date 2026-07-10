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
/// </summary>
public sealed class RemoteSyncServiceTests
{
    private const string CheckpointKey = "remote_last_synced_id";

    private readonly FakeStorageService _storage = new();
    private readonly FakeRemoteDatabaseClient _client = new();

    private static RemoteSyncOptions ConfiguredOptions() => new()
    {
        Url = "libsql://example.turso.io",
        AuthToken = "test-token",
        InitialDelaySeconds = 0,
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

    private async Task SeedStatusAsync(long routerLatency, long internetLatency)
    {
        var results = new List<TargetCheckResult>
        {
            new(
                new MonitorTarget("Router", "192.168.1.1", TargetCategory.Router),
                PingResult.Succeeded("192.168.1.1", routerLatency),
                null, null, 0, DateTimeOffset.UtcNow),
            new(
                new MonitorTarget("Internet", "8.8.8.8", TargetCategory.PublicDns),
                PingResult.Succeeded("8.8.8.8", internetLatency),
                null, null, 0, DateTimeOffset.UtcNow),
        };

        var status = new NetworkStatus(
            NetworkHealth.Good, results[0].PingResult, results[1].PingResult,
            DateTimeOffset.UtcNow, "seed", results);

        await _storage.SaveStatusAsync(status, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SyncOnceAsync_PushesRows_AndAdvancesCheckpoint()
    {
        // Arrange - 3 statuses × 2 targets = 6 rows.
        await SeedStatusAsync(5, 10);
        await SeedStatusAsync(6, 11);
        await SeedStatusAsync(7, 12);

        var service = CreateService(ConfiguredOptions());

        // Act
        var synced = await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(6, synced);
        Assert.Equal(6, _client.TotalInsertedRows);

        var checkpoint = await _storage.GetSyncStateAsync(CheckpointKey, TestContext.Current.CancellationToken);
        Assert.Equal("6", checkpoint);
    }

    [Fact]
    public async Task SyncOnceAsync_SecondPass_OnlyPushesNewRows()
    {
        // Arrange - two statuses (4 rows), sync, then one more status (2 rows).
        await SeedStatusAsync(5, 10);
        await SeedStatusAsync(6, 11);

        var service = CreateService(ConfiguredOptions());

        var first = await service.SyncOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(4, first);

        await SeedStatusAsync(7, 12);

        // Act
        var second = await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        // Assert - only the 2 new rows go over the wire.
        Assert.Equal(2, second);

        var checkpoint = await _storage.GetSyncStateAsync(CheckpointKey, TestContext.Current.CancellationToken);
        Assert.Equal("6", checkpoint);
    }

    [Fact]
    public async Task SyncOnceAsync_WhenRemoteFails_LeavesCheckpointUnchanged()
    {
        // Arrange
        await SeedStatusAsync(5, 10);
        _client.SucceedCalls = false;

        var service = CreateService(ConfiguredOptions());

        // Act
        var synced = await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        // Assert - nothing counted, nothing recorded, checkpoint never written.
        Assert.Equal(0, synced);
        Assert.Empty(_client.ExecutedPipelines);

        var checkpoint = await _storage.GetSyncStateAsync(CheckpointKey, TestContext.Current.CancellationToken);
        Assert.Null(checkpoint);
    }

    [Fact]
    public async Task SyncOnceAsync_ThenSucceeds_RetriesSameRows()
    {
        // Arrange - fail first, then recover; the same rows must be retried.
        await SeedStatusAsync(5, 10);
        _client.SucceedCalls = false;

        var service = CreateService(ConfiguredOptions());

        var failed = await service.SyncOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, failed);

        _client.SucceedCalls = true;

        // Act
        var recovered = await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        // Assert - the 2 previously-failed rows are pushed now.
        Assert.Equal(2, recovered);
        Assert.Equal(2, _client.TotalInsertedRows);
    }

    [Fact]
    public async Task SyncOnceAsync_WhenNotConfigured_ReturnsZeroAndDoesNothing()
    {
        // Arrange - rows exist, but there is no URL/token.
        await SeedStatusAsync(5, 10);
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
        await SeedStatusAsync(5, 10);
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
    }

    [Fact]
    public async Task SyncOnceAsync_EmitsCreateTableStatements()
    {
        // Arrange
        await SeedStatusAsync(5, 10);
        var service = CreateService(ConfiguredOptions());

        // Act
        await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        // Assert - the remote is never assumed to exist: every pipeline leads
        // with CREATE TABLE / CREATE INDEX guards.
        var pipeline = Assert.Single(_client.ExecutedPipelines);
        Assert.Contains(pipeline, s => s.Sql.Contains("CREATE TABLE IF NOT EXISTS", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pipeline, s => s.Sql.Contains("CREATE INDEX IF NOT EXISTS", StringComparison.OrdinalIgnoreCase));
    }
}
