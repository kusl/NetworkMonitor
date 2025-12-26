using NetworkMonitor.Core.Models;
using Xunit;

namespace NetworkMonitor.Tests;

/// <summary>
/// Tests for PingResult model.
/// </summary>
internal sealed class PingResultTests
{
    [Fact]
    public void Succeeded_CreatesSuccessfulResult()
    {
        // Arrange & Act
        var result = PingResult.Succeeded("192.168.1.1", 42);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("192.168.1.1", result.Target);
        Assert.Equal(42, result.RoundtripTimeMs);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void Failed_CreatesFailedResult()
    {
        // Arrange & Act
        var result = PingResult.Failed("8.8.8.8", "Timeout");

        // Assert
        Assert.False(result.Success);
        Assert.Equal("8.8.8.8", result.Target);
        Assert.Null(result.RoundtripTimeMs);
        Assert.Equal("Timeout", result.ErrorMessage);
    }

    [Fact]
    public void Timestamp_IsSetToUtcNow()
    {
        // Arrange
        var before = DateTimeOffset.UtcNow;

        // Act
        var result = PingResult.Succeeded("test", 10);

        // Assert
        var after = DateTimeOffset.UtcNow;
        Assert.InRange(result.Timestamp, before, after);
    }
}
