#!/bin/bash
# =============================================================================
# Fix Build Errors Script for NetworkMonitor
# Fixes:
#   1. CS0104: Ambiguous NullLogger<> reference (2 instances)
#   2. CA1001: NetworkMonitorServiceTests owns disposable field but is not disposable
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

# Determine the source directory
if [[ -d "src/NetworkMonitor.Core" ]]; then
    SRC_DIR="src"
elif [[ -d "NetworkMonitor.Core" ]]; then
    SRC_DIR="."
else
    log_error "Cannot find NetworkMonitor source directory"
    exit 1
fi

log_info "Working directory: $(pwd)"
log_info "Source directory: $SRC_DIR"

# =============================================================================
# Fix 1: Remove custom NullLogger (use Microsoft.Extensions.Logging.Abstractions)
# =============================================================================
log_info "Removing custom NullLogger (will use Microsoft's version)..."

NULLLOGGER_FILE="$SRC_DIR/NetworkMonitor.Tests/Fakes/NullLogger.cs"
if [[ -f "$NULLLOGGER_FILE" ]]; then
    rm "$NULLLOGGER_FILE"
    log_success "Removed custom NullLogger.cs"
else
    log_info "NullLogger.cs already removed or not found"
fi

# =============================================================================
# Fix 2: Update NetworkConfigurationServiceTests to use fully qualified NullLogger
# =============================================================================
log_info "Updating NetworkConfigurationServiceTests..."

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
        _gatewayDetector.WithCommonGateways(); // Empty
        var options = new MonitorOptions { RouterAddress = "auto" };
        var service = CreateService(options);

        // Act
        var result = await service.GetRouterAddressAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetInternetTargetAsync_ReturnsPrimaryTarget()
    {
        // Arrange
        _internetTargetProvider.WithPrimaryTarget("1.1.1.1");
        var service = CreateService();

        // Act
        var result = await service.GetInternetTargetAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("1.1.1.1", result);
    }

    [Fact]
    public async Task GetRouterAddressAsync_CachesResult()
    {
        // Arrange
        _gatewayDetector.WithGateway("192.168.1.1");
        _pingService.AlwaysSucceed(5);
        var options = new MonitorOptions { RouterAddress = "auto" };
        var service = CreateService(options);

        // Act - call twice
        var result1 = await service.GetRouterAddressAsync(TestContext.Current.CancellationToken);
        
        // Change the gateway - should not affect second call due to caching
        _gatewayDetector.WithGateway("10.0.0.1");
        var result2 = await service.GetRouterAddressAsync(TestContext.Current.CancellationToken);

        // Assert - both should return cached value
        Assert.Equal("192.168.1.1", result1);
        Assert.Equal("192.168.1.1", result2);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        // Arrange
        var service = CreateService();

        // Act & Assert - should not throw
        service.Dispose();
        service.Dispose();
    }

    [Fact]
    public async Task GetRouterAddressAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        var service = CreateService();
        service.Dispose();
        _service = null; // Prevent double dispose in cleanup

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => service.GetRouterAddressAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetInternetTargetAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        var service = CreateService();
        service.Dispose();
        _service = null; // Prevent double dispose in cleanup

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => service.GetInternetTargetAsync(TestContext.Current.CancellationToken));
    }
}
EOF
log_success "NetworkConfigurationServiceTests updated"

