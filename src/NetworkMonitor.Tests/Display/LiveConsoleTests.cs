using NetworkMonitor.Core.Display;
using Xunit;

namespace NetworkMonitor.Tests.Display;

public sealed class LiveConsoleTests
{
    [Fact]
    public void WriteTransientLine_WhenRedirected_TerminatesEachLineWithNewline()
    {
        using var writer = new StringWriter();
        var console = new LiveConsole(writer, ansiEnabled: false);

        console.WriteTransientLine("status one");
        console.WriteTransientLine("status two");

        Assert.Equal("status one\nstatus two\n", writer.ToString());
    }

    [Fact]
    public void WriteBlock_AfterTransient_WhenRedirected_KeepsLinesSeparate()
    {
        using var writer = new StringWriter();
        var console = new LiveConsole(writer, ansiEnabled: false);

        console.WriteTransientLine("● Excellent  Internet: 16ms [20:09:09]");
        console.WriteBlock("info: NetworkMonitor[0]\n      Network status changed");

        var lines = writer.ToString().Split('\n');
        Assert.Equal("● Excellent  Internet: 16ms [20:09:09]", lines[0]);
        Assert.Equal("info: NetworkMonitor[0]", lines[1]);
        Assert.Equal("      Network status changed", lines[2]);
    }

    [Fact]
    public void WriteBlock_AfterTransient_WhenAnsi_ErasesParkedLineBeforeWriting()
    {
        using var writer = new StringWriter();
        var console = new LiveConsole(writer, ansiEnabled: true);

        console.WriteTransientLine("STATUS");
        console.WriteBlock("LOG");

        Assert.Equal("\x1b[sSTATUS\x1b[u\x1b[JLOG\n", writer.ToString());
    }

    [Fact]
    public void WriteBlock_AlreadyEndingWithNewline_DoesNotDoubleTerminate()
    {
        using var writer = new StringWriter();
        new LiveConsole(writer, ansiEnabled: false).WriteBlock("already terminated\n");

        Assert.Equal("already terminated\n", writer.ToString());
    }

    [Fact]
    public void Reset_WhenAnsiParked_RestoresAndErases()
    {
        using var writer = new StringWriter();
        var console = new LiveConsole(writer, ansiEnabled: true);

        console.WriteTransientLine("STATUS");
        console.Reset();

        Assert.Equal("\x1b[sSTATUS\x1b[u\x1b[J", writer.ToString());
    }
}
