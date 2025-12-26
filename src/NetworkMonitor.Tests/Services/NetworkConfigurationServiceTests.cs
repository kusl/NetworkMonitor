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
public sealed class NetworkConfigurationServiceTests
{
    private readonly FakeGatewayDetector _gatewayDetector;
    private readonly FakeInternetTargetProvider _internetTargetProvider;
    private readonly FakePingService _pingService;

    public NetworkConfigurationServiceTests()
    {
        _gatewayDetector = new FakeGatewayDetector();
        _internetTargetProvider = new FakeInternetTargetProvider();
        _pingService = new FakePingService();
    }

    private NetworkConfigurationService CreateService(MonitorOptions? options = null)
    {
        return new NetworkConfigurationService(
            _gatewayDetector,
            _internetTargetProvider,
            _pingService,
            Options.Create(options ?? new MonitorOptions()),
            NullLogger<NetworkConfigurationService>.Instance);
    }

    [Fact]
    public async Task GetRouterAddressAsync_WhenAutoDetect_ReturnsDetectedGateway()
    {
        // Arrange
        _gatewayDetector.WithGateway("192.168.1.254");
        _pingService.AlwaysSucceed(5);
        var service = CreateService(new MonitorOptions { RouterAddress = "auto" });

        // Act
        var result = await service.GetRouterAddressAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("192.168.1.254", result);
    }

    [Fact]
    public async Task GetRouterAddressAsync_WhenConfigured_ReturnsConfiguredAddress()
    {
        // Arrange
        _gatewayDetector.WithGateway("192.168.1.254"); // Should be ignored
        var service = CreateService(new MonitorOptions { RouterAddress = "10.0.0.1" });

        // Act
        var result = await service.GetRouterAddressAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("10.0.0.1", result);
    }

    [Fact]
    public async Task GetRouterAddressAsync_WhenDetectionFails_TriesCommonAddresses()
    {
        // Arrange
        _gatewayDetector
            .WithNoGateway()
            .WithCommonGateways("192.168.1.1", "192.168.0.1");
        _pingService
            .QueueResult(Core.Models.PingResult.Failed("192.168.1.1", "Timeout"))
            .QueueResult(Core.Models.PingResult.Succeeded("192.168.0.1", 5));
        var service = CreateService();

        // Act
        var result = await service.GetRouterAddressAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("192.168.0.1", result);
    }

    [Fact]
    public async Task GetRouterAddressAsync_WhenNothingReachable_ReturnsNull()
    {
        // Arrange
        _gatewayDetector
            .WithNoGateway()
            .WithCommonGateways("192.168.1.1");
        _pingService.AlwaysFail("Timeout");
        var service = CreateService();

        // Act
        var result = await service.GetRouterAddressAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetInternetTargetAsync_WhenPrimaryReachable_ReturnsPrimary()
    {
        // Arrange
        _internetTargetProvider.WithTargets("8.8.8.8", "1.1.1.1");
        _pingService.AlwaysSucceed(10);
        var service = CreateService();

        // Act
        var result = await service.GetInternetTargetAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("8.8.8.8", result);
    }

    [Fact]
    public async Task GetInternetTargetAsync_WhenPrimaryUnreachable_ReturnsFallback()
    {
        // Arrange
        _internetTargetProvider.WithTargets("8.8.8.8", "1.1.1.1");
        _pingService
            .QueueResult(Core.Models.PingResult.Failed("8.8.8.8", "Timeout"))
            .QueueResult(Core.Models.PingResult.Succeeded("1.1.1.1", 10));
        var service = CreateService();

        // Act
        var result = await service.GetInternetTargetAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("1.1.1.1", result);
    }

    [Fact]
    public async Task GetInternetTargetAsync_WhenFallbackDisabled_ReturnsPrimary()
    {
        // Arrange
        _internetTargetProvider.WithTargets("8.8.8.8", "1.1.1.1");
        _pingService.AlwaysFail("Timeout");
        var service = CreateService(new MonitorOptions { EnableFallbackTargets = false });

        // Act
        var result = await service.GetInternetTargetAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("8.8.8.8", result); // Returns primary even if unreachable
    }

    [Fact]
    public async Task InitializeAsync_CachesResults()
    {
        // Arrange
        _gatewayDetector.WithGateway("192.168.1.1");
        _pingService.AlwaysSucceed(5);
        var service = CreateService();

        // Act
        await service.InitializeAsync(TestContext.Current.CancellationToken);
        var result1 = await service.GetRouterAddressAsync(TestContext.Current.CancellationToken);

        // Change the detector (shouldn't affect cached result)
        _gatewayDetector.WithGateway("10.0.0.1");
        var result2 = await service.GetRouterAddressAsync(TestContext.Current.CancellationToken);

        // Assert - both should return cached value
        Assert.Equal("192.168.1.1", result1);
        Assert.Equal("192.168.1.1", result2);
    }
}
