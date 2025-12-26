using NetworkMonitor.Core.Models;
using Xunit;

namespace NetworkMonitor.Tests.Models;

/// <summary>
/// Tests for NetworkStatusEventArgs.
/// </summary>
public sealed class NetworkStatusEventArgsTests
{
    private static NetworkStatus CreateTestStatus(NetworkHealth health) =>
        new(health, null, null, DateTimeOffset.UtcNow, "Test");

    [Fact]
    public void Constructor_SingleArg_SetsCurrentStatus()
    {
        // Arrange
        var status = CreateTestStatus(NetworkHealth.Excellent);

        // Act
        var args = new NetworkStatusEventArgs(status);

        // Assert
        Assert.Equal(status, args.CurrentStatus);
        Assert.Null(args.PreviousStatus);
    }

    [Fact]
    public void Constructor_TwoArgs_SetsBothStatuses()
    {
        // Arrange
        var current = CreateTestStatus(NetworkHealth.Excellent);
        var previous = CreateTestStatus(NetworkHealth.Poor);

        // Act
        var args = new NetworkStatusEventArgs(current, previous);

        // Assert
        Assert.Equal(current, args.CurrentStatus);
        Assert.Equal(previous, args.PreviousStatus);
    }

    [Fact]
    public void Status_ReturnsCurrentStatus()
    {
        // Arrange
        var current = CreateTestStatus(NetworkHealth.Good);
        var previous = CreateTestStatus(NetworkHealth.Degraded);
        var args = new NetworkStatusEventArgs(current, previous);

        // Act & Assert
        Assert.Same(args.CurrentStatus, args.Status);
    }

    [Fact]
    public void Constructor_WithNullPrevious_Succeeds()
    {
        // Arrange
        var current = CreateTestStatus(NetworkHealth.Excellent);

        // Act
        var args = new NetworkStatusEventArgs(current, null);

        // Assert
        Assert.Equal(current, args.CurrentStatus);
        Assert.Null(args.PreviousStatus);
    }
}
