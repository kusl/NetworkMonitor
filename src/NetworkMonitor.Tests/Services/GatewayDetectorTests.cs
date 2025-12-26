using Microsoft.Extensions.Logging.Abstractions;
using NetworkMonitor.Core.Services;
using Xunit;

namespace NetworkMonitor.Tests.Services;

/// <summary>
/// Tests for GatewayDetector.
/// Note: These tests run against the real network stack, so results
/// depend on the test environment. We test the interface contract.
/// </summary>
public sealed class GatewayDetectorTests
{
    private readonly GatewayDetector _detector;

    public GatewayDetectorTests()
    {
        _detector = new GatewayDetector(NullLogger<GatewayDetector>.Instance);
    }

    [Fact]
    public void DetectDefaultGateway_ReturnsValidIpOrNull()
    {
        // Act
        var result = _detector.DetectDefaultGateway();

        // Assert - should be null or a valid IP
        if (result != null)
        {
            Assert.Matches(@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$", result);
        }
    }

    [Fact]
    public void GetCommonGatewayAddresses_ReturnsNonEmptyList()
    {
        // Act
        var addresses = _detector.GetCommonGatewayAddresses();

        // Assert
        Assert.NotEmpty(addresses);
        Assert.Contains("192.168.1.1", addresses);
        Assert.Contains("192.168.0.1", addresses);
        Assert.Contains("10.0.0.1", addresses);
    }

    [Fact]
    public void GetCommonGatewayAddresses_AllAreValidIpAddresses()
    {
        // Act
        var addresses = _detector.GetCommonGatewayAddresses();

        // Assert
        foreach (var address in addresses)
        {
            Assert.Matches(@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$", address);
        }
    }
}
