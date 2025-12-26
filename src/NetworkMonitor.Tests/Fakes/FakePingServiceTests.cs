using NetworkMonitor.Core.Models;
using Xunit;

namespace NetworkMonitor.Tests.Fakes;

/// <summary>
/// Tests for the FakePingService itself.
/// Ensures our test doubles work correctly.
/// </summary>
public sealed class FakePingServiceTests
{
    [Fact]
    public async Task AlwaysSucceed_ReturnsSuccessfulPings()
    {
        // Arrange
        var fake = new FakePingService().AlwaysSucceed(25);
        
        // Act
        var result = await fake.PingAsync("test", 1000);
        
        // Assert
        Assert.True(result.Success);
        Assert.Equal(25, result.RoundtripTimeMs);
    }
    
    [Fact]
    public async Task QueuedResults_ReturnedInOrder()
    {
        // Arrange
        var fake = new FakePingService()
            .QueueResult(PingResult.Succeeded("", 10))
            .QueueResult(PingResult.Succeeded("", 20))
            .QueueResult(PingResult.Failed("", "error"));
        
        // Act
        var r1 = await fake.PingAsync("target", 1000);
        var r2 = await fake.PingAsync("target", 1000);
        var r3 = await fake.PingAsync("target", 1000);
        
        // Assert
        Assert.Equal(10, r1.RoundtripTimeMs);
        Assert.Equal(20, r2.RoundtripTimeMs);
        Assert.False(r3.Success);
    }
    
    [Fact]
    public async Task PingMultipleAsync_ReturnsRequestedCount()
    {
        // Arrange
        var fake = new FakePingService().AlwaysSucceed();
        
        // Act
        var results = await fake.PingMultipleAsync("test", 5, 1000);
        
        // Assert
        Assert.Equal(5, results.Count);
        Assert.All(results, r => Assert.True(r.Success));
    }
}
