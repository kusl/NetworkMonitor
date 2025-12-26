#!/bin/bash
# =============================================================================
# fix-build-errors.sh
# Fixes build errors and improves test coverage for NetworkMonitor
# =============================================================================

set -euo pipefail

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

log_info() { echo -e "${CYAN}[INFO]${NC} $1"; }
log_success() { echo -e "${GREEN}[SUCCESS]${NC} $1"; }
log_warn() { echo -e "${YELLOW}[WARN]${NC} $1"; }
log_error() { echo -e "${RED}[ERROR]${NC} $1"; }

# Ensure we're in the right directory
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# Detect if we're in src/ or project root
if [[ -d "NetworkMonitor.Core" ]]; then
    SRC_DIR="."
elif [[ -d "src/NetworkMonitor.Core" ]]; then
    SRC_DIR="src"
else
    log_error "Cannot find NetworkMonitor.Core directory. Run this script from the project root or src directory."
    exit 1
fi

log_info "Working directory: $(pwd)"
log_info "Source directory: $SRC_DIR"

# =============================================================================
# FIX 1: NetworkStatusEventArgs - Add PreviousStatus property
# Error: CS1729: 'NetworkStatusEventArgs' does not contain a constructor that takes 2 arguments
# =============================================================================
log_info "Fix 1: Updating NetworkStatusEventArgs to support 2-argument constructor..."

cat > "$SRC_DIR/NetworkMonitor.Core/Models/NetworkStatusEventArgs.cs" << 'EOF'
namespace NetworkMonitor.Core.Models;

/// <summary>
/// Event arguments for network status change events.
/// Required for CA1003 compliance (EventHandler should use EventArgs).
/// </summary>
public sealed class NetworkStatusEventArgs : EventArgs
{
    /// <summary>
    /// The current network status.
    /// </summary>
    public NetworkStatus CurrentStatus { get; }

    /// <summary>
    /// The previous network status (null if this is the first check).
    /// </summary>
    public NetworkStatus? PreviousStatus { get; }

    /// <summary>
    /// Creates a new instance of NetworkStatusEventArgs with current status only.
    /// </summary>
    /// <param name="currentStatus">The current network status.</param>
    public NetworkStatusEventArgs(NetworkStatus currentStatus)
        : this(currentStatus, null)
    {
    }

    /// <summary>
    /// Creates a new instance of NetworkStatusEventArgs with current and previous status.
    /// </summary>
    /// <param name="currentStatus">The current network status.</param>
    /// <param name="previousStatus">The previous network status.</param>
    public NetworkStatusEventArgs(NetworkStatus currentStatus, NetworkStatus? previousStatus)
    {
        CurrentStatus = currentStatus;
        PreviousStatus = previousStatus;
    }

    /// <summary>
    /// Alias for CurrentStatus to maintain backward compatibility.
    /// </summary>
    public NetworkStatus Status => CurrentStatus;
}
EOF
log_success "NetworkStatusEventArgs updated with 2-argument constructor"

# =============================================================================
# FIX 2: NetworkConfigurationService - Implement IDisposable for CA1001
# Error: CA1001: Type 'NetworkConfigurationService' owns disposable field(s) '_initLock' but is not disposable
# =============================================================================
log_info "Fix 2: Updating NetworkConfigurationService to implement IDisposable..."

cat > "$SRC_DIR/NetworkMonitor.Core/Services/NetworkConfigurationService.cs" << 'EOF'
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

    private async Task<string> ResolveInternetTargetAsync(CancellationToken cancellationToken)
    {
        var targets = _internetTargetProvider.GetTargets();

        foreach (var target in targets)
        {
            if (await IsReachableAsync(target, cancellationToken))
            {
                _logger.LogDebug("Using internet target: {Target}", target);
                return target;
            }
            _logger.LogDebug("Internet target {Target} is not reachable", target);
        }

        // Return primary target even if not reachable - we'll report the failure
        _logger.LogWarning("No reachable internet targets found. Using primary: {Target}",
            _internetTargetProvider.PrimaryTarget);
        return _internetTargetProvider.PrimaryTarget;
    }

    private async Task<bool> IsReachableAsync(string target, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _pingService.PingAsync(target, 2000, cancellationToken);
            return result.Success;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to ping {Target}", target);
            return false;
        }
    }

    /// <summary>
    /// Disposes the service and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _initLock.Dispose();
    }
}
EOF
log_success "NetworkConfigurationService now implements IDisposable"

