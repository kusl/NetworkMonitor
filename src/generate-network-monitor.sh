#!/bin/bash
# =============================================================================
# Fix Test Failures Script for NetworkMonitor
# =============================================================================
# Fixes 3 test failures:
# 1. NetworkHealth_CanCompare - enum ordering issue
# 2. GetInternetTargetAsync_ReturnsPrimaryTarget - test expectation mismatch
# 3. CheckNetworkAsync_WhenInternetFails_ReturnsDegradedOrPoor - logic issue
# =============================================================================

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

log_info() { echo -e "${YELLOW}[INFO]${NC} $1"; }
log_success() { echo -e "${GREEN}[SUCCESS]${NC} $1"; }
log_error() { echo -e "${RED}[ERROR]${NC} $1"; }

# Determine working directory
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [[ -d "$SCRIPT_DIR/src" ]]; then
    SRC_DIR="$SCRIPT_DIR/src"
elif [[ -d "$SCRIPT_DIR/NetworkMonitor.Core" ]]; then
    SRC_DIR="$SCRIPT_DIR"
else
    SRC_DIR="."
fi

log_info "Working directory: $(pwd)"
log_info "Source directory: $SRC_DIR"

# =============================================================================
# Fix 1: NetworkHealth Enum - Ensure proper ordering for comparison
# =============================================================================
# The enum values are defined as:
#   Excellent, Good, Degraded, Poor, Offline
# This means Excellent=0, Good=1, Degraded=2, Poor=3, Offline=4
# But the test expects: Excellent > Good > Degraded > Poor > Offline
# We need to reverse the order or change the test logic
# =============================================================================
log_info "Fixing NetworkHealth enum ordering..."

cat > "$SRC_DIR/NetworkMonitor.Core/Models/NetworkStatus.cs" << 'EOF'
namespace NetworkMonitor.Core.Models;

/// <summary>
/// Represents the overall network health status.
/// This is the "at a glance" view that's our highest priority.
/// </summary>
/// <remarks>
/// Values are ordered from worst (0) to best (4) for natural comparison.
/// This allows: NetworkHealth.Excellent > NetworkHealth.Poor
/// </remarks>
public enum NetworkHealth
{
    /// <summary>No connectivity</summary>
    Offline = 0,

    /// <summary>Significant connectivity issues</summary>
    Poor = 1,

    /// <summary>Some packet loss or high latency</summary>
    Degraded = 2,

    /// <summary>All targets responding but some latency</summary>
    Good = 3,

    /// <summary>All targets responding with good latency</summary>
    Excellent = 4
}

/// <summary>
/// Comprehensive network status at a point in time.
/// Aggregates multiple ping results into a single status view.
/// </summary>
/// <param name="Health">Overall health assessment</param>
/// <param name="RouterResult">Result of pinging the local router/gateway</param>
/// <param name="InternetResult">Result of pinging an internet target (e.g., 8.8.8.8)</param>
/// <param name="Timestamp">When this status was computed</param>
/// <param name="Message">Human-readable status message</param>
public sealed record NetworkStatus(
    NetworkHealth Health,
    PingResult? RouterResult,
    PingResult? InternetResult,
    DateTimeOffset Timestamp,
    string Message)
{
    /// <summary>
    /// Quick check if network is usable (Excellent, Good, or Degraded).
    /// </summary>
    public bool IsUsable => Health is NetworkHealth.Excellent
                            or NetworkHealth.Good
                            or NetworkHealth.Degraded;
}
EOF
log_success "NetworkHealth enum fixed with proper ordering"

# =============================================================================
# Fix 2: NetworkHealthTests - Update test to use proper assertions
# =============================================================================
log_info "Fixing NetworkHealthTests..."

cat > "$SRC_DIR/NetworkMonitor.Tests/Models/NetworkHealthTests.cs" << 'EOF'
using NetworkMonitor.Core.Models;
using Xunit;

namespace NetworkMonitor.Tests.Models;

/// <summary>
/// Tests for NetworkHealth enum values.
/// </summary>
public sealed class NetworkHealthTests
{
    [Fact]
    public void NetworkHealth_HasExpectedValues()
    {
        // Assert all expected values exist and have correct numeric values
        // Ordered from worst (0) to best (4)
        Assert.Equal(0, (int)NetworkHealth.Offline);
        Assert.Equal(1, (int)NetworkHealth.Poor);
        Assert.Equal(2, (int)NetworkHealth.Degraded);
        Assert.Equal(3, (int)NetworkHealth.Good);
        Assert.Equal(4, (int)NetworkHealth.Excellent);
    }

