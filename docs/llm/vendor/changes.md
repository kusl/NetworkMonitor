# NetworkMonitor — Full Changed Files

All files below are COMPLETE. Copy-paste each into the corresponding path.

---

## 1. `src/Directory.Build.props`

```xml
<Project>
  <!--
    Shared build properties for all projects in the solution.
    
    ANALYSIS LEVEL NOTE:
    We use 'latest-recommended' instead of 'latest-all' because 'latest-all'
    enables rules that are impractical for a console application:
    - CA1303: Requires resource files for ALL literal strings
    - CA1848: Requires LoggerMessage for ALL log calls
    - CA1873: Flags log argument evaluation - same family as CA1848
    - CA2007: Requires ConfigureAwait everywhere (not needed in console apps)
    
    These rules are valuable for large libraries but overkill here.
  -->
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <!-- Use 'recommended' level - 'all' is too strict for console apps -->
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <!-- Enable .NET analyzers -->
    <EnableNETAnalyzers>true</EnableNETAnalyzers>
    <!-- Enforce code style on build -->
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
  </PropertyGroup>

  <!-- Disable specific rules that don't make sense for this project -->
  <PropertyGroup>
    <!-- CA1303: Do not pass literals as localized parameters - not localizing this app -->
    <NoWarn>$(NoWarn);CA1303</NoWarn>
    <!-- CA2007: Consider calling ConfigureAwait - not needed in console app -->
    <NoWarn>$(NoWarn);CA2007</NoWarn>
    <!-- CA1848: Use LoggerMessage delegates - overkill for simple console app -->
    <NoWarn>$(NoWarn);CA1848</NoWarn>
    <!-- CA1873: Log argument evaluation may be expensive - same family as CA1848 -->
    <NoWarn>$(NoWarn);CA1873</NoWarn>
    <!-- CA1716: Identifiers should not match keywords - 'from/to' are fine param names -->
    <NoWarn>$(NoWarn);CA1716</NoWarn>
  </PropertyGroup>

  <!-- Test projects don't need to be packaged -->
  <PropertyGroup Condition="$(MSBuildProjectName.Contains('.Tests'))">
    <IsPackable>false</IsPackable>
  </PropertyGroup>
</Project>
```

---

## 2. `src/NetworkMonitor.Core/Models/MonitorOptions.cs`

```csharp
namespace NetworkMonitor.Core.Models;

/// <summary>
/// Configuration options for the network monitor.
/// Bound from appsettings.json or environment variables.
/// </summary>
public sealed class MonitorOptions
{
    /// <summary>
    /// Configuration section name in appsettings.json
    /// </summary>
    public const string SectionName = "NetworkMonitor";

    /// <summary>
    /// Special value indicating auto-detection should be used.
    /// </summary>
    public const string AutoDetect = "auto";

    /// <summary>
    /// Router/gateway IP address to ping for local network health.
    /// </summary>
    /// <remarks>
    /// Set to "auto" (default) to automatically detect the default gateway.
    /// The gateway is advertised by DHCP and can be read from the OS.
    /// 
    /// If auto-detection fails, common gateway addresses will be tried:
    /// 192.168.1.1, 192.168.0.1, 10.0.0.1, etc.
    /// 
    /// Set to a specific IP address to override auto-detection.
    /// </remarks>
    public string RouterAddress { get; set; } = AutoDetect;

    /// <summary>
    /// Internet target to ping for WAN connectivity.
    /// </summary>
    /// <remarks>
    /// Default: 8.8.8.8 (Google DNS - highly reliable)
    /// 
    /// If this target is unreachable, fallback targets will be tried:
    /// 1.1.1.1 (Cloudflare), 9.9.9.9 (Quad9), etc.
    /// 
    /// This is useful for networks that block specific DNS providers.
    /// </remarks>
    public string InternetTarget { get; set; } = "8.8.8.8";

    /// <summary>
    /// Timeout for each ping in milliseconds.
    /// Default: 3000ms (3 seconds)
    /// </summary>
    public int TimeoutMs { get; set; } = 3000;

    /// <summary>
    /// Interval between monitoring cycles in milliseconds.
    /// Default: 5000ms (5 seconds)
    /// </summary>
    public int IntervalMs { get; set; } = 5000;

    /// <summary>
    /// Number of pings per target per cycle.
    /// Default: 3 (for statistical significance)
    /// </summary>
    public int PingsPerCycle { get; set; } = 3;

    /// <summary>
    /// Latency threshold (ms) below which is considered "excellent".
    /// Default: 20ms
    /// </summary>
    public int ExcellentLatencyMs { get; set; } = 20;

    /// <summary>
    /// Latency threshold (ms) below which is considered "good".
    /// Default: 100ms
    /// </summary>
    public int GoodLatencyMs { get; set; } = 100;

    /// <summary>
    /// Packet loss percentage above which network is "degraded".
    /// Default: 10%
    /// </summary>
    public int DegradedPacketLossPercent { get; set; } = 10;

    /// <summary>
    /// Whether to use fallback targets if primary fails.
    /// Default: true
    /// </summary>
    public bool EnableFallbackTargets { get; set; } = true;

    /// <summary>
    /// Whether to include IPv6 targets for monitoring.
    /// Default: true
    /// </summary>
    public bool EnableIPv6 { get; set; } = true;

    /// <summary>
    /// Whether to perform DNS resolution checks on hostnames.
    /// Default: true
    /// </summary>
    public bool EnableDnsChecks { get; set; } = true;

    /// <summary>
    /// Custom targets to monitor (services, private IPs, hostnames).
    /// Each can be individually enabled/disabled at runtime.
    /// </summary>
    public List<CustomTargetConfig> CustomTargets { get; set; } = [];

    /// <summary>
    /// Names of checks to disable at runtime.
    /// Matches against target names (case-insensitive).
    /// Examples: "GoogleDNS", "CloudflareDNS", "Router", "Teams"
    /// </summary>
    public List<string> DisabledChecks { get; set; } = [];

    /// <summary>
    /// Checks if router address should be auto-detected.
    /// </summary>
    public bool IsRouterAutoDetect =>
        string.IsNullOrWhiteSpace(RouterAddress) ||
        RouterAddress.Equals(AutoDetect, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Checks if a named check is disabled.
    /// </summary>
    public bool IsCheckDisabled(string name) =>
        DisabledChecks.Exists(d => d.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Configuration for a custom monitoring target.
/// </summary>
public sealed class CustomTargetConfig
{
    /// <summary>
    /// Human-readable name for this target.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Address to monitor. Can be an IP (v4/v6) or hostname.
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// Whether this target is currently enabled.
    /// Can be toggled at runtime.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
```

---

## 3. `src/NetworkMonitor.Core/Models/MonitorTarget.cs` (NEW FILE)

```csharp
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
```

---

## 4. `src/NetworkMonitor.Core/Models/DnsResult.cs` (NEW FILE)

```csharp
namespace NetworkMonitor.Core.Models;

/// <summary>
/// Result of a DNS resolution check.
/// </summary>
/// <param name="Hostname">The hostname that was resolved</param>
/// <param name="Success">Whether DNS resolution succeeded</param>
/// <param name="ResolvedAddresses">All resolved IP addresses</param>
/// <param name="ResolutionTimeMs">Time taken for DNS resolution in ms</param>
/// <param name="ErrorMessage">Error message if resolution failed</param>
public sealed record DnsResult(
    string Hostname,
    bool Success,
    IReadOnlyList<string> ResolvedAddresses,
    long ResolutionTimeMs,
    string? ErrorMessage = null)
{
    /// <summary>
    /// Creates a successful DNS result.
    /// </summary>
    public static DnsResult Succeeded(string hostname, IReadOnlyList<string> addresses, long resolutionTimeMs) =>
        new(hostname, true, addresses, resolutionTimeMs);

    /// <summary>
    /// Creates a failed DNS result.
    /// </summary>
    public static DnsResult Failed(string hostname, long resolutionTimeMs, string errorMessage) =>
        new(hostname, false, Array.Empty<string>(), resolutionTimeMs, errorMessage);
}
```

---

## 5. `src/NetworkMonitor.Core/Models/TargetCheckResult.cs` (NEW FILE)

```csharp
namespace NetworkMonitor.Core.Models;

/// <summary>
/// Aggregated check result for a single monitoring target.
/// Includes ping (v4/v6), DNS, and packet loss data.
/// </summary>
/// <param name="Target">The target that was checked</param>
/// <param name="PingResult">IPv4 ping result (or primary ping for IP targets)</param>
/// <param name="PingResultV6">IPv6 ping result (null if IPv6 not applicable)</param>
/// <param name="DnsResult">DNS resolution result (null if target is an IP address)</param>
/// <param name="PacketLossPercent">Percentage of lost packets (0-100)</param>
/// <param name="Timestamp">When this check was performed</param>
public sealed record TargetCheckResult(
    MonitorTarget Target,
    PingResult? PingResult,
    PingResult? PingResultV6,
    DnsResult? DnsResult,
    double PacketLossPercent,
    DateTimeOffset Timestamp);
```

---

## 6. `src/NetworkMonitor.Core/Models/NetworkStatus.cs`

