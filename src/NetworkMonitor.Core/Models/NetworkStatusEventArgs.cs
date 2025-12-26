namespace NetworkMonitor.Core.Models;

/// <summary>
/// Event arguments for network status change events.
/// Required for CA1003 compliance (EventHandler should use EventArgs).
/// </summary>
public sealed class NetworkStatusEventArgs : EventArgs
{
    /// <summary>
    /// The current network status.
    /// </summary>
    public NetworkStatus CurrentStatus { get; }

    /// <summary>
    /// The previous network status (null if this is the first check).
    /// </summary>
    public NetworkStatus? PreviousStatus { get; }

    /// <summary>
    /// Creates a new instance of NetworkStatusEventArgs with current status only.
    /// </summary>
    /// <param name="currentStatus">The current network status.</param>
    public NetworkStatusEventArgs(NetworkStatus currentStatus)
        : this(currentStatus, null)
    {
    }

    /// <summary>
    /// Creates a new instance of NetworkStatusEventArgs with current and previous status.
    /// </summary>
    /// <param name="currentStatus">The current network status.</param>
    /// <param name="previousStatus">The previous network status.</param>
    public NetworkStatusEventArgs(NetworkStatus currentStatus, NetworkStatus? previousStatus)
    {
        CurrentStatus = currentStatus;
        PreviousStatus = previousStatus;
    }

    /// <summary>
    /// Alias for CurrentStatus to maintain backward compatibility.
    /// </summary>
    public NetworkStatus Status => CurrentStatus;
}
