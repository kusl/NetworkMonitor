namespace NetworkMonitor.Core.Services;

/// <summary>
/// Provides internet connectivity test targets with fallback support.
/// </summary>
/// <remarks>
/// Not all networks can reach all DNS providers. For example:
/// - Some countries block Google DNS (8.8.8.8)
/// - Some corporate networks only allow specific DNS servers
/// - Some ISPs intercept DNS traffic
/// 
/// This provider allows testing multiple targets and using the first
/// one that responds, ensuring the application works in various
/// network environments.
/// </remarks>
public interface IInternetTargetProvider
{
    /// <summary>
    /// Gets the ordered list of IPv4 internet targets to try.
    /// </summary>
    IReadOnlyList<string> GetTargets();

    /// <summary>
    /// Gets the ordered list of IPv6 internet targets to try.
    /// </summary>
    IReadOnlyList<string> GetIPv6Targets();

    /// <summary>
    /// Gets the primary (preferred) target.
    /// </summary>
    string PrimaryTarget { get; }
}