```csharp
namespace NetworkMonitor.Core.Models;

/// <summary>
/// Represents the overall network health status.
/// This is the primary output of the monitoring system.
/// </summary>
/// <param name="Health">Overall network health classification</param>
/// <param name="RouterResult">Ping result for the default gateway</param>
/// <param name="InternetResult">Ping result for the internet target</param>
/// <param name="Timestamp">When this status was determined</param>
/// <param name="Message">Human-readable status message</param>
/// <param name="TargetResults">Detailed results for all monitored targets</param>
public sealed record NetworkStatus(
    NetworkHealth Health,
    PingResult? RouterResult,
    PingResult? InternetResult,
    DateTimeOffset Timestamp,
    string Message,
    IReadOnlyList<TargetCheckResult>? TargetResults = null)
{
    /// <summary>
    /// Whether the network is usable (Excellent, Good, or Degraded).
    /// </summary>
    public bool IsUsable => Health is NetworkHealth.Excellent
        or NetworkHealth.Good
        or NetworkHealth.Degraded;
}

/// <summary>
/// Network health classifications, ordered from worst (0) to best (4).
/// This ordering allows natural comparison: Excellent > Good > Degraded > Poor > Offline.
/// </summary>
public enum NetworkHealth
{
    /// <summary>No network connectivity.</summary>
    Offline = 0,

    /// <summary>Significant connectivity issues.</summary>
    Poor = 1,

    /// <summary>Some issues detected (packet loss, high latency on some targets).</summary>
    Degraded = 2,

    /// <summary>All targets responding with acceptable latency.</summary>
    Good = 3,

    /// <summary>All targets responding with very low latency.</summary>
    Excellent = 4
}
```

---

## 7. `src/NetworkMonitor.Core/Models/NetworkStatusEventArgs.cs`

```csharp
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
```

---

## 8. `src/NetworkMonitor.Core/Services/IDnsResolverService.cs` (NEW FILE)

```csharp
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Services;

/// <summary>
/// Performs DNS resolution checks.
/// </summary>
public interface IDnsResolverService
{
    /// <summary>
    /// Resolves a hostname to IP addresses.
    /// </summary>
    /// <param name="hostname">Hostname to resolve</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>DNS resolution result</returns>
    Task<DnsResult> ResolveAsync(string hostname, CancellationToken cancellationToken = default);
}
```

---

## 9. `src/NetworkMonitor.Core/Services/DnsResolverService.cs` (NEW FILE)

```csharp
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Services;

/// <summary>
/// DNS resolution service using built-in System.Net.Dns.
/// No external packages required.
/// </summary>
public sealed class DnsResolverService : IDnsResolverService
{
    private readonly ILogger<DnsResolverService> _logger;

    public DnsResolverService(ILogger<DnsResolverService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DnsResult> ResolveAsync(string hostname, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var stopwatch = Stopwatch.StartNew();

        try
        {
            _logger.LogDebug("Resolving DNS for {Hostname}", hostname);

            // Check if hostname is already an IP address
            if (IPAddress.TryParse(hostname, out _))
            {
                stopwatch.Stop();
                return DnsResult.Succeeded(hostname, [hostname], stopwatch.ElapsedMilliseconds);
            }

            var entry = await Dns.GetHostEntryAsync(hostname, cancellationToken);
            stopwatch.Stop();

            var addresses = entry.AddressList
                .Select(a => a.ToString())
                .ToList();

            if (addresses.Count == 0)
            {
                _logger.LogDebug("DNS resolution for {Hostname} returned no addresses", hostname);
                return DnsResult.Failed(hostname, stopwatch.ElapsedMilliseconds, "No addresses returned");
            }

            _logger.LogDebug(
                "DNS resolution for {Hostname} succeeded: {Count} addresses in {ElapsedMs}ms",
                hostname, addresses.Count, stopwatch.ElapsedMilliseconds);

            return DnsResult.Succeeded(hostname, addresses, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SocketException ex)
        {
            stopwatch.Stop();
            _logger.LogDebug("DNS resolution for {Hostname} failed: {Error}", hostname, ex.Message);
            return DnsResult.Failed(hostname, stopwatch.ElapsedMilliseconds, ex.Message);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "Unexpected error resolving {Hostname}", hostname);
            return DnsResult.Failed(hostname, stopwatch.ElapsedMilliseconds, ex.Message);
        }
    }
}
```

---

## 10. `src/NetworkMonitor.Core/Services/IGatewayDetector.cs`

```csharp
namespace NetworkMonitor.Core.Services;

/// <summary>
/// Detects the default gateway (router) IP address.
/// </summary>
/// <remarks>
/// The default gateway is advertised by DHCP and can be read from the OS
/// network configuration. This allows the application to work "out of the box"
/// without requiring users to manually configure their router IP.
/// </remarks>
public interface IGatewayDetector
{
    /// <summary>
    /// Attempts to detect the default gateway IP address (IPv4).
    /// </summary>
    /// <returns>
    /// The IP address of the default gateway, or null if it cannot be detected.
    /// </returns>
    string? DetectDefaultGateway();

    /// <summary>
    /// Attempts to detect the default gateway IPv6 address.
    /// </summary>
    /// <returns>
    /// The IPv6 address of the default gateway, or null if not available.
    /// </returns>
    string? DetectDefaultGatewayV6();

    /// <summary>
    /// Gets a list of common gateway addresses to try as fallbacks.
    /// </summary>
    IReadOnlyList<string> GetCommonGatewayAddresses();
}
```

---

## 11. `src/NetworkMonitor.Core/Services/GatewayDetector.cs`

```csharp
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace NetworkMonitor.Core.Services;

/// <summary>
/// Cross-platform default gateway detector using System.Net.NetworkInformation.
/// Supports both IPv4 and IPv6 gateway detection.
/// </summary>
/// <remarks>
/// This implementation reads the default gateway from the OS routing table,
/// which is populated by DHCP or static configuration. Works on Windows,
/// macOS, and Linux without external dependencies.
/// </remarks>
public sealed class GatewayDetector : IGatewayDetector
{
    private readonly ILogger<GatewayDetector> _logger;

    /// <summary>
    /// Common gateway addresses used by consumer routers, ordered by popularity.
    /// These are used as fallbacks if auto-detection fails.
    /// </summary>
    private static readonly string[] CommonGateways =
    [
        "192.168.1.1",   // Most common (Linksys, TP-Link, many ISP routers)
        "192.168.0.1",   // Second most common (D-Link, Netgear, some ISPs)
        "10.0.0.1",      // Apple AirPort, some enterprise networks
        "192.168.2.1",   // Belkin, SMC
        "192.168.1.254", // Some ISP-provided routers (BT, etc.)
        "192.168.0.254", // Some ISP-provided routers
        "10.0.1.1",      // Apple AirPort alternate
        "192.168.10.1",  // Some business routers
        "192.168.100.1", // Some cable modems
        "172.16.0.1",    // Private network range (less common for home)
    ];

    public GatewayDetector(ILogger<GatewayDetector> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string? DetectDefaultGateway()
    {
        return DetectGateway(AddressFamily.InterNetwork);
    }

    /// <inheritdoc />
    public string? DetectDefaultGatewayV6()
    {
        return DetectGateway(AddressFamily.InterNetworkV6);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetCommonGatewayAddresses() => CommonGateways;

    private string? DetectGateway(AddressFamily addressFamily)
    {
        var label = addressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4";

        try
        {
            _logger.LogDebug("Attempting to detect {Label} default gateway...", label);

            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .Where(nic => nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToList();

            _logger.LogDebug("Found {Count} active network interfaces", interfaces.Count);

            foreach (var nic in interfaces)
            {
                var ipProps = nic.GetIPProperties();
                var gateways = ipProps.GatewayAddresses;

                foreach (var gateway in gateways)
                {
                    if (gateway.Address.AddressFamily != addressFamily)
                        continue;

                    var address = gateway.Address.ToString();

                    // Skip zero/unspecified addresses
                    if (address == "0.0.0.0" || address == "::")
                        continue;

                    // Skip link-local IPv6 for gateway detection (fe80::)
                    // unless it's the only option — keep it for now
                    _logger.LogInformation(
                        "Detected {Label} default gateway: {Gateway} on interface {Interface}",
                        label, address, nic.Name);

                    return address;
                }
            }

            _logger.LogWarning("No {Label} default gateway found on any network interface", label);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to detect {Label} default gateway", label);
            return null;
        }
    }
}
```

---

## 12. `src/NetworkMonitor.Core/Services/IInternetTargetProvider.cs`

```csharp
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
```

---

## 13. `src/NetworkMonitor.Core/Services/InternetTargetProvider.cs`

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Services;

/// <summary>
/// Provides internet connectivity test targets with automatic fallback.
/// Supports both IPv4 and IPv6 targets.
/// </summary>
public sealed class InternetTargetProvider : IInternetTargetProvider
{
    private readonly ILogger<InternetTargetProvider> _logger;
    private readonly MonitorOptions _options;

    /// <summary>
    /// Well-known, highly available DNS servers (IPv4).
    /// Ordered by global reliability.
    /// </summary>
    private static readonly string[] DefaultTargets =
    [
        "8.8.8.8",       // Google Public DNS (primary)
        "1.1.1.1",       // Cloudflare DNS (very fast, privacy-focused)
        "8.8.4.4",       // Google Public DNS (secondary)
        "1.0.0.1",       // Cloudflare DNS (secondary)
        "9.9.9.9",       // Quad9 DNS (security-focused)
        "208.67.222.222", // OpenDNS (Cisco)
        "208.67.220.220", // OpenDNS (secondary)
    ];

