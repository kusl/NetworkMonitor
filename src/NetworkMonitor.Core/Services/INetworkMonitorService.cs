using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Services;

/// <summary>
/// Main service for monitoring network health.
/// Orchestrates ping operations and computes overall status.
/// </summary>
public interface INetworkMonitorService
{
    /// <summary>
    /// Performs a single network health check.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Current network status.</returns>
    Task<NetworkStatus> CheckNetworkAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Event raised when network status changes.
    /// </summary>
    event EventHandler<NetworkStatusEventArgs>? StatusChanged;
}
