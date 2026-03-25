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
    public void IsRouterAutoDetect_WhenSpecificIp_ReturnsFalse()
    {
        // Arrange
        var options = new MonitorOptions { RouterAddress = "192.168.1.1" };

        // Act & Assert
        Assert.False(options.IsRouterAutoDetect);
    }

    [Fact]
    public void DefaultValues_AreReasonable()
    {
        // Arrange & Act
        var options = new MonitorOptions();

        // Assert
        Assert.Equal(3000, options.TimeoutMs);
        Assert.Equal(5000, options.IntervalMs);
        Assert.Equal(3, options.PingsPerCycle);
        Assert.True(options.EnableFallbackTargets);
        Assert.True(options.EnableIPv6);
        Assert.True(options.EnableDnsChecks);
        Assert.True(options.QuietConsole);
        Assert.Empty(options.CustomTargets);
        Assert.Empty(options.DisabledChecks);
    }

    [Fact]
    public void QuietConsole_DefaultsToTrue()
    {
        // Arrange & Act
        var options = new MonitorOptions();

        // Assert — quiet mode is opt-in for everyone by default
        Assert.True(options.QuietConsole);
    }

    [Fact]
    public void QuietConsole_CanBeDisabled()
    {
        // Arrange & Act
        var options = new MonitorOptions { QuietConsole = false };

        // Assert
        Assert.False(options.QuietConsole);
    }

    [Fact]
    public void IsCheckDisabled_WhenInList_ReturnsTrue()
    {
        // Arrange
        var options = new MonitorOptions { DisabledChecks = ["Router", "Teams"] };

        // Act & Assert
        Assert.True(options.IsCheckDisabled("Router"));
        Assert.True(options.IsCheckDisabled("router")); // case-insensitive
        Assert.True(options.IsCheckDisabled("Teams"));
        Assert.False(options.IsCheckDisabled("Internet"));
    }

    [Fact]
    public void IsCheckDisabled_WhenEmpty_ReturnsFalse()
    {
        // Arrange
        var options = new MonitorOptions();

        // Act & Assert
        Assert.False(options.IsCheckDisabled("Router"));
    }
}