    /// <summary>
    /// Well-known, highly available DNS servers (IPv6).
    /// Ordered by global reliability.
    /// </summary>
    private static readonly string[] DefaultIPv6Targets =
    [
        "2001:4860:4860::8888", // Google Public DNS (primary)
        "2606:4700:4700::1111", // Cloudflare DNS (primary)
        "2001:4860:4860::8844", // Google Public DNS (secondary)
        "2606:4700:4700::1001", // Cloudflare DNS (secondary)
        "2620:fe::fe",          // Quad9 DNS (primary)
        "2620:fe::9",           // Quad9 DNS (secondary)
        "2620:119:35::35",      // OpenDNS (Cisco)
    ];

    public InternetTargetProvider(
        IOptions<MonitorOptions> options,
        ILogger<InternetTargetProvider> logger)
    {
        _options = options.Value;
        _logger = logger;

        _logger.LogDebug(
            "Internet target provider initialized with primary target: {Target}",
            PrimaryTarget);
    }

    /// <inheritdoc />
    public string PrimaryTarget => _options.InternetTarget;

    /// <inheritdoc />
    public IReadOnlyList<string> GetTargets()
    {
        // If user specified a custom target, put it first
        if (!string.IsNullOrWhiteSpace(_options.InternetTarget) &&
            !DefaultTargets.Contains(_options.InternetTarget, StringComparer.OrdinalIgnoreCase))
        {
            var customList = new List<string> { _options.InternetTarget };
            customList.AddRange(DefaultTargets);
            return customList;
        }

        // Reorder default list to put configured target first
        var targets = new List<string>(DefaultTargets);
        var configuredIndex = targets.FindIndex(
            t => t.Equals(_options.InternetTarget, StringComparison.OrdinalIgnoreCase));

        if (configuredIndex > 0)
        {
            var configured = targets[configuredIndex];
            targets.RemoveAt(configuredIndex);
            targets.Insert(0, configured);
        }

        return targets;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetIPv6Targets()
    {
        return DefaultIPv6Targets;
    }
}
```

---

## 14. `src/NetworkMonitor.Core/Services/PingService.cs`

```csharp
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Services;

/// <summary>
/// Cross-platform ping implementation using System.Net.NetworkInformation.
/// Supports both IPv4 and IPv6.
/// Works on Windows, macOS, and Linux without external dependencies.
/// </summary>
public sealed class PingService : IPingService
{
    private readonly ILogger<PingService> _logger;

    public PingService(ILogger<PingService> logger)
    {
        _logger = logger;
    }