# =============================================================================
# Fix 3: Update NetworkMonitorServiceTests to implement IDisposable
# =============================================================================
log_info "Updating NetworkMonitorServiceTests to implement IDisposable..."

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
public sealed class NetworkMonitorServiceTests : IDisposable
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

    public void Dispose()
    {
        _configService.Dispose();
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
            .AlwaysSucceed(latencyMs: 10); // Internet succeeds

        // Act
        var status = await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(status.Health is NetworkHealth.Offline or NetworkHealth.Degraded or NetworkHealth.Poor);
        Assert.False(status.RouterResult?.Success);
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenInternetFails_ReturnsDegradedOrPoor()
    {
        // Arrange - router succeeds, internet fails
        _pingService
            .QueueResult(PingResult.Succeeded("192.168.1.1", 5))
            .QueueResult(PingResult.Failed("8.8.8.8", "Timeout"))
            .QueueResult(PingResult.Failed("8.8.8.8", "Timeout"))
            .QueueResult(PingResult.Failed("8.8.8.8", "Timeout"));

        // Act
        var status = await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(status.Health is NetworkHealth.Degraded or NetworkHealth.Poor or NetworkHealth.Offline);
        Assert.True(status.RouterResult?.Success);
        Assert.False(status.InternetResult?.Success);
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenBothFail_ReturnsOffline()
    {
        // Arrange - both fail
        _pingService.AlwaysFail("Network unreachable");

        // Act
        var status = await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(NetworkHealth.Offline, status.Health);
        Assert.False(status.RouterResult?.Success);
        Assert.False(status.InternetResult?.Success);
    }

    [Fact]
    public async Task CheckNetworkAsync_WhenNoRouter_StillChecksInternet()
    {
        // Arrange - no router configured
        _configService.WithRouterAddress(null);
        _pingService.AlwaysSucceed(latencyMs: 10);

        // Act
        var status = await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(status.RouterResult);
        Assert.NotNull(status.InternetResult);
        Assert.True(status.InternetResult.Success);
    }

    [Fact]
    public async Task CheckNetworkAsync_HighLatency_ReturnsGoodOrDegraded()
    {
        // Arrange
        _pingService.AlwaysSucceed(latencyMs: 150);

        // Act
        var status = await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(status.Health is NetworkHealth.Good or NetworkHealth.Degraded);
    }

    [Fact]
    public async Task StatusChanged_FiresWhenHealthChanges()
    {
        // Arrange
        NetworkStatusEventArgs? receivedArgs = null;
        _service.StatusChanged += (_, args) => receivedArgs = args;
        _pingService.AlwaysSucceed(latencyMs: 5);

        // Act - first check establishes baseline
        await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);
        
        // Change to failing
        _pingService.AlwaysFail("Network error");
        await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(receivedArgs);
        Assert.NotNull(receivedArgs.CurrentStatus);
    }

    [Fact]
    public async Task StatusChanged_IncludesPreviousStatus()
    {
        // Arrange
        var receivedArgs = new List<NetworkStatusEventArgs>();
        _service.StatusChanged += (_, args) => receivedArgs.Add(args);
        _pingService.AlwaysSucceed(latencyMs: 5);

        // Act - first check
        await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);
        
        // Second check - should have previous
        _pingService.AlwaysFail("Timeout");
        await _service.CheckNetworkAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(receivedArgs.Count >= 1);
        // The second event should have a previous status
        if (receivedArgs.Count > 1)
        {
            Assert.NotNull(receivedArgs[1].PreviousStatus);
        }
    }

    [Fact]
    public async Task CheckNetworkAsync_CancellationToken_Respected()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.CheckNetworkAsync(cts.Token));
    }
}
EOF
log_success "NetworkMonitorServiceTests updated with IDisposable"

# =============================================================================
# Update InternetTargetProviderTests to use Microsoft's NullLogger
# =============================================================================
log_info "Updating InternetTargetProviderTests..."

cat > "$SRC_DIR/NetworkMonitor.Tests/Services/InternetTargetProviderTests.cs" << 'EOF'
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
}
EOF
log_success "InternetTargetProviderTests updated"

# =============================================================================
# Ensure FakeNetworkConfigurationService implements IDisposable
# =============================================================================
log_info "Ensuring FakeNetworkConfigurationService implements IDisposable..."

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

    public void Dispose()
    {
        // Nothing to dispose in fake
    }
}
EOF
log_success "FakeNetworkConfigurationService updated"

