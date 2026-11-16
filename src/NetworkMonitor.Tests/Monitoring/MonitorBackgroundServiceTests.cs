using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Display;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Monitoring;
using NetworkMonitor.Core.Storage;
using Xunit;

namespace NetworkMonitor.Tests.Monitoring;

public sealed class MonitorBackgroundServiceTests
{
    private sealed class StubMonitor(Func<NetworkStatus> next) : INetworkMonitor
    {
        public Task<NetworkStatus> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(next());
    }

    private sealed class RecordingStore : IStatusStore
    {
        public List<NetworkStatus> Saved { get; } = [];

        public Task SaveAsync(NetworkStatus status, CancellationToken cancellationToken)
        {
            Saved.Add(status);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingDisplay : IStatusDisplay
    {
        public void Update(NetworkStatus status) => throw new IOException("console gone");

        public void Clear()
        {
        }
    }

    private static MonitorBackgroundService Create(INetworkMonitor monitor, IStatusStore store) =>
        new(monitor, store, new ThrowingDisplay(), Options.Create(new MonitorOptions()), TimeProvider.System, NullLogger<MonitorBackgroundService>.Instance);

    [Fact]
    public async Task RunCycle_SavesEvenWhenDisplayFails()
    {
        var store = new RecordingStore();
        var status = new NetworkStatus(NetworkHealth.Good, "ok", DateTimeOffset.UnixEpoch, []);

        await Create(new StubMonitor(() => status), store).RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Same(status, Assert.Single(store.Saved));
    }

    [Fact]
    public async Task RunCycle_MonitorFailure_IsContained()
    {
        var store = new RecordingStore();

        await Create(new StubMonitor(() => throw new InvalidOperationException("boom")), store).RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Empty(store.Saved);
    }
}
