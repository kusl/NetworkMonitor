using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Monitoring;
using Xunit;

namespace NetworkMonitor.Tests.Monitoring;

public sealed class HealthEvaluatorTests
{
    private static readonly MonitorOptions Options = new();

    private static TargetCheckResult Ok(MonitorTarget target, long latency, double loss = 0) =>
        new(target, true, latency, latency, latency, 0, loss);

    private static TargetCheckResult Down(MonitorTarget target) => TargetCheckResult.Failed(target, "TimedOut");

    private static readonly MonitorTarget Router = MonitorTarget.Router("192.168.1.1");
    private static readonly MonitorTarget Internet = MonitorTarget.Internet("8.8.8.8");

    [Theory]
    [InlineData(10, NetworkHealth.Excellent)]
    [InlineData(100, NetworkHealth.Good)]
    [InlineData(500, NetworkHealth.Poor)]
    public void Evaluate_ByInternetLatency(int latency, NetworkHealth expected) =>
        Assert.Equal(expected, HealthEvaluator.Evaluate(Ok(Router, 2), Ok(Internet, latency), [], Options).Health);

    [Fact]
    public void Evaluate_InternetDownRouterUp_IsPoor() =>
        Assert.Equal(NetworkHealth.Poor, HealthEvaluator.Evaluate(Ok(Router, 2), Down(Internet), [], Options).Health);

    [Fact]
    public void Evaluate_EverythingDown_IsOffline()
    {
        var (health, message) = HealthEvaluator.Evaluate(Down(Router), Down(Internet), [], Options);

        Assert.Equal(NetworkHealth.Offline, health);
        Assert.Equal("Cannot reach router or internet", message);
    }

    [Fact]
    public void Evaluate_RouterDownInternetUp_IsDegraded() =>
        Assert.Equal(NetworkHealth.Degraded, HealthEvaluator.Evaluate(Down(Router), Ok(Internet, 10), [], Options).Health);

    [Fact]
    public void Evaluate_InternetPacketLoss_IsDegraded() =>
        Assert.Equal(NetworkHealth.Degraded, HealthEvaluator.Evaluate(Ok(Router, 2), Ok(Internet, 10, loss: 34), [], Options).Health);

    [Fact]
    public void Evaluate_SlowRouterFastInternet_AnnotatesButStaysExcellent()
    {
        var (health, message) = HealthEvaluator.Evaluate(Ok(Router, 450), Ok(Internet, 12), [], Options);

        Assert.Equal(NetworkHealth.Excellent, health);
        Assert.Contains("router replies slowly", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_SlowRouterAndInternet_IsDegraded() =>
        Assert.Equal(NetworkHealth.Degraded, HealthEvaluator.Evaluate(Ok(Router, 450), Ok(Internet, 450), [], Options).Health);

    [Fact]
    public void Evaluate_NoInternetCheck_UsesRouter() =>
        Assert.Equal(NetworkHealth.Offline, HealthEvaluator.Evaluate(Down(Router), null, [], Options).Health);

    [Fact]
    public void Evaluate_OnlyCustomTargets_DerivesFromThem()
    {
        var a = new MonitorTarget("A", "a.example", TargetCategory.Custom);
        var b = new MonitorTarget("B", "b.example", TargetCategory.Custom);

        Assert.Equal(NetworkHealth.Good, HealthEvaluator.Evaluate(null, null, [Ok(a, 5), Ok(b, 5)], Options).Health);
        Assert.Equal(NetworkHealth.Degraded, HealthEvaluator.Evaluate(null, null, [Ok(a, 5), Down(b)], Options).Health);
        Assert.Equal(NetworkHealth.Offline, HealthEvaluator.Evaluate(null, null, [Down(a), Down(b)], Options).Health);
        Assert.Equal(NetworkHealth.Good, HealthEvaluator.Evaluate(null, null, [], Options).Health);
    }
}
