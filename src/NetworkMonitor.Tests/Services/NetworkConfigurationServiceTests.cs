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
    public async Task GetRouterAddressAsync_WhenAutoDetect_UsesDetectedGateway()
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
    public async Task GetRouterAddressAsync_WhenDetectionFails_FallsBackToCommonGateways()
    {
        // Arrange
        _gatewayDetector.WithNoGateway();
        _gatewayDetector.WithCommonGateways("192.168.0.1", "10.0.0.1");
        _pingService.AlwaysSucceed(5);
        var options = new MonitorOptions { RouterAddress = "auto" };
        var service = CreateService(options);

        // Act
        var result = await service.GetRouterAddressAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("192.168.0.1", result);
    }

    [Fact]
    public async Task GetRouterAddressAsync_WhenNoGatewayReachable_ReturnsNull()
    {
        // Arrange
        _gatewayDetector.WithNoGateway();
        _gatewayDetector.WithCommonGateways(); // Empty
        var options = new MonitorOptions { RouterAddress = "auto" };
        var service = CreateService(options);

        // Act
        var result = await service.GetRouterAddressAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetInternetTargetAsync_ReturnsPrimaryTarget()
    {
        // Arrange
        _internetTargetProvider.WithPrimaryTarget("1.1.1.1");
        var service = CreateService();

        // Act
        var result = await service.GetInternetTargetAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("1.1.1.1", result);
    }

    [Fact]
    public async Task GetRouterAddressAsync_CachesResult()
    {
        // Arrange
        _gatewayDetector.WithGateway("192.168.1.1");
        _pingService.AlwaysSucceed(5);
        var options = new MonitorOptions { RouterAddress = "auto" };
        var service = CreateService(options);

        // Act - call twice
        var result1 = await service.GetRouterAddressAsync(TestContext.Current.CancellationToken);

        // Change the gateway - should not affect second call due to caching
        _gatewayDetector.WithGateway("10.0.0.1");
        var result2 = await service.GetRouterAddressAsync(TestContext.Current.CancellationToken);

        // Assert - both should return cached value
        Assert.Equal("192.168.1.1", result1);
        Assert.Equal("192.168.1.1", result2);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        // Arrange
        var service = CreateService();

        // Act & Assert - should not throw
        service.Dispose();
        service.Dispose();
    }

    [Fact]
    public async Task GetRouterAddressAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        var service = CreateService();
        service.Dispose();
        _service = null; // Prevent double dispose in cleanup

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => service.GetRouterAddressAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetInternetTargetAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        var service = CreateService();
        service.Dispose();
        _service = null; // Prevent double dispose in cleanup

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => service.GetInternetTargetAsync(TestContext.Current.CancellationToken));
    }
}
