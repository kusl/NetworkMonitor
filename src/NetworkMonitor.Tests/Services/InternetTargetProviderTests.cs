using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Services;
using Xunit;

namespace NetworkMonitor.Tests.Services;

/// <summary>
/// Tests for InternetTargetProvider.
/// </summary>
public sealed class InternetTargetProviderTests
{
    [Fact]
    public void PrimaryTarget_ReturnsConfiguredTarget()
    {
        // Arrange
        var options = Options.Create(new MonitorOptions { InternetTarget = "1.1.1.1" });
        var provider = new InternetTargetProvider(options, NullLogger<InternetTargetProvider>.Instance);

        // Act & Assert
        Assert.Equal("1.1.1.1", provider.PrimaryTarget);
    }

    [Fact]
    public void GetTargets_ReturnsConfiguredTargetFirst()
    {
        // Arrange
        var options = Options.Create(new MonitorOptions { InternetTarget = "1.1.1.1" });
        var provider = new InternetTargetProvider(options, NullLogger<InternetTargetProvider>.Instance);

        // Act
        var targets = provider.GetTargets();

        // Assert
        Assert.Equal("1.1.1.1", targets[0]);
    }

    [Fact]
    public void GetTargets_IncludesMultipleFallbacks()
    {
        // Arrange
        var options = Options.Create(new MonitorOptions());
        var provider = new InternetTargetProvider(options, NullLogger<InternetTargetProvider>.Instance);

        // Act
        var targets = provider.GetTargets();

        // Assert
        Assert.True(targets.Count >= 3, "Should have multiple fallback targets");
        Assert.Contains("8.8.8.8", targets);
        Assert.Contains("1.1.1.1", targets);
    }

    [Fact]
    public void GetTargets_CustomTargetAddedToFront()
    {
        // Arrange - use a target not in the default list
        var options = Options.Create(new MonitorOptions { InternetTarget = "4.4.4.4" });
        var provider = new InternetTargetProvider(options, NullLogger<InternetTargetProvider>.Instance);

        // Act
        var targets = provider.GetTargets();

        // Assert
        Assert.Equal("4.4.4.4", targets[0]);
        Assert.Contains("8.8.8.8", targets); // Default fallbacks still present
    }
}
