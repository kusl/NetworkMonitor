using NetworkMonitor.Core.Models;
using Xunit;

namespace NetworkMonitor.Tests.Models;

/// <summary>
/// Tests for DnsResult.
/// </summary>
public sealed class DnsResultTests
{
    [Fact]
    public void Succeeded_CreatesSuccessfulResult()
    {
        // Arrange & Act
        var result = DnsResult.Succeeded("example.com", ["1.2.3.4", "5.6.7.8"], 15);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("example.com", result.Hostname);
        Assert.Equal(2, result.ResolvedAddresses.Count);
        Assert.Equal(15, result.ResolutionTimeMs);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void Failed_CreatesFailedResult()
    {
        // Arrange & Act
        var result = DnsResult.Failed("bad.example.com", 100, "No such host");

        // Assert
        Assert.False(result.Success);
        Assert.Equal("bad.example.com", result.Hostname);
        Assert.Empty(result.ResolvedAddresses);
        Assert.Equal(100, result.ResolutionTimeMs);
        Assert.Equal("No such host", result.ErrorMessage);
    }
}
