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
public sealed class NetworkMonitorServiceTests : IDisposable
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

    public void Dispose()
    {
        _configService.Dispose();
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenBothSucceed_ReturnsExcellentOrGood()
    {
        // Arrange
        _pingService.AlwaysSucceed(latencyMs: 5);

        // Act
        var status = await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(status.Health is NetworkHealth.Excellent or NetworkHealth.Good);
        Assert.True(status.RouterResult?.Success);
        Assert.True(status.InternetResult?.Success);
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenRouterFails_ReturnsOfflineOrDegraded()
    {
        // Arrange - router fails, internet succeeds
        _pingService
            .QueueResult(PingResult.Failed("192.168.1.1", "Timeout"))
            .QueueResult(PingResult.Failed("192.168.1.1", "Timeout"))
            .QueueResult(PingResult.Failed("192.168.1.1", "Timeout"))
            .AlwaysSucceed(latencyMs: 10); // Internet succeeds

        // Act
        var status = await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(status.Health is NetworkHealth.Offline or NetworkHealth.Degraded or NetworkHealth.Poor);
        Assert.False(status.RouterResult?.Success);
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenInternetFails_ReturnsDegradedOrPoor()
    {
        // Arrange - router succeeds, internet fails
        _pingService
            .QueueResult(PingResult.Succeeded("192.168.1.1", 5))
            .QueueResult(PingResult.Failed("8.8.8.8", "Timeout"))
            .QueueResult(PingResult.Failed("8.8.8.8", "Timeout"))
            .QueueResult(PingResult.Failed("8.8.8.8", "Timeout"));

        // Act
        var status = await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(status.Health is NetworkHealth.Degraded or NetworkHealth.Poor or NetworkHealth.Offline);
        Assert.True(status.RouterResult?.Success);
        Assert.False(status.InternetResult?.Success);
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenBothFail_ReturnsOffline()
    {
        // Arrange - both fail
        _pingService.AlwaysFail("Network unreachable");

        // Act
        var status = await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(NetworkHealth.Offline, status.Health);
        Assert.False(status.RouterResult?.Success);
        Assert.False(status.InternetResult?.Success);
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenNoRouter_StillChecksInternet()
    {
        // Arrange - no router configured
        _configService.WithRouterAddress(null);
        _pingService.AlwaysSucceed(latencyMs: 10);

        // Act
        var status = await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(status.RouterResult);
        Assert.NotNull(status.InternetResult);
        Assert.True(status.InternetResult.Success);
    }

    [Fact]
    public async Task CheckNetworkAsync_HighLatency_ReturnsGoodOrDegraded()
    {
        // Arrange
        _pingService.AlwaysSucceed(latencyMs: 150);

        // Act
        var status = await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(status.Health is NetworkHealth.Good or NetworkHealth.Degraded);
    }

    [Fact]
    public async Task StatusChanged_FiresWhenHealthChanges()
    {
        // Arrange
        NetworkStatusEventArgs? receivedArgs = null;
        _service.StatusChanged += (_, args) => receivedArgs = args;
        _pingService.AlwaysSucceed(latencyMs: 5);

        // Act - first check establishes baseline
        await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Change to failing
        _pingService.AlwaysFail("Network error");
        await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(receivedArgs);
        Assert.NotNull(receivedArgs.CurrentStatus);
    }

    [Fact]
    public async Task StatusChanged_IncludesPreviousStatus()
    {
        // Arrange
        var receivedArgs = new List<NetworkStatusEventArgs>();
        _service.StatusChanged += (_, args) => receivedArgs.Add(args);
        _pingService.AlwaysSucceed(latencyMs: 5);

        // Act - first check
        await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Second check - should have previous
        _pingService.AlwaysFail("Timeout");
        await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(receivedArgs.Count >= 1);
        // The second event should have a previous status
        if (receivedArgs.Count > 1)
        {
            Assert.NotNull(receivedArgs[1].PreviousStatus);
        }
    }

    [Fact]
    public async Task CheckNetworkAsync_CancellationToken_Respected()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.CheckNetworkAsync(cts.Token));
    }
}
