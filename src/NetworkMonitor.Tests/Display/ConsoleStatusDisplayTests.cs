using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Display;
using NetworkMonitor.Core.Models;
using Xunit;

namespace NetworkMonitor.Tests.Display;

public sealed class ConsoleStatusDisplayTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static TargetCheckResult Ok(MonitorTarget target, long latency) => new(target, true, latency, latency, latency, 0, 0);

    private static (ConsoleStatusDisplay Display, StringWriter Output) Create(bool quiet)
    {
        var output = new StringWriter();
        return (new ConsoleStatusDisplay(Options.Create(new MonitorOptions { QuietConsole = quiet }), new LiveConsole(output, ansiEnabled: false)), output);
    }

    private static NetworkStatus Status(params TargetCheckResult[] targets) =>
        new(NetworkHealth.Excellent, "ok", Now, targets);

    [Fact]
    public void Quiet_HealthyCycle_WritesSingleStatusLine()
    {
        var (display, output) = Create(quiet: true);
        using (output)
        {
            display.Update(Status(Ok(MonitorTarget.Router("192.168.1.1"), 2), Ok(MonitorTarget.Internet("8.8.8.8"), 12)));

            var text = output.ToString();
            Assert.StartsWith("● Excellent  Router:    2ms Internet:   12ms [", text, StringComparison.Ordinal);
            Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }
    }

    [Fact]
    public void Quiet_ProblemTarget_ListsItWithError()
    {
        var (display, output) = Create(quiet: true);
        using (output)
        {
            var failing = TargetCheckResult.Failed(new MonitorTarget("GitHub", "github.com", TargetCategory.Custom), "TimedOut");
            display.Update(Status(Ok(MonitorTarget.Internet("8.8.8.8"), 12), failing));

            var text = output.ToString();
            Assert.Contains("Router:   --  Internet:", text, StringComparison.Ordinal);
            Assert.Contains("Targets: 0/1", text, StringComparison.Ordinal);
            Assert.Contains("1 target(s) need attention", text, StringComparison.Ordinal);
            Assert.Contains("FAIL: TimedOut", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Verbose_ListsEveryTarget()
    {
        var (display, output) = Create(quiet: false);
        using (output)
        {
            display.Update(Status(Ok(MonitorTarget.Router("192.168.1.1"), 2), TargetCheckResult.Failed(MonitorTarget.Internet("8.8.8.8"), "TimedOut")));

            var text = output.ToString();
            Assert.Contains("Internet: FAIL   ", text, StringComparison.Ordinal);
            Assert.Contains("    " + "   2ms Router", text, StringComparison.Ordinal);
            Assert.Contains(" FAIL  Internet", text, StringComparison.Ordinal);
        }
    }
}
