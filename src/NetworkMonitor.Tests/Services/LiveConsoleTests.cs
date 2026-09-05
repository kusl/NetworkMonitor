using NetworkMonitor.Core.Services;
using Xunit;

namespace NetworkMonitor.Tests.Services;

/// <summary>
/// Tests for <see cref="LiveConsole"/>, the single synchronized owner of stdout.
/// These verify the core guarantee: a log record (or any permanent block) never
/// shares a line with the live status line, in both redirected and terminal modes.
/// </summary>
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

        // The status line and the log record must never share a line.
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

        // Save cursor + STATUS (parked), then restore + erase-to-end-of-screen
        // before the log, so LOG is not appended to the status line.
        Assert.Equal("\x1b[sSTATUS\x1b[u\x1b[JLOG\n", writer.ToString());
    }

    [Fact]
    public void WriteBlock_AlreadyEndingWithNewline_DoesNotDoubleTerminate()
    {
        using var writer = new StringWriter();
        var console = new LiveConsole(writer, ansiEnabled: false);

        console.WriteBlock("already terminated\n");

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

    [Fact]
    public void AnsiEnabled_ReflectsConstructorArgument()
    {
        using var w1 = new StringWriter();
        using var w2 = new StringWriter();

        Assert.False(new LiveConsole(w1, ansiEnabled: false).AnsiEnabled);
        Assert.True(new LiveConsole(w2, ansiEnabled: true).AnsiEnabled);
    }
}