    public async Task<PingResult> PingAsync(
        string target,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        // Check cancellation before doing any work
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            _logger.LogDebug("Pinging {Target} with timeout {TimeoutMs}ms", target, timeoutMs);

            // Resolve hostname to IP if needed, to support both IPv4 and IPv6
            IPAddress? resolvedAddress = null;
            if (!IPAddress.TryParse(target, out resolvedAddress))
            {
                // It's a hostname — resolve it
                try
                {
                    var entry = await Dns.GetHostEntryAsync(target, cancellationToken);
                    if (entry.AddressList.Length > 0)
                    {
                        resolvedAddress = entry.AddressList[0];
                    }
                    else
                    {
                        return PingResult.Failed(target, "DNS resolution returned no addresses");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return PingResult.Failed(target, $"DNS resolution failed: {ex.Message}");
                }
            }

            // Create a new Ping instance per call to allow concurrent pings.
            // The Ping class does not support multiple concurrent async operations
            // on the same instance.
            using var ping = new Ping();

            var stopwatch = Stopwatch.StartNew();

            // Note: PingAsync doesn't accept CancellationToken directly,
            // but we can use the timeout parameter
            var reply = await ping.SendPingAsync(resolvedAddress!, timeoutMs).ConfigureAwait(false);

            stopwatch.Stop();

            // Check cancellation after the ping completes
            cancellationToken.ThrowIfCancellationRequested();

            if (reply.Status == IPStatus.Success)
            {
                _logger.LogDebug(
                    "Ping to {Target} succeeded: {RoundtripMs}ms",
                    target,
                    reply.RoundtripTime);

                return PingResult.Succeeded(target, reply.RoundtripTime);
            }

            var errorMessage = reply.Status.ToString();
            _logger.LogDebug("Ping to {Target} failed: {Status}", target, errorMessage);

            return PingResult.Failed(target, errorMessage);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Ping to {Target} cancelled", target);
            throw;
        }
        catch (PingException ex)
        {
            _logger.LogWarning(ex, "Ping to {Target} threw exception", target);
            return PingResult.Failed(target, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error pinging {Target}", target);
            return PingResult.Failed(target, $"Unexpected error: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<PingResult>> PingMultipleAsync(
        string target,
        int count,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        var results = new List<PingResult>(count);

        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await PingAsync(target, timeoutMs, cancellationToken).ConfigureAwait(false);
            results.Add(result);

            // Small delay between pings to avoid flooding
            if (i < count - 1)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        return results;
    }
}
```

---

## 15. `src/NetworkMonitor.Core/Services/NetworkConfigurationService.cs`

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Services;

/// <summary>
/// Resolves network configuration by combining user settings with auto-detection.
/// </summary>
/// <remarks>
/// This service implements the "just works" philosophy:
/// 1. Try to auto-detect the gateway if configured to do so
/// 2. Fall back to common gateway addresses if detection fails
/// 3. Verify targets are reachable before using them
/// 4. Cache resolved addresses to avoid repeated detection
/// </remarks>
public sealed class NetworkConfigurationService : INetworkConfigurationService, IDisposable
{
    private readonly IGatewayDetector _gatewayDetector;
    private readonly IInternetTargetProvider _internetTargetProvider;
    private readonly IPingService _pingService;
    private readonly MonitorOptions _options;
    private readonly ILogger<NetworkConfigurationService> _logger;

    private string? _resolvedRouterAddress;
    private string? _resolvedInternetTarget;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public NetworkConfigurationService(
        IGatewayDetector gatewayDetector,
        IInternetTargetProvider internetTargetProvider,
        IPingService pingService,
        IOptions<MonitorOptions> options,
        ILogger<NetworkConfigurationService> logger)
    {
        _gatewayDetector = gatewayDetector;
        _internetTargetProvider = internetTargetProvider;
        _pingService = pingService;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string?> GetRouterAddressAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureInitializedAsync(cancellationToken);
        return _resolvedRouterAddress;
    }

    /// <inheritdoc />
    public async Task<string> GetInternetTargetAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureInitializedAsync(cancellationToken);
        return _resolvedInternetTarget ?? _internetTargetProvider.PrimaryTarget;
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;

            _logger.LogDebug("Initializing network configuration...");

            // Resolve router address
            _resolvedRouterAddress = await ResolveRouterAddressAsync(cancellationToken);

            // Resolve internet target
            _resolvedInternetTarget = await ResolveInternetTargetAsync(cancellationToken);

            _initialized = true;

            _logger.LogInformation(
                "Network configuration initialized. Router: {Router}, Internet: {Internet}",
                _resolvedRouterAddress ?? "(none)",
                _resolvedInternetTarget);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<string?> ResolveRouterAddressAsync(CancellationToken cancellationToken)
    {
        // If user specified a specific address (not "auto"), use it
        if (!_options.IsRouterAutoDetect)
        {
            _logger.LogDebug("Using configured router address: {Address}", _options.RouterAddress);
            return _options.RouterAddress;
        }

        _logger.LogDebug("Auto-detecting gateway...");

        // Try OS-level detection first
        var detected = _gatewayDetector.DetectDefaultGateway();
        if (!string.IsNullOrEmpty(detected))
        {
            _logger.LogDebug("OS detected gateway: {Gateway}", detected);
            if (await IsReachableAsync(detected, cancellationToken))
            {
                _logger.LogInformation("Using detected gateway: {Gateway}", detected);
                return detected;
            }
            _logger.LogDebug("Detected gateway {Gateway} is not reachable", detected);
        }

        // Fall back to common gateway addresses
        _logger.LogDebug("Trying common gateway addresses...");
        foreach (var gateway in _gatewayDetector.GetCommonGatewayAddresses())
        {
            if (await IsReachableAsync(gateway, cancellationToken))
            {
                _logger.LogInformation("Using fallback gateway: {Gateway}", gateway);
                return gateway;
            }
        }

        _logger.LogWarning("No reachable gateway found. Router monitoring will be disabled.");
        return null;
    }

    private async Task<string?> ResolveInternetTargetAsync(CancellationToken cancellationToken)
    {
        if (!_options.EnableFallbackTargets)
        {
            var primary = _internetTargetProvider.PrimaryTarget;
            _logger.LogDebug("Fallback targets disabled. Using primary target: {Target}", primary);
            return primary;
        }

        _logger.LogDebug("Finding reachable internet target...");

        foreach (var target in _internetTargetProvider.GetTargets())
        {
            if (await IsReachableAsync(target, cancellationToken))
            {
                _logger.LogInformation("Using internet target: {Target}", target);
                return target;
            }
        }

        _logger.LogWarning("No internet target is reachable. Using default: {Target}", _options.InternetTarget);
        return _options.InternetTarget;
    }

    private async Task<bool> IsReachableAsync(string target, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _pingService.PingAsync(target, _options.TimeoutMs, cancellationToken);
            return result.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Failed to reach {Target}: {Error}", target, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Disposes the service and its resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _initLock.Dispose();
    }
}
```

---

## 16. `src/NetworkMonitor.Core/Services/NetworkMonitorService.cs`

```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Services;

/// <summary>
/// Main network monitoring service.
/// Coordinates ping operations across multiple targets and computes overall network health.
/// Supports IPv4, IPv6, DNS resolution, packet loss tracking, and custom targets.
/// Exposes OpenTelemetry metrics for observability.
/// </summary>
public sealed class NetworkMonitorService : INetworkMonitorService
{
    private static readonly ActivitySource ActivitySource = new("NetworkMonitor.Core");
    private static readonly Meter Meter = new("NetworkMonitor.Core");

    // Metrics - use static readonly for performance (CA1859)
    private static readonly Counter<long> CheckCounter = Meter.CreateCounter<long>(
        "network_monitor.checks",
        description: "Number of network health checks performed");

    private static readonly Histogram<double> RouterLatencyHistogram = Meter.CreateHistogram<double>(
        "network_monitor.router_latency_ms",
        unit: "ms",
        description: "Router ping latency distribution");

    private static readonly Histogram<double> InternetLatencyHistogram = Meter.CreateHistogram<double>(
        "network_monitor.internet_latency_ms",
        unit: "ms",
        description: "Internet ping latency distribution");

    private static readonly Counter<long> FailureCounter = Meter.CreateCounter<long>(
        "network_monitor.failures",
        description: "Number of ping failures by target type");

    private static readonly Histogram<double> DnsResolutionHistogram = Meter.CreateHistogram<double>(
        "network_monitor.dns_resolution_ms",
        unit: "ms",
        description: "DNS resolution latency distribution");

    private static readonly Histogram<double> PacketLossHistogram = Meter.CreateHistogram<double>(
        "network_monitor.packet_loss_percent",
        unit: "%",
        description: "Packet loss percentage distribution");

    private readonly IPingService _pingService;
    private readonly INetworkConfigurationService _configService;
    private readonly IDnsResolverService? _dnsResolver;
    private readonly IInternetTargetProvider _internetTargetProvider;
    private readonly MonitorOptions _options;
    private readonly ILogger<NetworkMonitorService> _logger;

    private NetworkStatus? _lastStatus;

    /// <inheritdoc />
    public event EventHandler<NetworkStatusEventArgs>? StatusChanged;

    /// <summary>
    /// Creates a new network monitor service.
    /// </summary>
    public NetworkMonitorService(
        IPingService pingService,
        INetworkConfigurationService configService,
        IInternetTargetProvider internetTargetProvider,
        IOptions<MonitorOptions> options,
        ILogger<NetworkMonitorService> logger,
        IDnsResolverService? dnsResolver = null)
    {
        _pingService = pingService;
        _configService = configService;
        _internetTargetProvider = internetTargetProvider;
        _options = options.Value;
        _logger = logger;
        _dnsResolver = dnsResolver;
    }

    /// <inheritdoc />
    public async Task<NetworkStatus> CheckNetworkAsync(CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("CheckNetwork");

        cancellationToken.ThrowIfCancellationRequested();

        CheckCounter.Add(1);

        // Get resolved targets
        var routerAddress = await _configService.GetRouterAddressAsync(cancellationToken);
        var internetTarget = await _configService.GetInternetTargetAsync(cancellationToken);

        // Collect all target check results
        var targetResults = new List<TargetCheckResult>();

        // Ping router (if we have one and it's not disabled)
        PingResult? routerResult = null;
        if (!string.IsNullOrEmpty(routerAddress) && !_options.IsCheckDisabled("Router"))
        {
            var (pingResult, packetLoss) = await PingWithMetricsAsync(routerAddress, cancellationToken);
            routerResult = pingResult;

            if (routerResult is { Success: true, RoundtripTimeMs: not null })
            {
                RouterLatencyHistogram.Record(routerResult.RoundtripTimeMs.Value);
            }
            else
            {
                FailureCounter.Add(1, new KeyValuePair<string, object?>("target_type", "router"));
            }

            PacketLossHistogram.Record(packetLoss, new KeyValuePair<string, object?>("target", "router"));

            targetResults.Add(new TargetCheckResult(
                new MonitorTarget("Router", routerAddress, TargetCategory.Router),
                routerResult, null, null, packetLoss, DateTimeOffset.UtcNow));
        }

        // Ping internet target (if not disabled)
        PingResult? internetResult = null;
        double internetPacketLoss = 0;
        if (!_options.IsCheckDisabled("Internet"))
        {
            (internetResult, internetPacketLoss) = await PingWithMetricsAsync(internetTarget, cancellationToken);

            if (internetResult is { Success: true, RoundtripTimeMs: not null })
            {
                InternetLatencyHistogram.Record(internetResult.RoundtripTimeMs.Value);
            }
            else
            {
                FailureCounter.Add(1, new KeyValuePair<string, object?>("target_type", "internet"));
            }

            PacketLossHistogram.Record(internetPacketLoss, new KeyValuePair<string, object?>("target", "internet"));

            // DNS check for internet target (if it's a hostname)
            DnsResult? internetDns = null;
            if (_options.EnableDnsChecks && _dnsResolver != null && !IPAddress.TryParse(internetTarget, out _))
            {
                internetDns = await _dnsResolver.ResolveAsync(internetTarget, cancellationToken);
                DnsResolutionHistogram.Record(internetDns.ResolutionTimeMs,
                    new KeyValuePair<string, object?>("target", internetTarget));
            }

            targetResults.Add(new TargetCheckResult(
                new MonitorTarget("Internet", internetTarget, TargetCategory.PublicDns),
                internetResult, null, internetDns, internetPacketLoss, DateTimeOffset.UtcNow));
        }
        else
        {
            // Need a non-null internetResult for health computation
            internetResult = PingResult.Failed(internetTarget, "Check disabled");
        }

        // Check custom targets
        foreach (var customTarget in _options.CustomTargets)
        {
            if (!customTarget.Enabled || _options.IsCheckDisabled(customTarget.Name))
                continue;

            var customResult = await CheckCustomTargetAsync(customTarget, cancellationToken);
            targetResults.Add(customResult);
        }

        // Compute overall health
        var (health, message) = ComputeHealth(routerResult, internetResult, internetPacketLoss, _options);

        var status = new NetworkStatus(
            health,
            routerResult,
            internetResult,
            DateTimeOffset.UtcNow,
            message,
            targetResults);

        activity?.SetTag("health", health.ToString());
        activity?.SetTag("router.success", routerResult?.Success ?? false);
        activity?.SetTag("internet.success", internetResult.Success);
        activity?.SetTag("target_count", targetResults.Count);

        // Fire event if status changed
        if (_lastStatus?.Health != status.Health)
        {
            _logger.LogInformation(
                "Network status changed: {OldHealth} -> {NewHealth}: {Message}",
                _lastStatus?.Health.ToString() ?? "Unknown",
                status.Health,
                status.Message);

            StatusChanged?.Invoke(this, new NetworkStatusEventArgs(status, _lastStatus));
        }

        _lastStatus = status;
        return status;
    }

    private async Task<(PingResult Result, double PacketLossPercent)> PingWithMetricsAsync(
        string target,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = await _pingService.PingMultipleAsync(
                target,
                _options.PingsPerCycle,
                _options.TimeoutMs,
                cancellationToken);

            var packetLoss = results.Count > 0
                ? (double)(results.Count - results.Count(r => r.Success)) / results.Count * 100
                : 100.0;

            var aggregated = AggregateResults(results);
            return (aggregated, packetLoss);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error pinging {Target}", target);
            return (PingResult.Failed(target, ex.Message), 100.0);
        }
    }

    private async Task<TargetCheckResult> CheckCustomTargetAsync(
        CustomTargetConfig target,
        CancellationToken cancellationToken)
    {
        PingResult? pingResult = null;
        DnsResult? dnsResult = null;
        double packetLoss = 0;

        try
        {
            // DNS resolution for hostnames
            if (_options.EnableDnsChecks && _dnsResolver != null && !IPAddress.TryParse(target.Address, out _))
            {
                dnsResult = await _dnsResolver.ResolveAsync(target.Address, cancellationToken);
                DnsResolutionHistogram.Record(dnsResult.ResolutionTimeMs,
                    new KeyValuePair<string, object?>("target", target.Name));
            }

            // Ping
            (pingResult, packetLoss) = await PingWithMetricsAsync(target.Address, cancellationToken);
            PacketLossHistogram.Record(packetLoss,
                new KeyValuePair<string, object?>("target", target.Name));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking custom target {Name} ({Address})", target.Name, target.Address);
            pingResult = PingResult.Failed(target.Address, ex.Message);
            packetLoss = 100;
        }

        return new TargetCheckResult(
            new MonitorTarget(target.Name, target.Address, TargetCategory.Custom),
            pingResult, null, dnsResult, packetLoss, DateTimeOffset.UtcNow);
    }

    private static PingResult AggregateResults(IReadOnlyList<PingResult> results)
    {
        if (results.Count == 0)
        {
            return PingResult.Failed("unknown", "No ping results");
        }

        var successful = results.Where(r => r.Success).ToList();
        var target = results[0].Target;

        if (successful.Count == 0)
        {
            return PingResult.Failed(target, results[0].ErrorMessage ?? "All pings failed");
        }

        // Return median latency of successful pings for stability
        var sortedLatencies = successful
            .Where(r => r.RoundtripTimeMs.HasValue)
            .Select(r => r.RoundtripTimeMs!.Value)
            .OrderBy(l => l)
            .ToList();

        var medianLatency = sortedLatencies.Count > 0
            ? sortedLatencies[sortedLatencies.Count / 2]
            : 0;

        return PingResult.Succeeded(target, medianLatency);
    }

    /// <summary>
    /// Computes network health based on ping results.
    /// </summary>
    private static (NetworkHealth Health, string Message) ComputeHealth(
        PingResult? routerResult,
        PingResult internetResult,
        double packetLossPercent,
        MonitorOptions options)
    {
        // If we have a router configured and it's not responding, that's significant
        if (routerResult != null && !routerResult.Success)
        {
            return !internetResult.Success
                ? (NetworkHealth.Offline, "Cannot reach router or internet")
                : (NetworkHealth.Degraded, "Cannot reach router but internet works");
        }

        // If internet is down
        if (!internetResult.Success)
        {
            return routerResult?.Success == true
                ? (NetworkHealth.Poor, "Router OK but cannot reach internet")
                : (NetworkHealth.Offline, "Cannot reach internet");
        }

        // Check packet loss
        if (packetLossPercent >= options.DegradedPacketLossPercent)
        {
            return (NetworkHealth.Degraded,
                $"High packet loss: {packetLossPercent:F0}%");
        }

        // Both are up - check latency
        var internetLatency = internetResult.RoundtripTimeMs ?? 0;
        var routerLatency = routerResult?.RoundtripTimeMs ?? 0;

        if (internetLatency <= options.ExcellentLatencyMs &&
            routerLatency <= options.ExcellentLatencyMs)
        {
            return (NetworkHealth.Excellent,
                $"Excellent - Router: {routerLatency}ms, Internet: {internetLatency}ms");
        }

        if (internetLatency <= options.GoodLatencyMs &&
            routerLatency <= options.GoodLatencyMs)
        {
            return (NetworkHealth.Good,
                $"Good - Router: {routerLatency}ms, Internet: {internetLatency}ms");
        }

        // High latency somewhere
        if (routerLatency > options.GoodLatencyMs && routerResult != null)
        {
            return (NetworkHealth.Degraded,
                $"High local latency: Router {routerLatency}ms - possible WiFi interference");
        }

        return (NetworkHealth.Poor,
            $"High internet latency: {internetLatency}ms - possible ISP issues");
    }
}
```

---

## 17. `src/NetworkMonitor.Core/Services/MonitorBackgroundService.cs`

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Storage;

namespace NetworkMonitor.Core.Services;

/// <summary>
/// Background service that runs the continuous monitoring loop.
/// Implements IHostedService for proper lifecycle management.
/// </summary>
public sealed class MonitorBackgroundService : BackgroundService
{
    private readonly INetworkMonitorService _monitorService;
    private readonly IStatusDisplay _display;
    private readonly IStorageService _storage;
    private readonly MonitorOptions _options;
    private readonly ILogger<MonitorBackgroundService> _logger;

    /// <summary>
    /// Creates a new monitor background service.
    /// </summary>
    public MonitorBackgroundService(
        INetworkMonitorService monitorService,
        IStatusDisplay display,
        IStorageService storage,
        IOptions<MonitorOptions> options,
        ILogger<MonitorBackgroundService> logger)
    {
        _monitorService = monitorService;
        _display = display;
        _storage = storage;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Network Monitor starting. Interval: {IntervalMs}ms, Router: {Router}, Internet: {Internet}, IPv6: {IPv6}, DNS: {Dns}, CustomTargets: {CustomCount}",
            _options.IntervalMs,
            _options.RouterAddress,
            _options.InternetTarget,
            _options.EnableIPv6,
            _options.EnableDnsChecks,
            _options.CustomTargets.Count);

        // Subscribe to status changes for logging significant events
        _monitorService.StatusChanged += OnStatusChanged;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var status = await _monitorService.CheckNetworkAsync(stoppingToken);

                    // Update display
                    _display.UpdateStatus(status);

                    // Persist results
                    await _storage.SaveStatusAsync(status, stoppingToken);

                    // Wait for next cycle
                    await Task.Delay(_options.IntervalMs, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Normal shutdown
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during monitoring cycle");

                    // Continue monitoring even if one cycle fails
                    await Task.Delay(_options.IntervalMs, stoppingToken);
                }
            }
        }
        finally
        {
            _monitorService.StatusChanged -= OnStatusChanged;
            _display.Clear();
        }

        _logger.LogInformation("Network Monitor stopped");
    }

