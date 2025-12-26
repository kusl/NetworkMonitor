using NetworkMonitor.Core.Models;
using Xunit;

namespace NetworkMonitor.Tests.Models;

/// <summary>
/// Tests for MonitorOptions.
/// </summary>
public sealed class MonitorOptionsTests
{
    [Fact]
    public void IsRouterAutoDetect_WhenAuto_ReturnsTrue()
    {
        // Arrange
        var options = new MonitorOptions { RouterAddress = "auto" };

        // Act & Assert
        Assert.True(options.IsRouterAutoDetect);
    }

    [Fact]
    public void IsRouterAutoDetect_WhenAutoUppercase_ReturnsTrue()
    {
        // Arrange
        var options = new MonitorOptions { RouterAddress = "AUTO" };

        // Act & Assert
        Assert.True(options.IsRouterAutoDetect);
    }

    [Fact]
    public void IsRouterAutoDetect_WhenEmpty_ReturnsTrue()
    {
        // Arrange
        var options = new MonitorOptions { RouterAddress = "" };

        // Act & Assert
        Assert.True(options.IsRouterAutoDetect);
    }

    [Fact]
    public void IsRouterAutoDetect_WhenNull_ReturnsTrue()
    {
        // Arrange
        var options = new MonitorOptions { RouterAddress = null! };

        // Act & Assert
        Assert.True(options.IsRouterAutoDetect);
    }

    [Fact]
    public void IsRouterAutoDetect_WhenIpAddress_ReturnsFalse()
    {
        // Arrange
        var options = new MonitorOptions { RouterAddress = "192.168.1.1" };

        // Act & Assert
        Assert.False(options.IsRouterAutoDetect);
    }

    [Fact]
    public void DefaultRouterAddress_IsAuto()
    {
        // Arrange & Act
        var options = new MonitorOptions();

        // Assert
        Assert.Equal("auto", options.RouterAddress);
        Assert.True(options.IsRouterAutoDetect);
    }

    [Fact]
    public void EnableFallbackTargets_DefaultsToTrue()
    {
        // Arrange & Act
        var options = new MonitorOptions();

        // Assert
        Assert.True(options.EnableFallbackTargets);
    }
}
