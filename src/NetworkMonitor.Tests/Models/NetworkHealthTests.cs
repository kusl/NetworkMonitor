using NetworkMonitor.Core.Models;
using Xunit;

namespace NetworkMonitor.Tests.Models;

/// <summary>
/// Tests for NetworkHealth enum values.
/// </summary>
public sealed class NetworkHealthTests
{
    [Fact]
    public void NetworkHealth_HasExpectedValues()
    {
        // Assert all expected values exist
        Assert.Equal(0, (int)NetworkHealth.Offline);
        Assert.Equal(1, (int)NetworkHealth.Poor);
        Assert.Equal(2, (int)NetworkHealth.Degraded);
        Assert.Equal(3, (int)NetworkHealth.Good);
        Assert.Equal(4, (int)NetworkHealth.Excellent);
    }

    [Fact]
    public void NetworkHealth_CanCompare()
    {
        // Assert ordering works as expected
        Assert.True(NetworkHealth.Excellent > NetworkHealth.Good);
        Assert.True(NetworkHealth.Good > NetworkHealth.Degraded);
        Assert.True(NetworkHealth.Degraded > NetworkHealth.Poor);
        Assert.True(NetworkHealth.Poor > NetworkHealth.Offline);
    }
}