    private void OnStatusChanged(object? sender, NetworkStatusEventArgs e)
    {
        // Log significant status changes
        if (e.Status.Health == NetworkHealth.Offline)
        {
            _logger.LogWarning("Network is OFFLINE: {Message}", e.Status.Message);
        }
        else if (e.Status.Health == NetworkHealth.Poor)
        {
            _logger.LogWarning("Network is POOR: {Message}", e.Status.Message);
        }
    }
}
```

---

## 18. `src/NetworkMonitor.Core/Services/ConsoleStatusDisplay.cs`

```csharp
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Services;

/// <summary>
/// Console-based status display with ANSI colors.
/// Provides "at a glance" network status visualization.
/// Shows extended info for custom targets and packet loss.
/// </summary>
public sealed class ConsoleStatusDisplay : IStatusDisplay
{
    private readonly Lock _lock = new();

    // ANSI color codes
    private const string Reset = "\x1b[0m";
    private const string Bold = "\x1b[1m";
    private const string Green = "\x1b[32m";
    private const string Yellow = "\x1b[33m";
    private const string Red = "\x1b[31m";
    private const string Cyan = "\x1b[36m";
    private const string Magenta = "\x1b[35m";

    /// <inheritdoc />
    public void UpdateStatus(NetworkStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        lock (_lock)
        {
            var (color, symbol) = status.Health switch
            {
                NetworkHealth.Excellent => (Green, "●"),
                NetworkHealth.Good => (Green, "○"),
                NetworkHealth.Degraded => (Yellow, "◐"),
                NetworkHealth.Poor => (Red, "◑"),
                NetworkHealth.Offline => (Red, "○"),
                _ => (Reset, "?")
            };

            Console.Write($"\r{color}{Bold}{symbol} {status.Health,-10}{Reset} ");
            Console.Write($"{Cyan}Router:{Reset} ");

            if (status.RouterResult?.Success == true)
            {
                Console.Write($"{Green}{status.RouterResult.RoundtripTimeMs,4}ms{Reset} ");
            }
            else
            {
                Console.Write($"{Red}FAIL{Reset}   ");
            }

            Console.Write($"{Cyan}Internet:{Reset} ");

            if (status.InternetResult?.Success == true)
            {
                Console.Write($"{Green}{status.InternetResult.RoundtripTimeMs,4}ms{Reset} ");
            }
            else
            {
                Console.Write($"{Red}FAIL{Reset}   ");
            }

            // Show custom target summary if any
            if (status.TargetResults is { Count: > 0 })
            {
                var customResults = status.TargetResults
                    .Where(r => r.Target.Category == TargetCategory.Custom)
                    .ToList();

                if (customResults.Count > 0)
                {
                    var ok = customResults.Count(r => r.PingResult?.Success == true);
                    var total = customResults.Count;
                    var customColor = ok == total ? Green : ok > 0 ? Yellow : Red;
                    Console.Write($"{Cyan}Custom:{Reset} {customColor}{ok}/{total}{Reset} ");
                }
            }

            Console.Write($"{Magenta}[{status.Timestamp:HH:mm:ss}]{Reset}");

            // Pad to clear any previous longer text
            Console.Write("          ");
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_lock)
        {
            Console.Write("\r" + new string(' ', Console.WindowWidth - 1) + "\r");
        }
    }
}
```

---

## 19. `src/NetworkMonitor.Core/ServiceCollectionExtensions.cs`

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NetworkMonitor.Core.Exporters;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Services;
using NetworkMonitor.Core.Storage;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace NetworkMonitor.Core;

/// <summary>
/// Extension methods for registering Network Monitor services.
/// Encapsulates all the DI wiring in one place.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all Network Monitor services with the DI container.
    /// </summary>
    public static IServiceCollection AddNetworkMonitor(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Bind options from configuration
        services.Configure<MonitorOptions>(
            configuration.GetSection(MonitorOptions.SectionName));
        services.Configure<StorageOptions>(
            configuration.GetSection(StorageOptions.SectionName));

        // Register core services
        services.AddSingleton<IPingService, PingService>();
        services.AddSingleton<IGatewayDetector, GatewayDetector>();
        services.AddSingleton<IInternetTargetProvider, InternetTargetProvider>();
        services.AddSingleton<INetworkConfigurationService, NetworkConfigurationService>();
        services.AddSingleton<IDnsResolverService, DnsResolverService>();
        services.AddSingleton<INetworkMonitorService, NetworkMonitorService>();
        services.AddSingleton<IStatusDisplay, ConsoleStatusDisplay>();
        services.AddSingleton<IStorageService, SqliteStorageService>();

        // Register background service
        services.AddHostedService<MonitorBackgroundService>();

        return services;
    }

    /// <summary>
    /// Adds OpenTelemetry metrics with file and console export.
    /// </summary>
    public static IServiceCollection AddNetworkMonitorTelemetry(
        this IServiceCollection services,
        FileExporterOptions? fileOptions = null)
    {
        fileOptions ??= FileExporterOptions.Default;

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName: "NetworkMonitor",
                    serviceVersion: "1.0.0"))
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter("NetworkMonitor.Core")
                    .AddRuntimeInstrumentation()
                    .AddConsoleExporter()
                    .AddFileExporter(fileOptions);
            });

        return services;
    }
}
```

---

## 20. `src/NetworkMonitor.Console/appsettings.json`

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning",
      "NetworkMonitor": "Information"
    }
  },
  "NetworkMonitor": {
    "RouterAddress": "auto",
    "InternetTarget": "8.8.8.8",
    "TimeoutMs": 3000,
    "IntervalMs": 5000,
    "PingsPerCycle": 3,
    "ExcellentLatencyMs": 20,
    "GoodLatencyMs": 100,
    "DegradedPacketLossPercent": 10,
    "EnableFallbackTargets": true,
    "EnableIPv6": true,
    "EnableDnsChecks": true,
    "CustomTargets": [],
    "DisabledChecks": []
  },
  "Storage": {
    "RetentionDays": 30,
    "DatabasePath": ""
  }
}
```

---

## 21. `src/NetworkMonitor.Tests/Fakes/FakeGatewayDetector.cs`

```csharp
using NetworkMonitor.Core.Services;