# =============================================================================
# Ensure FakeInternetTargetProvider exists with proper methods
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
    private List<string> _targets = new() { "8.8.8.8", "1.1.1.1", "208.67.222.222" };

    public string PrimaryTarget => _primaryTarget;

    public FakeInternetTargetProvider WithPrimaryTarget(string target)
    {
        _primaryTarget = target;
        if (!_targets.Contains(target))
        {
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
# Add NetworkStatusEventArgsTests for better coverage
# =============================================================================
log_info "Adding NetworkStatusEventArgsTests..."

mkdir -p "$SRC_DIR/NetworkMonitor.Tests/Models"

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
# Add PingResultTests for better coverage
# =============================================================================
log_info "Adding PingResultTests..."

cat > "$SRC_DIR/NetworkMonitor.Tests/Models/PingResultTests.cs" << 'EOF'
using NetworkMonitor.Core.Models;
using Xunit;

namespace NetworkMonitor.Tests.Models;

/// <summary>
/// Tests for PingResult.
/// </summary>
public sealed class PingResultTests
{
    [Fact]
    public void Succeeded_CreatesSuccessfulResult()
    {
        // Act
        var result = PingResult.Succeeded("8.8.8.8", 15);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("8.8.8.8", result.Target);
        Assert.Equal(15, result.LatencyMs);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void Failed_CreatesFailedResult()
    {
        // Act
        var result = PingResult.Failed("8.8.8.8", "Request timed out");

        // Assert
        Assert.False(result.Success);
        Assert.Equal("8.8.8.8", result.Target);
        Assert.Null(result.LatencyMs);
        Assert.Equal("Request timed out", result.ErrorMessage);
    }

    [Fact]
    public void Timestamp_IsSetToCurrentTime()
    {
        // Arrange
        var before = DateTimeOffset.UtcNow;

        // Act
        var result = PingResult.Succeeded("8.8.8.8", 10);

        // Assert
        var after = DateTimeOffset.UtcNow;
        Assert.True(result.Timestamp >= before);
        Assert.True(result.Timestamp <= after);
    }

    [Fact]
    public void Succeeded_WithZeroLatency_IsValid()
    {
        // Act
        var result = PingResult.Succeeded("localhost", 0);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(0, result.LatencyMs);
    }
}
EOF
log_success "PingResultTests added"

# =============================================================================
# Add NetworkHealthTests for better coverage
# =============================================================================
log_info "Adding NetworkHealthTests..."

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
        // Assert all expected values exist
        Assert.Equal(0, (int)NetworkHealth.Offline);
        Assert.Equal(1, (int)NetworkHealth.Poor);
        Assert.Equal(2, (int)NetworkHealth.Degraded);
        Assert.Equal(3, (int)NetworkHealth.Good);
        Assert.Equal(4, (int)NetworkHealth.Excellent);
    }

    [Fact]
    public void NetworkHealth_CanCompare()
    {
        // Assert ordering works as expected
        Assert.True(NetworkHealth.Excellent > NetworkHealth.Good);
        Assert.True(NetworkHealth.Good > NetworkHealth.Degraded);
        Assert.True(NetworkHealth.Degraded > NetworkHealth.Poor);
        Assert.True(NetworkHealth.Poor > NetworkHealth.Offline);
    }
}
EOF
log_success "NetworkHealthTests added"

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
echo "Fixed 3 build errors:"
echo ""
echo "1. CS0104: Ambiguous NullLogger<> reference (NetworkConfigurationServiceTests)"
echo "   - Removed custom NullLogger.cs from Fakes folder"
echo "   - Now using Microsoft.Extensions.Logging.Abstractions.NullLogger<T>"
echo ""
echo "2. CS0104: Ambiguous NullLogger<> reference (NetworkMonitorServiceTests)"
echo "   - Same fix as above - using Microsoft's NullLogger<T>"
echo ""
echo "3. CA1001: NetworkMonitorServiceTests owns disposable field '_configService'"
echo "   - Made NetworkMonitorServiceTests implement IDisposable"
echo "   - Added Dispose() method that disposes _configService"
echo ""
echo "Added/Updated test files for better coverage:"
echo "   - NetworkStatusEventArgsTests.cs"
echo "   - PingResultTests.cs"
echo "   - NetworkHealthTests.cs"
echo "   - NetworkConfigurationServiceTests.cs (with dispose tests)"
echo "   - NetworkMonitorServiceTests.cs (implements IDisposable)"
echo "   - InternetTargetProviderTests.cs"
echo ""
echo "Updated Fakes:"
echo "   - Removed NullLogger.cs (using Microsoft's version)"
echo "   - FakeNetworkConfigurationService.cs (implements IDisposable)"
echo "   - FakeInternetTargetProvider.cs (fluent configuration)"
echo ""
echo "============================================================================="
