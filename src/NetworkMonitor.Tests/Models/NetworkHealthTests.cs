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
        // Assert all expected values exist and have correct numeric values
        // Ordered from worst (0) to best (4)
        Assert.Equal(0, (int)NetworkHealth.Offline);
        Assert.Equal(1, (int)NetworkHealth.Poor);
        Assert.Equal(2, (int)NetworkHealth.Degraded);
        Assert.Equal(3, (int)NetworkHealth.Good);
        Assert.Equal(4, (int)NetworkHealth.Excellent);
    }

    [Fact]
    public void NetworkHealth_ValuesAreDefined()
    {
        // Assert all expected values are defined in the enum
        Assert.True(Enum.IsDefined<NetworkHealth>(NetworkHealth.Offline));
        Assert.True(Enum.IsDefined<NetworkHealth>(NetworkHealth.Poor));
        Assert.True(Enum.IsDefined<NetworkHealth>(NetworkHealth.Degraded));
        Assert.True(Enum.IsDefined<NetworkHealth>(NetworkHealth.Good));
        Assert.True(Enum.IsDefined<NetworkHealth>(NetworkHealth.Excellent));
    }

    [Fact]
    public void NetworkHealth_CanCompare()
    {
        // Assert ordering works as expected (Excellent > Good > Degraded > Poor > Offline)
        Assert.True(NetworkHealth.Excellent > NetworkHealth.Good);
        Assert.True(NetworkHealth.Good > NetworkHealth.Degraded);
        Assert.True(NetworkHealth.Degraded > NetworkHealth.Poor);
        Assert.True(NetworkHealth.Poor > NetworkHealth.Offline);
    }

    [Fact]
    public void NetworkHealth_ToString_ReturnsName()
    {
        Assert.Equal("Excellent", NetworkHealth.Excellent.ToString());
        Assert.Equal("Good", NetworkHealth.Good.ToString());
        Assert.Equal("Degraded", NetworkHealth.Degraded.ToString());
        Assert.Equal("Poor", NetworkHealth.Poor.ToString());
        Assert.Equal("Offline", NetworkHealth.Offline.ToString());
    }

    [Fact]
    public void NetworkHealth_ComparisonOperators_WorkCorrectly()
    {
        // Test various comparison operators
        // Assert.True(NetworkHealth.Excellent >= NetworkHealth.Excellent);
        Assert.True(NetworkHealth.Excellent >= NetworkHealth.Good);
        Assert.False(NetworkHealth.Good >= NetworkHealth.Excellent);

        // Assert.True(NetworkHealth.Offline <= NetworkHealth.Offline);
        Assert.True(NetworkHealth.Offline <= NetworkHealth.Poor);
        Assert.False(NetworkHealth.Poor <= NetworkHealth.Offline);

        Assert.True(NetworkHealth.Excellent != NetworkHealth.Good);
        // Assert.True(NetworkHealth.Excellent == NetworkHealth.Excellent);
    }
}