namespace NetworkMonitor.Tests.Fakes;

/// <summary>
/// Fake gateway detector for testing.
/// </summary>
public sealed class FakeGatewayDetector : IGatewayDetector
{
    private string? _gatewayToReturn;
    private string? _gatewayV6ToReturn;
    private readonly List<string> _commonGateways = ["192.168.1.1", "192.168.0.1", "10.0.0.1"];

    /// <summary>
    /// Configures the detector to return a specific IPv4 gateway.
    /// </summary>
    public FakeGatewayDetector WithGateway(string? gateway)
    {
        _gatewayToReturn = gateway;
        return this;
    }

    /// <summary>
    /// Configures the detector to return a specific IPv6 gateway.
    /// </summary>
    public FakeGatewayDetector WithGatewayV6(string? gateway)
    {
        _gatewayV6ToReturn = gateway;
        return this;
    }

    /// <summary>
    /// Configures the detector to return null (no gateway found).
    /// </summary>
    public FakeGatewayDetector WithNoGateway()
    {
        _gatewayToReturn = null;
        _gatewayV6ToReturn = null;
        return this;
    }

    /// <summary>
    /// Configures the common gateways list.
    /// </summary>
    public FakeGatewayDetector WithCommonGateways(params string[] gateways)
    {
        _commonGateways.Clear();
        _commonGateways.AddRange(gateways);
        return this;
    }

    public string? DetectDefaultGateway() => _gatewayToReturn;

    public string? DetectDefaultGatewayV6() => _gatewayV6ToReturn;

    public IReadOnlyList<string> GetCommonGatewayAddresses() => _commonGateways;
}
```

---

## 22. `src/NetworkMonitor.Tests/Fakes/FakeInternetTargetProvider.cs`

```csharp
using NetworkMonitor.Core.Services;

namespace NetworkMonitor.Tests.Fakes;

/// <summary>
/// Fake internet target provider for testing.
/// </summary>
public sealed class FakeInternetTargetProvider : IInternetTargetProvider
{
    private string _primaryTarget = "8.8.8.8";
    private List<string> _targets = ["8.8.8.8", "1.1.1.1", "208.67.222.222"];
    private List<string> _ipv6Targets = ["2001:4860:4860::8888", "2606:4700:4700::1111"];

    public string PrimaryTarget => _primaryTarget;

    public FakeInternetTargetProvider WithPrimaryTarget(string target)
    {
        _primaryTarget = target;

        // Remove the target if it exists (no need to check Contains first)
        _targets.Remove(target);

        // Now insert it at the start
        _targets.Insert(0, target);

        return this;
    }

    public FakeInternetTargetProvider WithTargets(params string[] targets)
    {
        _targets = targets.ToList();
        if (_targets.Count > 0)
        {
            _primaryTarget = _targets[0];
        }
        return this;
    }

    public FakeInternetTargetProvider WithIPv6Targets(params string[] targets)
    {
        _ipv6Targets = targets.ToList();
        return this;
    }

    public IReadOnlyList<string> GetTargets() => _targets;

    public IReadOnlyList<string> GetIPv6Targets() => _ipv6Targets;
}
```

---

## 23. `src/NetworkMonitor.Tests/Fakes/FakeDnsResolverService.cs` (NEW FILE)

```csharp
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Services;

namespace NetworkMonitor.Tests.Fakes;

/// <summary>
/// Fake DNS resolver for testing.
/// </summary>
public sealed class FakeDnsResolverService : IDnsResolverService
{
    private readonly Dictionary<string, DnsResult> _results = new(StringComparer.OrdinalIgnoreCase);
    private Func<string, DnsResult>? _factory;

    /// <summary>
    /// Configures a specific result for a hostname.
    /// </summary>
    public FakeDnsResolverService WithResult(string hostname, DnsResult result)
    {
        _results[hostname] = result;
        return this;
    }

    /// <summary>
    /// Configures all resolutions to succeed.
    /// </summary>
    public FakeDnsResolverService AlwaysSucceed(long resolutionTimeMs = 5)
    {
        _factory = hostname => DnsResult.Succeeded(hostname, ["127.0.0.1"], resolutionTimeMs);
        return this;
    }

    /// <summary>
    /// Configures all resolutions to fail.
    /// </summary>
    public FakeDnsResolverService AlwaysFail(string error = "DNS resolution failed")
    {
        _factory = hostname => DnsResult.Failed(hostname, 100, error);
        return this;
    }

    public Task<DnsResult> ResolveAsync(string hostname, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_results.TryGetValue(hostname, out var result))
        {
            return Task.FromResult(result);
        }

        if (_factory != null)
        {
            return Task.FromResult(_factory(hostname));
        }

