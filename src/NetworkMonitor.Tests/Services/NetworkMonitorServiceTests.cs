using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Services;
using NetworkMonitor.Tests.Fakes;
using Xunit;

namespace NetworkMonitor.Tests.Services;

/// <summary>
/// Tests for NetworkMonitorService.
/// </summary>
public sealed class NetworkMonitorServiceTests : IDisposable
{
    private readonly FakePingService _pingService;
    private readonly FakeNetworkConfigurationService _configService;
    private readonly FakeInternetTargetProvider _internetTargetProvider;
    private readonly FakeDnsResolverService _dnsResolver;
    private readonly MonitorOptions _options;

    public NetworkMonitorServiceTests()
    {
        _pingService = new FakePingService();
        _configService = new FakeNetworkConfigurationService();
        _internetTargetProvider = new FakeInternetTargetProvider();
        _dnsResolver = new FakeDnsResolverService().AlwaysSucceed();
        _options = new MonitorOptions
        {
            PingsPerCycle = 1,
            TimeoutMs = 1000,
            ExcellentLatencyMs = 20,
            GoodLatencyMs = 50
        };
    }

    public void Dispose()
    {
        _configService.Dispose();
    }

    private NetworkMonitorService CreateService(MonitorOptions? options = null)
    {
        return new NetworkMonitorService(
            _pingService,
            _configService,
            _internetTargetProvider,
            Options.Create(options ?? _options),
            NullLogger<NetworkMonitorService>.Instance,
            _dnsResolver);
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenAllSucceed_ReturnsExcellentOrGood()
    {
        // Arrange
        _configService.WithRouterAddress("192.168.1.1");
        _configService.WithInternetTarget("8.8.8.8");

        // Queue successful pings with low latency
        _pingService.QueueResult(PingResult.Succeeded("192.168.1.1", 5));
        _pingService.QueueResult(PingResult.Succeeded("8.8.8.8", 10));

        var service = CreateService();

        // Act
        var status = await service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(
            status.Health is NetworkHealth.Excellent or NetworkHealth.Good,
            $"Expected Excellent or Good but got {status.Health}");
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenRouterFails_ReturnsOfflineOrDegraded()
    {
        // Arrange
        _configService.WithRouterAddress("192.168.1.1");
        _configService.WithInternetTarget("8.8.8.8");

        // Router fails, internet succeeds
        _pingService.QueueResult(PingResult.Failed("192.168.1.1", "Timeout"));
        _pingService.QueueResult(PingResult.Succeeded("8.8.8.8", 10));

        var service = CreateService();

        // Act
        var status = await service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert - Router failure with internet success = Degraded
        Assert.True(
            status.Health is NetworkHealth.Offline or NetworkHealth.Degraded,
            $"Expected Offline or Degraded but got {status.Health}");
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenAllFail_ReturnsOffline()
    {
        // Arrange
        _configService.WithRouterAddress("192.168.1.1");
        _configService.WithInternetTarget("8.8.8.8");
        _pingService.AlwaysFail();

        var service = CreateService();

        // Act
        var status = await service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(NetworkHealth.Offline, status.Health);
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenInternetFailsButRouterOK_ReturnsPoor()
    {
        // Arrange
        _configService.WithRouterAddress("192.168.1.1");
        _configService.WithInternetTarget("8.8.8.8");

        _pingService.QueueResult(PingResult.Succeeded("192.168.1.1", 5));
        _pingService.QueueResult(PingResult.Failed("8.8.8.8", "Timeout"));

        var service = CreateService();

        // Act
        var status = await service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(NetworkHealth.Poor, status.Health);
    }

    [Fact]
    public async Task CheckNetworkAsync_NoRouter_UsesOnlyInternet()
    {
        // Arrange
        _configService.WithRouterAddress(null);
        _configService.WithInternetTarget("8.8.8.8");
        _pingService.AlwaysSucceed(5);

        var service = CreateService();

        // Act
        var status = await service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(status.RouterResult);
        Assert.True(status.Health is NetworkHealth.Excellent or NetworkHealth.Good);
    }

    [Fact]
    public async Task CheckNetworkAsync_HighLatency_ReturnsPoorOrDegraded()
    {
        // Arrange
        _configService.WithRouterAddress("192.168.1.1");
        _configService.WithInternetTarget("8.8.8.8");

        // High latency
        _pingService.QueueResult(PingResult.Succeeded("192.168.1.1", 5));
        _pingService.QueueResult(PingResult.Succeeded("8.8.8.8", 200));

        var service = CreateService();

        // Act
        var status = await service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert - High internet latency
        Assert.True(
            status.Health is NetworkHealth.Poor or NetworkHealth.Degraded,
            $"Expected Poor or Degraded but got {status.Health}");
    }

    [Fact]
    public async Task CheckNetworkAsync_StatusChangedEvent_RaisedOnFirstCheck()
    {
        // Arrange
        _configService.WithRouterAddress("192.168.1.1");
        _configService.WithInternetTarget("8.8.8.8");
        _pingService.AlwaysSucceed(5);

        var service = CreateService();

        NetworkStatusEventArgs? eventArgs = null;
        service.StatusChanged += (_, args) => eventArgs = args;

        // Act
        await service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(eventArgs);
        Assert.NotNull(eventArgs.CurrentStatus);
    }

    [Fact]
    public async Task CheckNetworkAsync_StatusChangedEvent_IncludesPreviousStatus()
    {
        // Arrange
        _configService.WithRouterAddress("192.168.1.1");
        _configService.WithInternetTarget("8.8.8.8");

        // First check - excellent
        _pingService.QueueResult(PingResult.Succeeded("192.168.1.1", 5));
        _pingService.QueueResult(PingResult.Succeeded("8.8.8.8", 10));

        // Second check - offline
        _pingService.QueueResult(PingResult.Failed("192.168.1.1", "Timeout"));
        _pingService.QueueResult(PingResult.Failed("8.8.8.8", "Timeout"));

        var service = CreateService();
        var events = new List<NetworkStatusEventArgs>();
        service.StatusChanged += (_, args) => events.Add(args);

        // Act
        await service.CheckNetworkAsync(TestContext.Current.CancellationToken);
        await service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert - Should have two events, second one has previous status
        Assert.Equal(2, events.Count);
        Assert.Null(events[0].PreviousStatus); // First event has no previous
        Assert.NotNull(events[1].PreviousStatus); // Second event has previous
    }

    [Fact]
    public async Task CheckNetworkAsync_SupportsCancellation()
    {
        // Arrange
        _configService.WithRouterAddress("192.168.1.1");
        _configService.WithInternetTarget("8.8.8.8");

        var service = CreateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.CheckNetworkAsync(cts.Token));
    }

    [Fact]
    public async Task CheckNetworkAsync_WithDisabledCheck_SkipsRouter()
    {
        // Arrange
        _configService.WithRouterAddress("192.168.1.1");
        _configService.WithInternetTarget("8.8.8.8");
        _pingService.AlwaysSucceed(5);

        var options = new MonitorOptions
        {
            PingsPerCycle = 1,
            TimeoutMs = 1000,
            ExcellentLatencyMs = 20,
            GoodLatencyMs = 50,
            DisabledChecks = ["Router"]
        };

        var service = CreateService(options);

        // Act
        var status = await service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert - Router should be null when disabled
        Assert.Null(status.RouterResult);
    }

    [Fact]
    public async Task CheckNetworkAsync_WithCustomTargets_IncludesResults()
    {
        // Arrange
        _configService.WithRouterAddress("192.168.1.1");
        _configService.WithInternetTarget("8.8.8.8");
        _pingService.AlwaysSucceed(5);

        var options = new MonitorOptions
        {
            PingsPerCycle = 1,
            TimeoutMs = 1000,
            ExcellentLatencyMs = 20,
            GoodLatencyMs = 50,
            CustomTargets =
            [
                new CustomTargetConfig { Name = "Intranet", Address = "10.0.0.12", Enabled = true }
            ]
        };

        var service = CreateService(options);

        // Act
        var status = await service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(status.TargetResults);
        Assert.Contains(status.TargetResults, r => r.Target.Name == "Intranet");
    }

    [Fact]
    public async Task CheckNetworkAsync_WithDisabledCustomTarget_SkipsIt()
    {
        // Arrange
        _configService.WithRouterAddress("192.168.1.1");
        _configService.WithInternetTarget("8.8.8.8");
        _pingService.AlwaysSucceed(5);

        var options = new MonitorOptions
        {
            PingsPerCycle = 1,
            TimeoutMs = 1000,
            ExcellentLatencyMs = 20,
            GoodLatencyMs = 50,
            CustomTargets =
            [
                new CustomTargetConfig { Name = "Teams", Address = "teams.microsoft.com", Enabled = false }
            ]
        };

        var service = CreateService(options);

        // Act
        var status = await service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(status.TargetResults);
        Assert.DoesNotContain(status.TargetResults, r => r.Target.Name == "Teams");
    }

    [Fact]
    public async Task CheckNetworkAsync_ReturnsTargetResults()
    {
        // Arrange
        _configService.WithRouterAddress("192.168.1.1");
        _configService.WithInternetTarget("8.8.8.8");
        _pingService.AlwaysSucceed(5);

        var service = CreateService();

        // Act
        var status = await service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert - should have at least router and internet results
        Assert.NotNull(status.TargetResults);
        Assert.True(status.TargetResults.Count >= 2);
    }
}
