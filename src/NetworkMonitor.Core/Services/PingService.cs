using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Services;

/// <summary>
/// Cross-platform ping implementation using System.Net.NetworkInformation.
/// Supports both IPv4 and IPv6.
/// Works on Windows, macOS, and Linux without external dependencies.
/// </summary>
/// <remarks>
/// Two correctness details worth knowing:
///
/// 1. DNS is resolved ONCE per <see cref="PingMultipleAsync"/> call and the
///    resulting IP is reused for all pings in that round. Previously each ping
///    re-resolved the hostname, which (a) issued 1 + N DNS lookups per target
///    per cycle and (b) let DNS round-robin change the IP between pings, making
///    latency variance impossible to interpret.
///
/// 2. Latency comes from the ICMP reply's RoundtripTime, not a wall-clock
///    Stopwatch. The old Stopwatch was started and stopped but never read.
/// </remarks>
public sealed class PingService : IPingService
{
    private readonly ILogger<PingService> _logger;
    private readonly MonitorOptions _options;

    public PingService(
        ILogger<PingService> logger,
        IOptions<MonitorOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<PingResult> PingAsync(
        string target,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var (address, error) = await ResolveTargetAsync(target, cancellationToken).ConfigureAwait(false);
        if (address is null)
        {
            return PingResult.Failed(target, error ?? "Could not resolve target");
        }

        return await PingResolvedAsync(target, address, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PingResult>> PingMultipleAsync(
        string target,
        int count,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        var results = new List<PingResult>(Math.Max(count, 0));

        // Resolve the hostname exactly once, then reuse the IP for every ping
        // in this round so the numbers are directly comparable.
        var (address, error) = await ResolveTargetAsync(target, cancellationToken).ConfigureAwait(false);

        if (address is null)
        {
            // Emit one failed result per requested ping so packet-loss math and
            // aggregation behave exactly as they would for real timeouts.
            var count1 = Math.Max(count, 1);
            for (var i = 0; i < count1; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(PingResult.Failed(target, error ?? "Could not resolve target"));
            }
            return results;
        }

        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            results.Add(await PingResolvedAsync(target, address, timeoutMs, cancellationToken).ConfigureAwait(false));

            // Small delay between pings to avoid flooding.
            if (i < count - 1)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        return results;
    }

    /// <summary>
    /// Resolves a target string to a single IP address to ping.
    /// IP literals are returned as-is. Hostnames are resolved and reduced to one
    /// address via <see cref="SelectAddress"/>, honoring the IPv6 policy.
    /// </summary>
    private async Task<(IPAddress? Address, string? Error)> ResolveTargetAsync(
        string target,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(target, out var literal))
        {
            // Explicit IP literal: ping exactly what the user asked for,
            // regardless of the EnableIPv6 policy.
            return (literal, null);
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(target, cancellationToken).ConfigureAwait(false);
            var chosen = SelectAddress(addresses, _options.EnableIPv6);

            if (chosen is not null)
            {
                return (chosen, null);
            }

            if (addresses.Length == 0)
            {
                return (null, "DNS resolution returned no addresses");
            }

            // The only way to get here is IPv6-only host with IPv6 disabled.
            return (null, "Host resolved only to IPv6 but IPv6 is disabled");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"DNS resolution failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Chooses a single address to ping from a resolved set.
    /// Prefers IPv4 (for stable, comparable latency); falls back to IPv6 only
    /// when <paramref name="enableIPv6"/> is true and no IPv4 address exists.
    /// Returns null when nothing is usable under the policy.
    /// </summary>
    public static IPAddress? SelectAddress(IReadOnlyList<IPAddress> addresses, bool enableIPv6)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        IPAddress? firstV6 = null;

        foreach (var addr in addresses)
        {
            if (addr.AddressFamily == AddressFamily.InterNetwork)
            {
                return addr; // IPv4 preferred, first wins
            }

            if (firstV6 is null && addr.AddressFamily == AddressFamily.InterNetworkV6)
            {
                firstV6 = addr;
            }
        }

        return enableIPv6 ? firstV6 : null;
    }

    private async Task<PingResult> PingResolvedAsync(
        string displayTarget,
        IPAddress address,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Pinging {Target} ({Address}) with timeout {TimeoutMs}ms",
                displayTarget, address, timeoutMs);

            // A new Ping instance per call: Ping does not support concurrent
            // async operations on a shared instance.
            using var ping = new Ping();

            // This overload honors the cancellation token, so shutdown is prompt
            // instead of blocking for the full timeout.
            var reply = await ping.SendPingAsync(
                    address,
                    TimeSpan.FromMilliseconds(timeoutMs),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (reply.Status == IPStatus.Success)
            {
                _logger.LogDebug("Ping to {Target} succeeded: {RoundtripMs}ms",
                    displayTarget, reply.RoundtripTime);
                return PingResult.Succeeded(displayTarget, reply.RoundtripTime);
            }

            var errorMessage = reply.Status.ToString();
            _logger.LogDebug("Ping to {Target} failed: {Status}", displayTarget, errorMessage);
            return PingResult.Failed(displayTarget, errorMessage);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Ping to {Target} cancelled", displayTarget);
            throw;
        }
        catch (PingException ex)
        {
            _logger.LogWarning(ex, "Ping to {Target} threw exception", displayTarget);
            return PingResult.Failed(displayTarget, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error pinging {Target}", displayTarget);
            return PingResult.Failed(displayTarget, $"Unexpected error: {ex.Message}");
        }
    }
}
