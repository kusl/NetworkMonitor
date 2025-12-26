using NetworkMonitor.Core.Models;
using Xunit;

namespace NetworkMonitor.Tests;

/// <summary>
/// Tests for NetworkStatus model.
/// </summary>
internal sealed class NetworkStatusTests
{
    [Theory]
    [InlineData(NetworkHealth.Excellent, true)]
    [InlineData(NetworkHealth.Good, true)]
    [InlineData(NetworkHealth.Degraded, true)]
    [InlineData(NetworkHealth.Poor, false)]
    [InlineData(NetworkHealth.Offline, false)]
    public void IsUsable_ReturnsCorrectValue(NetworkHealth health, bool expectedUsable)
    {
        // Arrange
        var status = new NetworkStatus(
            health,
            PingResult.Succeeded("router", 10),
            PingResult.Succeeded("internet", 20),
            DateTimeOffset.UtcNow,
            "Test message");

        // Act & Assert
        Assert.Equal(expectedUsable, status.IsUsable);
    }
}