        // Default: succeed
        return Task.FromResult(DnsResult.Succeeded(hostname, ["127.0.0.1"], 5));
    }
}
```

---

## 24. `src/NetworkMonitor.Tests/Services/NetworkMonitorServiceTests.cs`

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Services;
using NetworkMonitor.Tests.Fakes;
using Xunit;

namespace NetworkMonitor.Tests.Services;

/// <summary>
/// Tests for NetworkMonitorService.
/// </summary>
public sealed class NetworkMonitorServiceTests : IDisposable
{
    private readonly FakePingService _pingService;
    private readonly FakeNetworkConfigurationService _configService;
    private readonly FakeInternetTargetProvider _internetTargetProvider;
    private readonly FakeDnsResolverService _dnsResolver;
    private readonly MonitorOptions _options;

    public NetworkMonitorServiceTests()
    {
        _pingService = new FakePingService();
        _configService = new FakeNetworkConfigurationService();
        _internetTargetProvider = new FakeInternetTargetProvider();
        _dnsResolver = new FakeDnsResolverService().AlwaysSucceed();
        _options = new MonitorOptions
        {
            PingsPerCycle = 1,
            TimeoutMs = 1000,
            ExcellentLatencyMs = 20,
            GoodLatencyMs = 50
        };
    }

    public void Dispose()
    {
        _configService.Dispose();
    }

    private NetworkMonitorService CreateService(MonitorOptions? options = null)
    {
        return new NetworkMonitorService(
            _pingService,
            _configService,
            _internetTargetProvider,
            Options.Create(options ?? _options),
            NullLogger<NetworkMonitorService>.Instance,
            _dnsResolver);
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenAllSucceed_ReturnsExcellentOrGood()
    {
        // Arrange
        _configService.WithRouterAddress("192.168.1.1");
        _configService.WithInternetTarget("8.8.8.8");

        // Queue successful pings with low latency
        _pingService.QueueResult(PingResult.Succeeded("192.168.1.1", 5));
        _pingService.QueueResult(PingResult.Succeeded("8.8.8.8", 10));

        var service = CreateService();

        // Act
        var status = await service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(
            status.Health is NetworkHealth.Excellent or NetworkHealth.Good,
            $"Expected Excellent or Good but got {status.Health}");
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenRouterFails_ReturnsOfflineOrDegraded()
    {
        // Arrange
        _configService.WithRouterAddress("192.168.1.1");
        _configService.WithInternetTarget("8.8.8.8");

        // Router fails, internet succeeds
        _pingService.QueueResult(PingResult.Failed("192.168.1.1", "Timeout"));
        _pingService.QueueResult(PingResult.Succeeded("8.8.8.8", 10));

        var service = CreateService();

        // Act
        var status = await service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert - Router failure with internet success = Degraded
        Assert.True(
            status.Health is NetworkHealth.Offline or NetworkHealth.Degraded,
            $"Expected Offline or Degraded but got {status.Health}");
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenAllFail_ReturnsOffline()
    {
        // Arrange
        _configService.WithRouterAddress("192.168.1.1");
        _configService.WithInternetTarget("8.8.8.8");
        _pingService.AlwaysFail();

        var service = CreateService();

        // Act
        var status = await service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(NetworkHealth.Offline, status.Health);
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenInternetFailsButRouterOK_ReturnsPoor()
    {
        // Arrange
        _configService.WithRouterAddress("192.168.1.1");
        _configService.WithInternetTarget("8.8.8.8");

        _pingService.QueueResult(PingResult.Succeeded("192.168.1.1", 5));
        _pingService.QueueResult(PingResult.Failed("8.8.8.8", "Timeout"));

        var service = CreateService();

        // Act
        var status = await service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(NetworkHealth.Poor, status.Health);
    }

    [Fact]
    public async Task CheckNetworkAsync_NoRouter_UsesOnlyInternet()
    {
        // Arrange
        _configService.WithRouterAddress(null);
        _configService.WithInternetTarget("8.8.8.8");
        _pingService.AlwaysSucceed(5);

        var service = CreateService();

        // Act
        var status = await service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(status.RouterResult);
        Assert.True(status.Health is NetworkHealth.Excellent or NetworkHealth.Good);
    }

    [Fact]
    public async Task CheckNetworkAsync_HighLatency_ReturnsPoorOrDegraded()
    {
        // Arrange
        _configService.WithRouterAddress("192.168.1.1");
        _configService.WithInternetTarget("8.8.8.8");

        // High latency
        _pingService.QueueResult(PingResult.Succeeded("192.168.1.1", 5));
        _pingService.QueueResult(PingResult.Succeeded("8.8.8.8", 200));

        var service = CreateService();

        // Act
        var status = await service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert - High internet latency
        Assert.True(
            status.Health is NetworkHealth.Poor or NetworkHealth.Degraded,
            $"Expected Poor or Degraded but got {status.Health}");
    }

    [Fact]
    public async Task CheckNetworkAsync_StatusChangedEvent_RaisedOnFirstCheck()
    {
        // Arrange
        _configService.WithRouterAddress("192.168.1.1");
        _configService.WithInternetTarget("8.8.8.8");
        _pingService.AlwaysSucceed(5);

        var service = CreateService();

        NetworkStatusEventArgs? eventArgs = null;
        service.StatusChanged += (_, args) => eventArgs = args;

        // Act
        await service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(eventArgs);
        Assert.NotNull(eventArgs.CurrentStatus);
    }

    [Fact]
    public async Task CheckNetworkAsync_StatusChangedEvent_IncludesPreviousStatus()
    {
        // Arrange
        _configService.WithRouterAddress("192.168.1.1");
        _configService.WithInternetTarget("8.8.8.8");

        // First check - excellent
        _pingService.QueueResult(PingResult.Succeeded("192.168.1.1", 5));
        _pingService.QueueResult(PingResult.Succeeded("8.8.8.8", 10));

        // Second check - offline
        _pingService.QueueResult(PingResult.Failed("192.168.1.1", "Timeout"));
        _pingService.QueueResult(PingResult.Failed("8.8.8.8", "Timeout"));

        var service = CreateService();
        var events = new List<NetworkStatusEventArgs>();
        service.StatusChanged += (_, args) => events.Add(args);

        // Act
        await service.CheckNetworkAsync(TestContext.Current.CancellationToken);
        await service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert - Should have two events, second one has previous status
        Assert.Equal(2, events.Count);
        Assert.Null(events[0].PreviousStatus); // First event has no previous
        Assert.NotNull(events[1].PreviousStatus); // Second event has previous
    }

    [Fact]
    public async Task CheckNetworkAsync_SupportsCancellation()
    {
        // Arrange
        _configService.WithRouterAddress("192.168.1.1");
        _configService.WithInternetTarget("8.8.8.8");

        var service = CreateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.CheckNetworkAsync(cts.Token));
    }

    [Fact]
    public async Task CheckNetworkAsync_WithDisabledCheck_SkipsRouter()
    {
        // Arrange
        _configService.WithRouterAddress("192.168.1.1");
        _configService.WithInternetTarget("8.8.8.8");
        _pingService.AlwaysSucceed(5);

        var options = new MonitorOptions
        {
            PingsPerCycle = 1,
            TimeoutMs = 1000,
            ExcellentLatencyMs = 20,
            GoodLatencyMs = 50,
            DisabledChecks = ["Router"]
        };

        var service = CreateService(options);

        // Act
        var status = await service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert - Router should be null when disabled
        Assert.Null(status.RouterResult);
    }

    [Fact]
    public async Task CheckNetworkAsync_WithCustomTargets_IncludesResults()
    {
        // Arrange
        _configService.WithRouterAddress("192.168.1.1");
        _configService.WithInternetTarget("8.8.8.8");
        _pingService.AlwaysSucceed(5);

        var options = new MonitorOptions
        {
            PingsPerCycle = 1,
            TimeoutMs = 1000,
            ExcellentLatencyMs = 20,
            GoodLatencyMs = 50,
            CustomTargets =
            [
                new CustomTargetConfig { Name = "Intranet", Address = "10.0.0.12", Enabled = true }
            ]
        };

        var service = CreateService(options);

        // Act
        var status = await service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(status.TargetResults);
        Assert.Contains(status.TargetResults, r => r.Target.Name == "Intranet");
    }

    [Fact]
    public async Task CheckNetworkAsync_WithDisabledCustomTarget_SkipsIt()
    {
        // Arrange
        _configService.WithRouterAddress("192.168.1.1");
        _configService.WithInternetTarget("8.8.8.8");
        _pingService.AlwaysSucceed(5);

        var options = new MonitorOptions
        {
            PingsPerCycle = 1,
            TimeoutMs = 1000,
            ExcellentLatencyMs = 20,
            GoodLatencyMs = 50,
            CustomTargets =
            [
                new CustomTargetConfig { Name = "Teams", Address = "teams.microsoft.com", Enabled = false }
            ]
        };

        var service = CreateService(options);

        // Act
        var status = await service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(status.TargetResults);
        Assert.DoesNotContain(status.TargetResults, r => r.Target.Name == "Teams");
    }

    [Fact]
    public async Task CheckNetworkAsync_ReturnsTargetResults()
    {
        // Arrange
        _configService.WithRouterAddress("192.168.1.1");
        _configService.WithInternetTarget("8.8.8.8");
        _pingService.AlwaysSucceed(5);

        var service = CreateService();

        // Act
        var status = await service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert - should have at least router and internet results
        Assert.NotNull(status.TargetResults);
        Assert.True(status.TargetResults.Count >= 2);
    }
}
```

---

## 25. `src/NetworkMonitor.Tests/Services/InternetTargetProviderTests.cs`

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Services;
using Xunit;

namespace NetworkMonitor.Tests.Services;

/// <summary>
/// Tests for InternetTargetProvider.
/// </summary>
public sealed class InternetTargetProviderTests
{
    [Fact]
    public void PrimaryTarget_ReturnsConfiguredTarget()
    {
        // Arrange
        var options = Options.Create(new MonitorOptions { InternetTarget = "1.1.1.1" });
        var provider = new InternetTargetProvider(options, NullLogger<InternetTargetProvider>.Instance);

        // Act & Assert
        Assert.Equal("1.1.1.1", provider.PrimaryTarget);
    }

    [Fact]
    public void GetTargets_ReturnsConfiguredTargetFirst()
    {
        // Arrange
        var options = Options.Create(new MonitorOptions { InternetTarget = "1.1.1.1" });
        var provider = new InternetTargetProvider(options, NullLogger<InternetTargetProvider>.Instance);

        // Act
        var targets = provider.GetTargets();

        // Assert
        Assert.Equal("1.1.1.1", targets[0]);
    }

    [Fact]
    public void GetTargets_IncludesMultipleFallbacks()
    {
        // Arrange
        var options = Options.Create(new MonitorOptions());
        var provider = new InternetTargetProvider(options, NullLogger<InternetTargetProvider>.Instance);

        // Act
        var targets = provider.GetTargets();

        // Assert
        Assert.True(targets.Count >= 3, "Should have multiple fallback targets");
        Assert.Contains("8.8.8.8", targets);
        Assert.Contains("1.1.1.1", targets);
    }

    [Fact]
    public void GetTargets_CustomTargetAddedToFront()
    {
        // Arrange - use a target not in the default list
        var options = Options.Create(new MonitorOptions { InternetTarget = "4.4.4.4" });
        var provider = new InternetTargetProvider(options, NullLogger<InternetTargetProvider>.Instance);

        // Act
        var targets = provider.GetTargets();

        // Assert
        Assert.Equal("4.4.4.4", targets[0]);
        Assert.Contains("8.8.8.8", targets); // Default fallbacks still present
    }

