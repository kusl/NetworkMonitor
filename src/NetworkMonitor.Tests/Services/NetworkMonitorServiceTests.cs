using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Services;
using NetworkMonitor.Tests.Fakes;
using Xunit;

namespace NetworkMonitor.Tests.Services;

/// <summary>
/// Tests for NetworkMonitorService.
/// Uses fake implementations for isolation.
/// </summary>
public sealed class NetworkMonitorServiceTests
{
    private readonly FakePingService _pingService;
    private readonly FakeNetworkConfigurationService _configService;
    private readonly NetworkMonitorService _service;

    public NetworkMonitorServiceTests()
    {
        _pingService = new FakePingService();
        _configService = new FakeNetworkConfigurationService();
        var options = Options.Create(new MonitorOptions());
        _service = new NetworkMonitorService(
            _pingService,
            _configService,
            options,
            NullLogger<NetworkMonitorService>.Instance);
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenBothSucceed_ReturnsExcellent()
    {
        // Arrange
        _pingService.AlwaysSucceed(latencyMs: 5);

        // Act
        var status = await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(NetworkHealth.Excellent, status.Health);
        Assert.True(status.RouterResult?.Success);
        Assert.True(status.InternetResult?.Success);
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenRouterFails_ReturnsDegradedOrPoor()
    {
        // Arrange - router fails, internet succeeds
        _configService.WithRouterAddress("192.168.1.1");
        _pingService
            .QueueResult(PingResult.Failed("192.168.1.1", "Timeout"))
            .QueueResult(PingResult.Failed("192.168.1.1", "Timeout"))
            .QueueResult(PingResult.Failed("192.168.1.1", "Timeout"))
            .QueueResult(PingResult.Succeeded("8.8.8.8", 20))
            .QueueResult(PingResult.Succeeded("8.8.8.8", 20))
            .QueueResult(PingResult.Succeeded("8.8.8.8", 20));

        // Act
        var status = await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(NetworkHealth.Degraded, status.Health);
        Assert.False(status.RouterResult?.Success);
        Assert.True(status.InternetResult?.Success);
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenInternetFails_ReturnsPoor()
    {
        // Arrange - router succeeds, internet fails
        _pingService
            .QueueResult(PingResult.Succeeded("192.168.1.1", 5))
            .QueueResult(PingResult.Succeeded("192.168.1.1", 5))
            .QueueResult(PingResult.Succeeded("192.168.1.1", 5))
            .QueueResult(PingResult.Failed("8.8.8.8", "Timeout"))
            .QueueResult(PingResult.Failed("8.8.8.8", "Timeout"))
            .QueueResult(PingResult.Failed("8.8.8.8", "Timeout"));

        // Act
        var status = await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(NetworkHealth.Poor, status.Health);
        Assert.True(status.RouterResult?.Success);
        Assert.False(status.InternetResult?.Success);
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenBothFail_ReturnsOffline()
    {
        // Arrange
        _pingService.AlwaysFail("Network unreachable");

        // Act
        var status = await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(NetworkHealth.Offline, status.Health);
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenNoRouterConfigured_SkipsRouterPing()
    {
        // Arrange
        _configService.WithRouterAddress(null);
        _pingService.AlwaysSucceed(20);

        // Act
        var status = await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(status.RouterResult);
        Assert.NotNull(status.InternetResult);
        Assert.True(status.InternetResult.Success);
    }

    [Fact]
    public async Task CheckNetworkAsync_HighLatency_ReturnsDegraded()
    {
        // Arrange - High latency on internet
        _pingService
            .QueueResult(PingResult.Succeeded("192.168.1.1", 10))
            .QueueResult(PingResult.Succeeded("192.168.1.1", 10))
            .QueueResult(PingResult.Succeeded("192.168.1.1", 10))
            .QueueResult(PingResult.Succeeded("8.8.8.8", 500))
            .QueueResult(PingResult.Succeeded("8.8.8.8", 500))
            .QueueResult(PingResult.Succeeded("8.8.8.8", 500));

        // Act
        var status = await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(NetworkHealth.Poor, status.Health);
    }

    [Fact]
    public async Task CheckNetworkAsync_FiresStatusChangedEvent()
    {
        // Arrange
        _pingService.AlwaysSucceed(5);
        NetworkStatus? receivedStatus = null;
        _service.StatusChanged += (_, e) => receivedStatus = e.CurrentStatus;

        // Act
        await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(receivedStatus);
        Assert.Equal(NetworkHealth.Excellent, receivedStatus.Health);
    }

    [Fact]
    public async Task CheckNetworkAsync_RespectsCancellation()
    {
        // Arrange
        _pingService.AlwaysSucceed(5);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _service.CheckNetworkAsync(cts.Token));
    }
}
