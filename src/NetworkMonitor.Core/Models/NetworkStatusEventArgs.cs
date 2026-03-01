namespace NetworkMonitor.Core.Models;

/// <summary>
/// Event arguments for network status change events.
/// </summary>
public sealed class NetworkStatusEventArgs : EventArgs
{
    /// <summary>
    /// The current (new) network status.
    /// </summary>
    public NetworkStatus CurrentStatus { get; }

    /// <summary>
    /// The previous network status (null on first check).
    /// </summary>
    public NetworkStatus? PreviousStatus { get; }

    /// <summary>
    /// Convenience property — alias for <see cref="CurrentStatus"/>.
    /// </summary>
    public NetworkStatus Status => CurrentStatus;

    public NetworkStatusEventArgs(NetworkStatus currentStatus, NetworkStatus? previousStatus = null)
    {
        ArgumentNullException.ThrowIfNull(currentStatus);
        CurrentStatus = currentStatus;
        PreviousStatus = previousStatus;
    }
}
