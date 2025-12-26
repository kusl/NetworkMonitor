using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Services;

/// <summary>
/// Abstraction for displaying network status.
/// Allows for different display implementations (console, GUI, etc.).
/// </summary>
public interface IStatusDisplay
{
    /// <summary>
    /// Updates the display with the current network status.
    /// </summary>
    /// <param name="status">Current status to display</param>
    void UpdateStatus(NetworkStatus status);

    /// <summary>
    /// Clears the display.
    /// </summary>
    void Clear();
}