    [Fact]
    public void NetworkHealth_ValuesAreDefined()
    {
        // Assert all expected values are defined in the enum
        Assert.True(Enum.IsDefined(NetworkHealth.Offline));
        Assert.True(Enum.IsDefined(NetworkHealth.Poor));
        Assert.True(Enum.IsDefined(NetworkHealth.Degraded));
        Assert.True(Enum.IsDefined(NetworkHealth.Good));
        Assert.True(Enum.IsDefined(NetworkHealth.Excellent));
    }

    [Fact]
    public void NetworkHealth_CanCompare()
    {
        // Assert ordering works as expected (Excellent > Good > Degraded > Poor > Offline)
        Assert.True(NetworkHealth.Excellent > NetworkHealth.Good);
        Assert.True(NetworkHealth.Good > NetworkHealth.Degraded);
        Assert.True(NetworkHealth.Degraded > NetworkHealth.Poor);
        Assert.True(NetworkHealth.Poor > NetworkHealth.Offline);
    }

    [Fact]
    public void NetworkHealth_ToString_ReturnsName()
    {
        Assert.Equal("Excellent", NetworkHealth.Excellent.ToString());
        Assert.Equal("Good", NetworkHealth.Good.ToString());
        Assert.Equal("Degraded", NetworkHealth.Degraded.ToString());
        Assert.Equal("Poor", NetworkHealth.Poor.ToString());
        Assert.Equal("Offline", NetworkHealth.Offline.ToString());
    }

    [Fact]
    public void NetworkHealth_ComparisonOperators_WorkCorrectly()
    {
        // Test various comparison operators
        Assert.True(NetworkHealth.Excellent >= NetworkHealth.Excellent);
        Assert.True(NetworkHealth.Excellent >= NetworkHealth.Good);
        Assert.False(NetworkHealth.Good >= NetworkHealth.Excellent);
        
        Assert.True(NetworkHealth.Offline <= NetworkHealth.Offline);
        Assert.True(NetworkHealth.Offline <= NetworkHealth.Poor);
        Assert.False(NetworkHealth.Poor <= NetworkHealth.Offline);
        
        Assert.True(NetworkHealth.Excellent != NetworkHealth.Good);
        Assert.True(NetworkHealth.Excellent == NetworkHealth.Excellent);
    }
}
EOF
log_success "NetworkHealthTests fixed"

