using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Services;
using NetworkMonitor.Tests.Fakes;
using Xunit;

namespace NetworkMonitor.Tests.Services;

/// <summary>
/// Tests for NetworkConfigurationService.
/// </summary>
public sealed class NetworkConfigurationServiceTests : IDisposable
{
    private readonly FakeGatewayDetector _gatewayDetector;
    private readonly FakeInternetTargetProvider _internetTargetProvider;
    private readonly FakePingService _pingService;
    private NetworkConfigurationService? _service;

    public NetworkConfigurationServiceTests()
    {
        _gatewayDetector = new FakeGatewayDetector();
        _internetTargetProvider = new FakeInternetTargetProvider();
        _pingService = new FakePingService();
    }

    private NetworkConfigurationService CreateService(MonitorOptions? options = null)
    {
        _service = new NetworkConfigurationService(
            _gatewayDetector,
            _internetTargetProvider,
            _pingService,
            Options.Create(options ?? new MonitorOptions()),
            NullLogger<NetworkConfigurationService>.Instance);
        return _service;
    }

    public void Dispose()
    {
        _service?.Dispose();
    }

    [Fact]
    public async Task GetRouterAddressAsync_WhenExplicitlyConfigured_ReturnsConfiguredAddress()
    {
        // Arrange
        var options = new MonitorOptions { RouterAddress = "10.0.0.1" };
        var service = CreateService(options);

        // Act
        var result = await service.GetRouterAddressAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("10.0.0.1", result);
    }

    [Fact]
    public async Task GetRouterAddressAsync_WhenAutoDetect_UsesGatewayDetector()
    {
        // Arrange
        _gatewayDetector.WithGateway("192.168.1.1");
        _pingService.AlwaysSucceed(5);
        var options = new MonitorOptions { RouterAddress = "auto" };
        var service = CreateService(options);

        // Act
        var result = await service.GetRouterAddressAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("192.168.1.1", result);
    }

    [Fact]
    public async Task GetRouterAddressAsync_WhenAutoDetectFails_TriesCommonGateways()
    {
        // Arrange
        _gatewayDetector.WithGateway(null); // No auto-detected gateway
        _gatewayDetector.WithCommonGateways("192.168.0.1", "192.168.1.1", "10.0.0.1");
        _pingService.AlwaysSucceed(5);
        var options = new MonitorOptions { RouterAddress = "auto" };
        var service = CreateService(options);

        // Act
        var result = await service.GetRouterAddressAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("192.168.0.1", result); // First common gateway that responds
    }

    [Fact]
    public async Task GetInternetTargetAsync_ReturnsPrimaryTarget()
    {
        // Arrange - Configure the fake to use "1.1.1.1" as primary
        _internetTargetProvider.WithPrimaryTarget("1.1.1.1");
        _internetTargetProvider.WithTargets("1.1.1.1", "8.8.8.8");
        _pingService.AlwaysSucceed(5);
        var options = new MonitorOptions { EnableFallbackTargets = false };
        var service = CreateService(options);

        // Act
        var result = await service.GetInternetTargetAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("1.1.1.1", result);
    }

    [Fact]
    public async Task GetInternetTargetAsync_WhenFallbackEnabled_ReturnsFirstReachable()
    {
        // Arrange
        _internetTargetProvider.WithTargets("8.8.8.8", "1.1.1.1");
        _pingService.AlwaysSucceed(5);
        var options = new MonitorOptions { EnableFallbackTargets = true };
        var service = CreateService(options);

        // Act
        var result = await service.GetInternetTargetAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("8.8.8.8", result); // First reachable target
    }

    [Fact]
    public async Task GetInternetTargetAsync_WhenPrimaryUnreachable_UsesFallback()
    {
        // Arrange
        _internetTargetProvider.WithTargets("8.8.8.8", "1.1.1.1");
        // First target fails, second succeeds
        _pingService.QueueResult(PingResult.Failed("8.8.8.8", "Timeout"));
        _pingService.QueueResult(PingResult.Succeeded("1.1.1.1", 10));
        var options = new MonitorOptions { EnableFallbackTargets = true };
        var service = CreateService(options);

        // Act
        var result = await service.GetInternetTargetAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("1.1.1.1", result);
    }

    [Fact]
    public async Task ResultsAreCached_MultipleCallsReturnSameValue()
    {
        // Arrange
        _gatewayDetector.WithGateway("192.168.1.1");
        _pingService.AlwaysSucceed(5);
        var options = new MonitorOptions { RouterAddress = "auto" };
        var service = CreateService(options);

        // Act
        var result1 = await service.GetRouterAddressAsync(TestContext.Current.CancellationToken);
        _gatewayDetector.WithGateway("10.0.0.1"); // Change gateway
        var result2 = await service.GetRouterAddressAsync(TestContext.Current.CancellationToken);

        // Assert - Should return cached value
        Assert.Equal(result1, result2);
        Assert.Equal("192.168.1.1", result2);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        // Arrange
        var service = CreateService();

        // Act & Assert - Should not throw
        service.Dispose();
        service.Dispose();
    }

    [Fact]
    public async Task GetRouterAddressAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        var service = CreateService();
        service.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => service.GetRouterAddressAsync(TestContext.Current.CancellationToken));
    }
}
