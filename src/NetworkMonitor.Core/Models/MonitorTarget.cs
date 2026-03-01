namespace NetworkMonitor.Core.Models;

/// <summary>
/// Represents a target to monitor with its category and enabled state.
/// </summary>
/// <param name="Name">Human-readable name</param>
/// <param name="Address">IP address or hostname</param>
/// <param name="Category">Category of this target</param>
/// <param name="Enabled">Whether this target is currently enabled</param>
public sealed record MonitorTarget(
    string Name,
    string Address,
    TargetCategory Category,
    bool Enabled = true);

/// <summary>
/// Category of a monitoring target.
/// </summary>
public enum TargetCategory
{
    /// <summary>Local network router/gateway.</summary>
    Router,

    /// <summary>Well-known public DNS server (Google, Cloudflare, etc.).</summary>
    PublicDns,

    /// <summary>A named service like Microsoft Teams.</summary>
    Service,

    /// <summary>Custom user-defined target (private IP, hostname).</summary>
    Custom
}
