using Microsoft.Extensions.Logging.Abstractions;
using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Sync;
using NetworkMonitor.Tests.Fakes;
using Xunit;
using static NetworkMonitor.Tests.Fakes.Rollups;

namespace NetworkMonitor.Tests.Sync;

public sealed class RollupSyncerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(10 * HourMs + 1_800_000);

    private readonly FakeRollupStore _store = new();
    private readonly RemoteSyncOptions _options = new() { Url = "https://db", AuthToken = "t" };
    private readonly FakeRollupSink _sink;
    private readonly RollupSyncer _syncer;

    public RollupSyncerTests()
    {
        _sink = new FakeRollupSink(_options);
        _syncer = new RollupSyncer(_sink, _store, new ManualTimeProvider(Now), NullLogger.Instance);
    }

    [Fact]
    public async Task SyncOnce_PushesOnlyClosedBuckets_AndAdvancesCheckpoint()
    {
        _store.Rollups.AddRange([Make(8 * HourMs, "a"), Make(9 * HourMs, "a"), Make(10 * HourMs, "a")]);

        var pushed = await _syncer.SyncOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, pushed);
        Assert.Equal((10 * HourMs).ToString(System.Globalization.CultureInfo.InvariantCulture), _store.State["fake_checkpoint"]);
    }

    [Fact]
    public async Task SyncOnce_SecondPass_PushesNothingNew()
    {
        _store.Rollups.Add(Make(9 * HourMs, "a"));
        await _syncer.SyncOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, await _syncer.SyncOnceAsync(TestContext.Current.CancellationToken));
        Assert.Single(_sink.Pushes);
    }

    [Fact]
    public async Task SyncOnce_RemoteFailure_LeavesCheckpoint_ThenRetries()
    {
        _store.Rollups.Add(Make(9 * HourMs, "a"));
        _sink.Succeed = false;

        Assert.Equal(0, await _syncer.SyncOnceAsync(TestContext.Current.CancellationToken));
        Assert.False(_store.State.ContainsKey("fake_checkpoint"));

        _sink.Succeed = true;
        Assert.Equal(1, await _syncer.SyncOnceAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SyncOnce_Disabled_DoesNothing()
    {
        _sink.IsEnabled = false;
        _store.Rollups.Add(Make(9 * HourMs, "a"));

        Assert.Equal(0, await _syncer.SyncOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, _sink.SchemaCalls);
    }

    [Fact]
    public async Task SyncOnce_EnsuresSchemaOnlyOnce()
    {
        await _syncer.SyncOnceAsync(TestContext.Current.CancellationToken);
        await _syncer.SyncOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, _sink.SchemaCalls);
    }

    [Fact]
    public async Task SyncOnce_NoRows_AdvancesCheckpointToOpenBucket()
    {
        Assert.Equal(0, await _syncer.SyncOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal((10 * HourMs).ToString(System.Globalization.CultureInfo.InvariantCulture), _store.State["fake_checkpoint"]);
    }

    [Fact]
    public async Task SyncOnce_Backlog_PagesOnBucketBoundaries()
    {
        _options.BatchSize = 3;
        foreach (var hour in new[] { 6, 7, 8 })
        {
            _store.Rollups.AddRange([Make(hour * HourMs, "a"), Make(hour * HourMs, "b")]);
        }

        var pushed = await _syncer.SyncOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(6, pushed);
        Assert.All(_sink.Pushes, batch => Assert.Single(batch.Select(r => r.BucketStartMs).Distinct()));
    }

    [Fact]
    public void CompleteBuckets_TruncatedPage_HoldsBackPartialLastBucket()
    {
        var page = new[] { Make(1 * HourMs, "a"), Make(2 * HourMs, "a"), Make(2 * HourMs, "b") };

        var (batch, next) = RollupSyncer.CompleteBuckets(page, truncated: true, HourMs);

        Assert.Single(batch);
        Assert.Equal(2 * HourMs, next);
    }
}
