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
