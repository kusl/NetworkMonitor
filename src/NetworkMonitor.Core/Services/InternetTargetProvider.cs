using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Services;

/// <summary>
/// Provides internet connectivity test targets with automatic fallback.
/// </summary>
public sealed class InternetTargetProvider : IInternetTargetProvider
{
    private readonly ILogger<InternetTargetProvider> _logger;
    private readonly MonitorOptions _options;

    /// <summary>
    /// Well-known, highly available DNS servers that can be used for
    /// connectivity testing. Ordered by global reliability.
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
}