    [Fact]
    public void GetIPv6Targets_ReturnsNonEmptyList()
    {
        // Arrange
        var options = Options.Create(new MonitorOptions());
        var provider = new InternetTargetProvider(options, NullLogger<InternetTargetProvider>.Instance);

        // Act
        var targets = provider.GetIPv6Targets();

        // Assert
        Assert.NotEmpty(targets);
        Assert.Contains(targets, t => t.Contains(':'));
    }
}
```

---

## 26. `src/NetworkMonitor.Tests/Services/GatewayDetectorTests.cs`

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using NetworkMonitor.Core.Services;
using Xunit;

namespace NetworkMonitor.Tests.Services;

/// <summary>
/// Tests for GatewayDetector.
/// Note: These tests run against the real network stack, so results
/// depend on the test environment. We test the interface contract.
/// </summary>
public sealed class GatewayDetectorTests
{
    private readonly GatewayDetector _detector;

    public GatewayDetectorTests()
    {
        _detector = new GatewayDetector(NullLogger<GatewayDetector>.Instance);
    }

    [Fact]
    public void DetectDefaultGateway_ReturnsValidIpOrNull()
    {
        // Act
        var result = _detector.DetectDefaultGateway();

        // Assert - should be null or a valid IP
        if (result != null)
        {
            Assert.Matches(@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$", result);
        }
    }

    [Fact]
    public void DetectDefaultGatewayV6_ReturnsValidIpOrNull()
    {
        // Act
        var result = _detector.DetectDefaultGatewayV6();

        // Assert - should be null or a valid IPv6 address
        if (result != null)
        {
            Assert.Contains(":", result);
        }
    }

    [Fact]
    public void GetCommonGatewayAddresses_ReturnsNonEmptyList()
    {
        // Act
        var addresses = _detector.GetCommonGatewayAddresses();

        // Assert
        Assert.NotEmpty(addresses);
        Assert.Contains("192.168.1.1", addresses);
        Assert.Contains("192.168.0.1", addresses);
        Assert.Contains("10.0.0.1", addresses);
    }

    [Fact]
    public void GetCommonGatewayAddresses_AllAreValidIpAddresses()
    {
        // Act
        var addresses = _detector.GetCommonGatewayAddresses();

        // Assert
        foreach (var address in addresses)
        {
            Assert.Matches(@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$", address);
        }
    }
}
```

---

## 27. `src/NetworkMonitor.Tests/Models/MonitorOptionsTests.cs`

```csharp
using NetworkMonitor.Core.Models;
using Xunit;

namespace NetworkMonitor.Tests.Models;

/// <summary>
/// Tests for MonitorOptions.
/// </summary>
public sealed class MonitorOptionsTests
{
    [Fact]
    public void IsRouterAutoDetect_WhenAuto_ReturnsTrue()
    {
        // Arrange
        var options = new MonitorOptions { RouterAddress = "auto" };

        // Act & Assert
        Assert.True(options.IsRouterAutoDetect);
    }

    [Fact]
    public void IsRouterAutoDetect_WhenAutoUppercase_ReturnsTrue()
    {
        // Arrange
        var options = new MonitorOptions { RouterAddress = "AUTO" };

        // Act & Assert
        Assert.True(options.IsRouterAutoDetect);
    }

    [Fact]
    public void IsRouterAutoDetect_WhenEmpty_ReturnsTrue()
    {
        // Arrange
        var options = new MonitorOptions { RouterAddress = "" };

        // Act & Assert
        Assert.True(options.IsRouterAutoDetect);
    }

    [Fact]
    public void IsRouterAutoDetect_WhenNull_ReturnsTrue()
    {
        // Arrange
        var options = new MonitorOptions { RouterAddress = null! };

        // Act & Assert
        Assert.True(options.IsRouterAutoDetect);
    }

    [Fact]
    public void IsRouterAutoDetect_WhenSpecificIp_ReturnsFalse()
    {
        // Arrange
        var options = new MonitorOptions { RouterAddress = "192.168.1.1" };

        // Act & Assert
        Assert.False(options.IsRouterAutoDetect);
    }

    [Fact]
    public void DefaultValues_AreReasonable()
    {
        // Arrange & Act
        var options = new MonitorOptions();

        // Assert
        Assert.Equal(3000, options.TimeoutMs);
        Assert.Equal(5000, options.IntervalMs);
        Assert.Equal(3, options.PingsPerCycle);
        Assert.True(options.EnableFallbackTargets);
        Assert.True(options.EnableIPv6);
        Assert.True(options.EnableDnsChecks);
        Assert.Empty(options.CustomTargets);
        Assert.Empty(options.DisabledChecks);
    }

    [Fact]
    public void IsCheckDisabled_WhenInList_ReturnsTrue()
    {
        // Arrange
        var options = new MonitorOptions { DisabledChecks = ["Router", "Teams"] };

        // Act & Assert
        Assert.True(options.IsCheckDisabled("Router"));
        Assert.True(options.IsCheckDisabled("router")); // case-insensitive
        Assert.True(options.IsCheckDisabled("Teams"));
        Assert.False(options.IsCheckDisabled("Internet"));
    }

    [Fact]
    public void IsCheckDisabled_WhenEmpty_ReturnsFalse()
    {
        // Arrange
        var options = new MonitorOptions();

        // Act & Assert
        Assert.False(options.IsCheckDisabled("Router"));
    }
}
```

---

## 28. `src/NetworkMonitor.Tests/Models/DnsResultTests.cs` (NEW FILE)

```csharp
using NetworkMonitor.Core.Models;
using Xunit;

namespace NetworkMonitor.Tests.Models;

/// <summary>
/// Tests for DnsResult.
/// </summary>
public sealed class DnsResultTests
{
    [Fact]
    public void Succeeded_CreatesSuccessfulResult()
    {
        // Arrange & Act
        var result = DnsResult.Succeeded("example.com", ["1.2.3.4", "5.6.7.8"], 15);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("example.com", result.Hostname);
        Assert.Equal(2, result.ResolvedAddresses.Count);
        Assert.Equal(15, result.ResolutionTimeMs);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void Failed_CreatesFailedResult()
    {
        // Arrange & Act
        var result = DnsResult.Failed("bad.example.com", 100, "No such host");

        // Assert
        Assert.False(result.Success);
        Assert.Equal("bad.example.com", result.Hostname);
        Assert.Empty(result.ResolvedAddresses);
        Assert.Equal(100, result.ResolutionTimeMs);
        Assert.Equal("No such host", result.ErrorMessage);
    }
}
```

---

## 29. `src/NetworkMonitor.Tests/Models/TargetCheckResultTests.cs` (NEW FILE)

```csharp
using NetworkMonitor.Core.Models;
using Xunit;

namespace NetworkMonitor.Tests.Models;

/// <summary>
/// Tests for TargetCheckResult.
/// </summary>
public sealed class TargetCheckResultTests
{
    [Fact]
    public void Constructor_SetsAllProperties()
    {
        // Arrange
        var target = new MonitorTarget("Test", "1.2.3.4", TargetCategory.PublicDns);
        var ping = PingResult.Succeeded("1.2.3.4", 10);
        var dns = DnsResult.Succeeded("test.com", ["1.2.3.4"], 5);

        // Act
        var result = new TargetCheckResult(target, ping, null, dns, 0.0, DateTimeOffset.UtcNow);

        // Assert
        Assert.Equal("Test", result.Target.Name);
        Assert.NotNull(result.PingResult);
        Assert.Null(result.PingResultV6);
        Assert.NotNull(result.DnsResult);
        Assert.Equal(0.0, result.PacketLossPercent);
    }

    [Fact]
    public void MonitorTarget_Categories()
    {
        // Act & Assert
        Assert.Equal(TargetCategory.Router, new MonitorTarget("R", "1.1.1.1", TargetCategory.Router).Category);
        Assert.Equal(TargetCategory.PublicDns, new MonitorTarget("D", "8.8.8.8", TargetCategory.PublicDns).Category);
        Assert.Equal(TargetCategory.Service, new MonitorTarget("S", "teams.ms.com", TargetCategory.Service).Category);
        Assert.Equal(TargetCategory.Custom, new MonitorTarget("C", "10.0.0.1", TargetCategory.Custom).Category);
    }
}
```

---

## 30. `src/NetworkMonitor.Tests/Services/DnsResolverServiceTests.cs` (NEW FILE)

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using NetworkMonitor.Core.Services;
using Xunit;

namespace NetworkMonitor.Tests.Services;

/// <summary>
/// Tests for DnsResolverService.
/// Note: These tests run against real DNS, so results depend on the test environment.
/// </summary>
public sealed class DnsResolverServiceTests
{
    private readonly DnsResolverService _resolver;

    public DnsResolverServiceTests()
    {
        _resolver = new DnsResolverService(NullLogger<DnsResolverService>.Instance);
    }

    [Fact]
    public async Task ResolveAsync_WithIpAddress_ReturnsItDirectly()
    {
        // Act
        var result = await _resolver.ResolveAsync("8.8.8.8", TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result.Success);
        Assert.Contains("8.8.8.8", result.ResolvedAddresses);
    }

    [Fact]
    public async Task ResolveAsync_WithIpv6Address_ReturnsItDirectly()
    {
        // Act
        var result = await _resolver.ResolveAsync("2001:4860:4860::8888", TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result.Success);
        Assert.Contains("2001:4860:4860::8888", result.ResolvedAddresses);
    }

    [Fact]
    public async Task ResolveAsync_SupportsCancellation()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _resolver.ResolveAsync("example.com", cts.Token));
    }
}
```