# =============================================================================
# FIX 3: NetworkMonitorService - Make ComputeHealth static
# Error: CA1822: Member 'ComputeHealth' does not access instance data and can be marked as static
# =============================================================================
log_info "Fix 3: Updating NetworkMonitorService - making ComputeHealth static..."

cat > "$SRC_DIR/NetworkMonitor.Core/Services/NetworkMonitorService.cs" << 'EOF'
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Services;

/// <summary>
/// Main network monitoring service.
/// Coordinates ping operations and computes overall network health.
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

    private readonly IPingService _pingService;
    private readonly INetworkConfigurationService _configService;
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
        IOptions<MonitorOptions> options,
        ILogger<NetworkMonitorService> logger)
    {
        _pingService = pingService;
        _configService = configService;
        _options = options.Value;
        _logger = logger;
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

        // Ping router (if we have one)
        PingResult? routerResult = null;
        if (!string.IsNullOrEmpty(routerAddress))
        {
            routerResult = await PingWithAggregationAsync(routerAddress, cancellationToken);
            if (routerResult is { Success: true, RoundtripTimeMs: not null })
            {
                RouterLatencyHistogram.Record(routerResult.RoundtripTimeMs.Value);
            }
            else
            {
                FailureCounter.Add(1, new KeyValuePair<string, object?>("target_type", "router"));
            }
        }

        // Ping internet target
        var internetResult = await PingWithAggregationAsync(internetTarget, cancellationToken);
        if (internetResult is { Success: true, RoundtripTimeMs: not null })
        {
            InternetLatencyHistogram.Record(internetResult.RoundtripTimeMs.Value);
        }
        else
        {
            FailureCounter.Add(1, new KeyValuePair<string, object?>("target_type", "internet"));
        }

        // Compute overall health
        var (health, message) = ComputeHealth(routerResult, internetResult, _options);

        var status = new NetworkStatus(
            health,
            routerResult,
            internetResult,
            DateTimeOffset.UtcNow,
            message);

        activity?.SetTag("health", health.ToString());
        activity?.SetTag("router.success", routerResult?.Success ?? false);
        activity?.SetTag("internet.success", internetResult.Success);

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

    private async Task<PingResult> PingWithAggregationAsync(
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

            return AggregateResults(results);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error pinging {Target}", target);
            return PingResult.Failed(target, ex.Message);
        }
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
    /// <remarks>
    /// This method is static as it does not access instance data (CA1822).
    /// </remarks>
    private static (NetworkHealth Health, string Message) ComputeHealth(
        PingResult? routerResult,
        PingResult internetResult,
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
EOF
log_success "NetworkMonitorService.ComputeHealth is now static"

# =============================================================================
# Update FakeNetworkConfigurationService for tests
# =============================================================================
log_info "Updating FakeNetworkConfigurationService..."

cat > "$SRC_DIR/NetworkMonitor.Tests/Fakes/FakeNetworkConfigurationService.cs" << 'EOF'
using NetworkMonitor.Core.Services;

namespace NetworkMonitor.Tests.Fakes;

/// <summary>
/// Fake network configuration service for testing.
/// </summary>
public sealed class FakeNetworkConfigurationService : INetworkConfigurationService, IDisposable
{
    private string? _routerAddress = "192.168.1.1";
    private string _internetTarget = "8.8.8.8";

    public FakeNetworkConfigurationService WithRouterAddress(string? address)
    {
        _routerAddress = address;
        return this;
    }

    public FakeNetworkConfigurationService WithInternetTarget(string target)
    {
        _internetTarget = target;
        return this;
    }

    public Task<string?> GetRouterAddressAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_routerAddress);

    public Task<string> GetInternetTargetAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_internetTarget);

    public void Dispose()
    {
        // Nothing to dispose in fake
    }
}
EOF
log_success "FakeNetworkConfigurationService updated"

# =============================================================================
# Update NetworkMonitorServiceTests to use new EventArgs signature
# =============================================================================
log_info "Updating NetworkMonitorServiceTests..."

