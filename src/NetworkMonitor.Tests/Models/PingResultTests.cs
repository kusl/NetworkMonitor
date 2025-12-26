using NetworkMonitor.Core.Models;
using Xunit;

namespace NetworkMonitor.Tests.Models;

/// <summary>
/// Tests for PingResult.
/// </summary>
public sealed class PingResultTests
{
    [Fact]
    public void Succeeded_CreatesSuccessfulResult()
    {
        // Act
        var result = PingResult.Succeeded("8.8.8.8", 15);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("8.8.8.8", result.Target);
        Assert.Equal(15, result.LatencyMs);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void Failed_CreatesFailedResult()
    {
        // Act
        var result = PingResult.Failed("8.8.8.8", "Request timed out");

        // Assert
        Assert.False(result.Success);
        Assert.Equal("8.8.8.8", result.Target);
        Assert.Null(result.LatencyMs);
        Assert.Equal("Request timed out", result.ErrorMessage);
    }

    [Fact]
    public void Timestamp_IsSetToCurrentTime()
    {
        // Arrange
        var before = DateTimeOffset.UtcNow;

        // Act
        var result = PingResult.Succeeded("8.8.8.8", 10);

        // Assert
        var after = DateTimeOffset.UtcNow;
        Assert.True(result.Timestamp >= before);
        Assert.True(result.Timestamp <= after);
    }

    [Fact]
    public void Succeeded_WithZeroLatency_IsValid()
    {
        // Act
        var result = PingResult.Succeeded("localhost", 0);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(0, result.LatencyMs);
    }
}
