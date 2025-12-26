namespace NetworkMonitor.Core.Models;

/// <summary>
/// Event arguments for network status change events.
/// Required for CA1003 compliance (EventHandler should use EventArgs).
/// </summary>
public sealed class NetworkStatusEventArgs : EventArgs
{
    /// <summary>
    /// The new network status.
    /// </summary>
    public NetworkStatus Status { get; }

    /// <summary>
    /// Creates a new instance of NetworkStatusEventArgs.
    /// </summary>
    /// <param name="status">The network status.</param>
    public NetworkStatusEventArgs(NetworkStatus status)
    {
        Status = status;
    }
}