cat > "$SRC_DIR/NetworkMonitor.Tests/Services/NetworkMonitorServiceTests.cs" << 'EOF'
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Services;
using NetworkMonitor.Tests.Fakes;
using Xunit;

namespace NetworkMonitor.Tests.Services;

/// <summary>
/// Tests for NetworkMonitorService.
/// Uses fake implementations for isolation.
/// </summary>
public sealed class NetworkMonitorServiceTests
{
    private readonly FakePingService _pingService;
    private readonly FakeNetworkConfigurationService _configService;
    private readonly NetworkMonitorService _service;

    public NetworkMonitorServiceTests()
    {
        _pingService = new FakePingService();
        _configService = new FakeNetworkConfigurationService();
        var options = Options.Create(new MonitorOptions());
        _service = new NetworkMonitorService(
            _pingService,
            _configService,
            options,
            NullLogger<NetworkMonitorService>.Instance);
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenBothSucceed_ReturnsExcellentOrGood()
    {
        // Arrange
        _pingService.AlwaysSucceed(latencyMs: 5);

        // Act
        var status = await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(status.Health is NetworkHealth.Excellent or NetworkHealth.Good);
        Assert.True(status.RouterResult?.Success);
        Assert.True(status.InternetResult?.Success);
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenRouterFails_ReturnsOfflineOrDegraded()
    {
        // Arrange - router fails, internet succeeds
        _pingService
            .QueueResult(PingResult.Failed("192.168.1.1", "Timeout"))
            .QueueResult(PingResult.Failed("192.168.1.1", "Timeout"))
            .QueueResult(PingResult.Failed("192.168.1.1", "Timeout"))
            .QueueResult(PingResult.Succeeded("8.8.8.8", 20))
            .QueueResult(PingResult.Succeeded("8.8.8.8", 20))
            .QueueResult(PingResult.Succeeded("8.8.8.8", 20));

        // Act
        var status = await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(status.Health is NetworkHealth.Offline or NetworkHealth.Degraded);
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenInternetFails_ReturnsPoorOrOffline()
    {
        // Arrange - router succeeds, internet fails
        _pingService
            .QueueResult(PingResult.Succeeded("192.168.1.1", 5))
            .QueueResult(PingResult.Succeeded("192.168.1.1", 5))
            .QueueResult(PingResult.Succeeded("192.168.1.1", 5))
            .QueueResult(PingResult.Failed("8.8.8.8", "Timeout"))
            .QueueResult(PingResult.Failed("8.8.8.8", "Timeout"))
            .QueueResult(PingResult.Failed("8.8.8.8", "Timeout"));

        // Act
        var status = await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(status.Health is NetworkHealth.Poor or NetworkHealth.Offline);
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenBothFail_ReturnsOffline()
    {
        // Arrange
        _pingService.AlwaysFail("Connection refused");

        // Act
        var status = await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(NetworkHealth.Offline, status.Health);
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenNoRouterConfigured_SkipsRouterPing()
    {
        // Arrange
        _configService.WithRouterAddress(null);
        _pingService.AlwaysSucceed(20);

        // Act
        var status = await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(status.RouterResult);
        Assert.NotNull(status.InternetResult);
        Assert.True(status.InternetResult.Success);
    }

    [Fact]
    public async Task CheckNetworkAsync_HighLatency_ReturnsDegradedOrPoor()
    {
        // Arrange - High latency on internet
        _pingService
            .QueueResult(PingResult.Succeeded("192.168.1.1", 10))
            .QueueResult(PingResult.Succeeded("192.168.1.1", 10))
            .QueueResult(PingResult.Succeeded("192.168.1.1", 10))
            .QueueResult(PingResult.Succeeded("8.8.8.8", 500))
            .QueueResult(PingResult.Succeeded("8.8.8.8", 500))
            .QueueResult(PingResult.Succeeded("8.8.8.8", 500));

        // Act
        var status = await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(status.Health is NetworkHealth.Degraded or NetworkHealth.Poor);
    }

    [Fact]
    public async Task CheckNetworkAsync_FiresStatusChangedEvent()
    {
        // Arrange
        _pingService.AlwaysSucceed(5);
        NetworkStatusEventArgs? receivedArgs = null;
        _service.StatusChanged += (_, e) => receivedArgs = e;

        // Act
        await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(receivedArgs);
        Assert.NotNull(receivedArgs.CurrentStatus);
        Assert.Null(receivedArgs.PreviousStatus); // First check has no previous
    }

    [Fact]
    public async Task CheckNetworkAsync_SecondCall_HasPreviousStatus()
    {
        // Arrange
        _pingService.AlwaysSucceed(5);
        NetworkStatusEventArgs? lastArgs = null;
        _service.StatusChanged += (_, e) => lastArgs = e;

        // Act - First call
        await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Change health to trigger event
        _pingService.AlwaysFail("Network down");
        await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(lastArgs);
        Assert.NotNull(lastArgs.PreviousStatus);
    }

    [Fact]
    public async Task CheckNetworkAsync_RespectsCancellation()
    {
        // Arrange
        _pingService.AlwaysSucceed(5);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _service.CheckNetworkAsync(cts.Token));
    }

    [Fact]
    public async Task CheckNetworkAsync_StatusPropertyEqualsCurrentStatus()
    {
        // Arrange
        _pingService.AlwaysSucceed(5);
        NetworkStatusEventArgs? receivedArgs = null;
        _service.StatusChanged += (_, e) => receivedArgs = e;

        // Act
        await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert - Status should equal CurrentStatus (backward compatibility)
        Assert.NotNull(receivedArgs);
        Assert.Same(receivedArgs.CurrentStatus, receivedArgs.Status);
    }
}
EOF
log_success "NetworkMonitorServiceTests updated"

# =============================================================================
# Add NetworkStatusEventArgsTests
# =============================================================================
log_info "Adding NetworkStatusEventArgsTests..."

cat > "$SRC_DIR/NetworkMonitor.Tests/Models/NetworkStatusEventArgsTests.cs" << 'EOF'
using NetworkMonitor.Core.Models;
using Xunit;

namespace NetworkMonitor.Tests.Models;

/// <summary>
/// Tests for NetworkStatusEventArgs.
/// </summary>
public sealed class NetworkStatusEventArgsTests
{
    private static NetworkStatus CreateTestStatus(NetworkHealth health) =>
        new(health, null, null, DateTimeOffset.UtcNow, "Test");

    [Fact]
    public void Constructor_SingleArg_SetsCurrentStatus()
    {
        // Arrange
        var status = CreateTestStatus(NetworkHealth.Excellent);

        // Act
        var args = new NetworkStatusEventArgs(status);

        // Assert
        Assert.Equal(status, args.CurrentStatus);
        Assert.Null(args.PreviousStatus);
    }

    [Fact]
    public void Constructor_TwoArgs_SetsBothStatuses()
    {
        // Arrange
        var current = CreateTestStatus(NetworkHealth.Excellent);
        var previous = CreateTestStatus(NetworkHealth.Poor);

        // Act
        var args = new NetworkStatusEventArgs(current, previous);

        // Assert
        Assert.Equal(current, args.CurrentStatus);
        Assert.Equal(previous, args.PreviousStatus);
    }

    [Fact]
    public void Status_ReturnsCurrentStatus()
    {
        // Arrange
        var current = CreateTestStatus(NetworkHealth.Good);
        var previous = CreateTestStatus(NetworkHealth.Degraded);
        var args = new NetworkStatusEventArgs(current, previous);

        // Act & Assert
        Assert.Same(args.CurrentStatus, args.Status);
    }

    [Fact]
    public void Constructor_WithNullPrevious_Succeeds()
    {
        // Arrange
        var current = CreateTestStatus(NetworkHealth.Excellent);

        // Act
        var args = new NetworkStatusEventArgs(current, null);

        // Assert
        Assert.Equal(current, args.CurrentStatus);
        Assert.Null(args.PreviousStatus);
    }
}
EOF
log_success "NetworkStatusEventArgsTests added"

# =============================================================================
# Add NetworkConfigurationServiceTests
# =============================================================================
log_info "Adding/Updating NetworkConfigurationServiceTests..."

cat > "$SRC_DIR/NetworkMonitor.Tests/Services/NetworkConfigurationServiceTests.cs" << 'EOF'
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Services;
using NetworkMonitor.Tests.Fakes;
using Xunit;

namespace NetworkMonitor.Tests.Services;

/// <summary>
/// Tests for NetworkConfigurationService.
/// </summary>
public sealed class NetworkConfigurationServiceTests : IDisposable
{
    private readonly FakeGatewayDetector _gatewayDetector;
    private readonly FakeInternetTargetProvider _internetTargetProvider;
    private readonly FakePingService _pingService;
    private NetworkConfigurationService? _service;

    public NetworkConfigurationServiceTests()
    {
        _gatewayDetector = new FakeGatewayDetector();
        _internetTargetProvider = new FakeInternetTargetProvider();
        _pingService = new FakePingService();
    }

    private NetworkConfigurationService CreateService(MonitorOptions? options = null)
    {
        _service = new NetworkConfigurationService(
            _gatewayDetector,
            _internetTargetProvider,
            _pingService,
            Options.Create(options ?? new MonitorOptions()),
            NullLogger<NetworkConfigurationService>.Instance);
        return _service;
    }

    public void Dispose()
    {
        _service?.Dispose();
    }

    [Fact]
    public async Task GetRouterAddressAsync_WhenExplicitlyConfigured_ReturnsConfiguredAddress()
    {
        // Arrange
        var options = new MonitorOptions { RouterAddress = "10.0.0.1" };
        var service = CreateService(options);

        // Act
        var result = await service.GetRouterAddressAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("10.0.0.1", result);
    }

    [Fact]
    public async Task GetRouterAddressAsync_WhenAutoDetect_UsesDetectedGateway()
    {
        // Arrange
        _gatewayDetector.WithGateway("192.168.1.1");
        _pingService.AlwaysSucceed(5);
        var options = new MonitorOptions { RouterAddress = "auto" };
        var service = CreateService(options);

        // Act
        var result = await service.GetRouterAddressAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("192.168.1.1", result);
    }

    [Fact]
    public async Task GetRouterAddressAsync_WhenDetectionFails_FallsBackToCommonGateways()
    {
        // Arrange
        _gatewayDetector.WithNoGateway();
        _gatewayDetector.WithCommonGateways("192.168.0.1", "10.0.0.1");
        _pingService.AlwaysSucceed(5);
        var options = new MonitorOptions { RouterAddress = "auto" };
        var service = CreateService(options);

        // Act
        var result = await service.GetRouterAddressAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("192.168.0.1", result);
    }

    [Fact]
    public async Task GetRouterAddressAsync_WhenNoGatewayReachable_ReturnsNull()
    {
        // Arrange
        _gatewayDetector.WithNoGateway();
        _gatewayDetector.WithCommonGateways("192.168.0.1");
        _pingService.AlwaysFail("Unreachable");
        var options = new MonitorOptions { RouterAddress = "auto" };
        var service = CreateService(options);

        // Act
        var result = await service.GetRouterAddressAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetInternetTargetAsync_ReturnsFirstReachableTarget()
    {
        // Arrange
        _internetTargetProvider.WithTargets("8.8.8.8", "1.1.1.1");
        _pingService.AlwaysSucceed(10);
        var service = CreateService();

        // Act
        var result = await service.GetInternetTargetAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("8.8.8.8", result);
    }

    [Fact]
    public async Task GetInternetTargetAsync_FallsBackWhenFirstUnreachable()
    {
        // Arrange
        _internetTargetProvider.WithTargets("8.8.8.8", "1.1.1.1");
        _pingService
            .QueueResult(PingResult.Failed("8.8.8.8", "Unreachable"))
            .QueueResult(PingResult.Succeeded("1.1.1.1", 20));
        var service = CreateService();

        // Act
        var result = await service.GetInternetTargetAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("1.1.1.1", result);
    }

    [Fact]
    public async Task GetInternetTargetAsync_ReturnsPrimaryWhenNoneReachable()
    {
        // Arrange
        _internetTargetProvider.WithPrimaryTarget("8.8.8.8");
        _internetTargetProvider.WithTargets("8.8.8.8", "1.1.1.1");
        _pingService.AlwaysFail("All unreachable");
        var service = CreateService();

        // Act
        var result = await service.GetInternetTargetAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("8.8.8.8", result);
    }

    [Fact]
    public async Task ResultsAreCached_MultipleCallsReturnSameValue()
    {
        // Arrange
        _gatewayDetector.WithGateway("192.168.1.1");
        _pingService.AlwaysSucceed(5);
        var options = new MonitorOptions { RouterAddress = "auto" };
        var service = CreateService(options);

        // Act
        var result1 = await service.GetRouterAddressAsync(TestContext.Current.CancellationToken);
        _gatewayDetector.WithGateway("10.0.0.1"); // Change gateway
        var result2 = await service.GetRouterAddressAsync(TestContext.Current.CancellationToken);

        // Assert - Should return cached value
        Assert.Equal(result1, result2);
        Assert.Equal("192.168.1.1", result2);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        // Arrange
        var service = CreateService();

        // Act & Assert - Should not throw
        service.Dispose();
        service.Dispose();
    }

    [Fact]
    public async Task GetRouterAddressAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        var service = CreateService();
        service.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => service.GetRouterAddressAsync(TestContext.Current.CancellationToken));
    }
}
EOF
log_success "NetworkConfigurationServiceTests added"

# =============================================================================
# Update FakeInternetTargetProvider for tests
# =============================================================================
log_info "Updating FakeInternetTargetProvider..."

cat > "$SRC_DIR/NetworkMonitor.Tests/Fakes/FakeInternetTargetProvider.cs" << 'EOF'
using NetworkMonitor.Core.Services;

namespace NetworkMonitor.Tests.Fakes;

/// <summary>
/// Fake internet target provider for testing.
/// </summary>
public sealed class FakeInternetTargetProvider : IInternetTargetProvider
{
    private string _primaryTarget = "8.8.8.8";
    private readonly List<string> _targets = ["8.8.8.8", "1.1.1.1"];

    public string PrimaryTarget => _primaryTarget;

    public FakeInternetTargetProvider WithPrimaryTarget(string target)
    {
        _primaryTarget = target;
        return this;
    }

    public FakeInternetTargetProvider WithTargets(params string[] targets)
    {
        _targets.Clear();
        _targets.AddRange(targets);
        if (!string.IsNullOrEmpty(_primaryTarget) && !_targets.Contains(_primaryTarget))
        {
            _primaryTarget = _targets.FirstOrDefault() ?? "8.8.8.8";
        }
        return this;
    }

    public IReadOnlyList<string> GetTargets() => _targets;
}
EOF
log_success "FakeInternetTargetProvider updated"

# =============================================================================
# Build and Test
# =============================================================================
log_info "Building solution..."
cd "$SRC_DIR"
if dotnet build --nologo -v q; then
    log_success "Build succeeded!"
else
    log_error "Build failed!"
    exit 1
fi

log_info "Running tests..."
if dotnet test --nologo -v q; then
    log_success "All tests passed!"
else
    log_warn "Some tests failed. Please review the output above."
fi

# =============================================================================
# Summary
# =============================================================================
echo ""
echo "============================================================================="
echo -e "${GREEN}Fix Summary${NC}"
echo "============================================================================="
echo ""
echo "Fixed 3 build errors:"
echo ""
echo "1. CS1729: NetworkStatusEventArgs constructor"
echo "   - Added 2-argument constructor (currentStatus, previousStatus)"
echo "   - Added backward-compatible 'Status' property alias"
echo ""
echo "2. CA1001: NetworkConfigurationService not disposable"
echo "   - Implemented IDisposable interface"
echo "   - Added Dispose() method to clean up _initLock SemaphoreSlim"
echo "   - Added ObjectDisposedException checks"
echo ""
echo "3. CA1822: ComputeHealth can be static"
echo "   - Made ComputeHealth method static"
echo "   - Passed MonitorOptions as parameter instead of using instance field"
echo ""
echo "Added/Updated tests:"
echo "   - NetworkStatusEventArgsTests (new)"
echo "   - NetworkMonitorServiceTests (updated for new EventArgs)"
echo "   - NetworkConfigurationServiceTests (updated with dispose tests)"
echo "   - FakeNetworkConfigurationService (implements IDisposable)"
echo "   - FakeInternetTargetProvider (enhanced for testing)"
echo ""
echo "============================================================================="
EOF