# =============================================================================
# Fix 3: NetworkConfigurationServiceTests - Fix internet target test
# =============================================================================
# The test expects "1.1.1.1" but the service is returning "8.8.8.8"
# This is because the FakeInternetTargetProvider has "8.8.8.8" as default
# We need to configure the fake properly in the test
# =============================================================================
log_info "Fixing NetworkConfigurationServiceTests..."

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
    public async Task GetRouterAddressAsync_WhenAutoDetect_UsesGatewayDetector()
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
    public async Task GetRouterAddressAsync_WhenAutoDetectFails_TriesCommonGateways()
    {
        // Arrange
        _gatewayDetector.WithGateway(null); // No auto-detected gateway
        _gatewayDetector.WithCommonGateways("192.168.0.1", "192.168.1.1", "10.0.0.1");
        _pingService.AlwaysSucceed(5);
        var options = new MonitorOptions { RouterAddress = "auto" };
        var service = CreateService(options);

        // Act
        var result = await service.GetRouterAddressAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("192.168.0.1", result); // First common gateway that responds
    }

    [Fact]
    public async Task GetInternetTargetAsync_ReturnsPrimaryTarget()
    {
        // Arrange - Configure the fake to use "1.1.1.1" as primary
        _internetTargetProvider.WithPrimaryTarget("1.1.1.1");
        _internetTargetProvider.WithTargets("1.1.1.1", "8.8.8.8");
        _pingService.AlwaysSucceed(5);
        var options = new MonitorOptions { EnableFallbackTargets = false };
        var service = CreateService(options);

        // Act
        var result = await service.GetInternetTargetAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("1.1.1.1", result);
    }

    [Fact]
    public async Task GetInternetTargetAsync_WhenFallbackEnabled_ReturnsFirstReachable()
    {
        // Arrange
        _internetTargetProvider.WithTargets("8.8.8.8", "1.1.1.1");
        _pingService.AlwaysSucceed(5);
        var options = new MonitorOptions { EnableFallbackTargets = true };
        var service = CreateService(options);

        // Act
        var result = await service.GetInternetTargetAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("8.8.8.8", result); // First reachable target
    }

    [Fact]
    public async Task GetInternetTargetAsync_WhenPrimaryUnreachable_UsesFallback()
    {
        // Arrange
        _internetTargetProvider.WithTargets("8.8.8.8", "1.1.1.1");
        // First target fails, second succeeds
        _pingService.QueueResult(PingResult.Failed("8.8.8.8", "Timeout"));
        _pingService.QueueResult(PingResult.Succeeded("1.1.1.1", 10));
        var options = new MonitorOptions { EnableFallbackTargets = true };
        var service = CreateService(options);

        // Act
        var result = await service.GetInternetTargetAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("1.1.1.1", result);
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
log_success "NetworkConfigurationServiceTests fixed"

# =============================================================================
# Fix 4: NetworkMonitorServiceTests - Fix the internet fails test
# =============================================================================
# The test expects Degraded or Poor when internet fails
# Looking at ComputeHealth logic:
# - If internet fails and router succeeds: returns Poor
# - If internet fails and router null/fails: returns Offline
# The test needs to properly set up the router result
# =============================================================================
log_info "Fixing NetworkMonitorServiceTests..."

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
/// </summary>
public sealed class NetworkMonitorServiceTests : IDisposable
{
    private readonly FakePingService _pingService;
    private readonly FakeNetworkConfigurationService _configService;
    private readonly MonitorOptions _options;

    public NetworkMonitorServiceTests()
    {
        _pingService = new FakePingService();
        _configService = new FakeNetworkConfigurationService();
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
            Options.Create(options ?? _options),
            NullLogger<NetworkMonitorService>.Instance);
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
    public async Task CheckNetworkAsync_WhenInternetFails_ReturnsDegradedOrPoor()
    {
        // Arrange
        _configService.WithRouterAddress("192.168.1.1");
        _configService.WithInternetTarget("8.8.8.8");
        
        // Router succeeds, internet fails
        _pingService.QueueResult(PingResult.Succeeded("192.168.1.1", 5));
        _pingService.QueueResult(PingResult.Failed("8.8.8.8", "Timeout"));
        
        var service = CreateService();

        // Act
        var status = await service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert - Router OK but no internet = Poor (not Degraded)
        Assert.True(
            status.Health is NetworkHealth.Degraded or NetworkHealth.Poor,
            $"Expected Degraded or Poor but got {status.Health}");
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenBothFail_ReturnsOffline()
    {
        // Arrange
        _configService.WithRouterAddress("192.168.1.1");
        _configService.WithInternetTarget("8.8.8.8");
        
        // Both fail
        _pingService.QueueResult(PingResult.Failed("192.168.1.1", "Timeout"));
        _pingService.QueueResult(PingResult.Failed("8.8.8.8", "Timeout"));
        
        var service = CreateService();

        // Act
        var status = await service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(NetworkHealth.Offline, status.Health);
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenNoRouter_StillChecksInternet()
    {
        // Arrange
        _configService.WithRouterAddress(null); // No router configured
        _configService.WithInternetTarget("8.8.8.8");
        
        _pingService.QueueResult(PingResult.Succeeded("8.8.8.8", 10));
        
        var service = CreateService();

        // Act
        var status = await service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert - Should still work without router
        Assert.True(status.Health >= NetworkHealth.Degraded);
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenHighLatency_ReturnsDegradedOrPoor()
    {
        // Arrange
        _configService.WithRouterAddress("192.168.1.1");
        _configService.WithInternetTarget("8.8.8.8");
        
        // High latency (above GoodLatencyMs of 50)
        _pingService.QueueResult(PingResult.Succeeded("192.168.1.1", 5));
        _pingService.QueueResult(PingResult.Succeeded("8.8.8.8", 250));
        
        var service = CreateService();

        // Act
        var status = await service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(
            status.Health is NetworkHealth.Degraded or NetworkHealth.Poor,
            $"Expected Degraded or Poor for high latency but got {status.Health}");
    }

    [Fact]
    public async Task CheckNetworkAsync_RaisesStatusChangedEvent()
    {
        // Arrange
        _configService.WithRouterAddress("192.168.1.1");
        _configService.WithInternetTarget("8.8.8.8");
        
        _pingService.QueueResult(PingResult.Succeeded("192.168.1.1", 5));
        _pingService.QueueResult(PingResult.Succeeded("8.8.8.8", 10));
        
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
}
EOF
log_success "NetworkMonitorServiceTests fixed"

# =============================================================================
# Ensure FakeInternetTargetProvider has proper methods
# =============================================================================
log_info "Ensuring FakeInternetTargetProvider is properly configured..."

cat > "$SRC_DIR/NetworkMonitor.Tests/Fakes/FakeInternetTargetProvider.cs" << 'EOF'
using NetworkMonitor.Core.Services;

namespace NetworkMonitor.Tests.Fakes;

/// <summary>
/// Fake internet target provider for testing.
/// </summary>
public sealed class FakeInternetTargetProvider : IInternetTargetProvider
{
    private string _primaryTarget = "8.8.8.8";
    private List<string> _targets = ["8.8.8.8", "1.1.1.1", "208.67.222.222"];

    public string PrimaryTarget => _primaryTarget;

    public FakeInternetTargetProvider WithPrimaryTarget(string target)
    {
        _primaryTarget = target;
        // Ensure primary is first in the targets list
        if (!_targets.Contains(target))
        {
            _targets.Insert(0, target);
        }
        else
        {
            _targets.Remove(target);
            _targets.Insert(0, target);
        }
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

    public IReadOnlyList<string> GetTargets() => _targets;
}
EOF
log_success "FakeInternetTargetProvider updated"

# =============================================================================
# Ensure FakeNetworkConfigurationService is properly configured
# =============================================================================
log_info "Ensuring FakeNetworkConfigurationService is properly configured..."

cat > "$SRC_DIR/NetworkMonitor.Tests/Fakes/FakeNetworkConfigurationService.cs" << 'EOF'
using NetworkMonitor.Core.Services;

namespace NetworkMonitor.Tests.Fakes;

/// <summary>
/// Fake network configuration service for testing.
/// Returns configurable addresses without actual network operations.
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

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public void Dispose()
    {
        // Nothing to dispose in fake
    }
}
EOF
log_success "FakeNetworkConfigurationService updated"

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
    log_error "Some tests failed. Please review the output above."
    exit 1
fi

# =============================================================================
# Summary
# =============================================================================
echo ""
echo "============================================================================="
echo -e "${GREEN}Fix Summary${NC}"
echo "============================================================================="
echo ""
echo "Fixed 3 test failures:"
echo ""
echo "1. NetworkHealth_CanCompare"
echo "   - Root cause: Enum values were ordered Excellent=0, Good=1, etc."
echo "   - Fix: Reversed order so Offline=0, Poor=1, ..., Excellent=4"
echo "   - Now: Excellent > Good > Degraded > Poor > Offline works correctly"
echo ""
echo "2. GetInternetTargetAsync_ReturnsPrimaryTarget"
echo "   - Root cause: Test expected '1.1.1.1' but didn't configure fake"
echo "   - Fix: Added WithPrimaryTarget('1.1.1.1') and WithTargets() to test"
echo "   - Now: Test properly configures the fake before asserting"
echo ""
echo "3. CheckNetworkAsync_WhenInternetFails_ReturnsDegradedOrPoor"
echo "   - Root cause: Test didn't properly queue ping results"
echo "   - Fix: Properly queue router success + internet failure"
echo "   - Now: Test correctly validates Poor/Degraded response"
echo ""
echo "Additional improvements:"
echo "   - Added NetworkHealth_ValuesAreDefined test using generic Enum.IsDefined<T>()"
echo "   - Added NetworkHealth_ComparisonOperators_WorkCorrectly test"
echo "   - Added more comprehensive NetworkMonitorService tests"
echo "   - Added status change event tests with previous status validation"
echo "   - Added cancellation support test"
echo ""
echo "============================================================================="
