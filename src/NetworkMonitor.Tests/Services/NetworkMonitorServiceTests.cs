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
internal sealed class NetworkMonitorServiceTests
{
    private readonly FakePingService _pingService;
    private readonly NetworkMonitorService _service;

    public NetworkMonitorServiceTests()
    {
        _pingService = new FakePingService();
        var options = Options.Create(new MonitorOptions());
        _service = new NetworkMonitorService(
            _pingService,
            options,
            NullLogger<NetworkMonitorService>.Instance);
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenBothSucceed_ReturnsExcellent()
    {
        // Arrange
        _pingService.AlwaysSucceed(latencyMs: 5);

        // Act
        var status = await _service.CheckNetworkAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);

        // Assert
        Assert.Equal(NetworkHealth.Excellent, status.Health);
        Assert.True(status.RouterResult?.Success);
        Assert.True(status.InternetResult?.Success);
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenRouterFails_ReturnsOffline()
    {
        // Arrange
        _pingService.AlwaysFail("No route to host");

        // Act
        var status = await _service.CheckNetworkAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);

        // Assert
        Assert.Equal(NetworkHealth.Offline, status.Health);
        Assert.Contains("local network", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenInternetFails_ReturnsPoor()
    {
        // Arrange - Router succeeds, internet fails
        _pingService
            .QueueResult(PingResult.Succeeded("router", 10))
            .QueueResult(PingResult.Succeeded("router", 10))
            .QueueResult(PingResult.Succeeded("router", 10))
            .QueueResult(PingResult.Failed("internet", "Timeout"))
            .QueueResult(PingResult.Failed("internet", "Timeout"))
            .QueueResult(PingResult.Failed("internet", "Timeout"));

        // Act
        var status = await _service.CheckNetworkAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);

        // Assert
        Assert.Equal(NetworkHealth.Poor, status.Health);
        Assert.True(status.RouterResult?.Success);
        Assert.False(status.InternetResult?.Success);
    }

    [Fact]
    public async Task CheckNetworkAsync_HighLatency_ReturnsDegraded()
    {
        // Arrange - High latency on internet
        _pingService
            .QueueResult(PingResult.Succeeded("router", 10))
            .QueueResult(PingResult.Succeeded("router", 10))
            .QueueResult(PingResult.Succeeded("router", 10))
            .QueueResult(PingResult.Succeeded("internet", 500))
            .QueueResult(PingResult.Succeeded("internet", 500))
            .QueueResult(PingResult.Succeeded("internet", 500));

        // Act
        var status = await _service.CheckNetworkAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);

        // Assert
        Assert.Equal(NetworkHealth.Degraded, status.Health);
    }

    [Fact]
    public async Task CheckNetworkAsync_FiresStatusChangedEvent()
    {
        // Arrange
        _pingService.AlwaysSucceed(5);
        NetworkStatus? receivedStatus = null;
        _service.StatusChanged += (_, s) => receivedStatus = s;

        // Act
        await _service.CheckNetworkAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);

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
        await cts.CancelAsync().ConfigureAwait(false);

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _service.CheckNetworkAsync(cts.Token)).ConfigureAwait(false);
    }
}
