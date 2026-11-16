using System.Text.Json;
using NetworkMonitor.Core.Telemetry;
using NetworkMonitor.Tests.Fakes;
using Xunit;

namespace NetworkMonitor.Tests.Telemetry;

public sealed class RollingJsonArrayWriterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nm-telemetry-").FullName;
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 16, 23, 0, 0, TimeSpan.Zero));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string ReadIds(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return string.Join(',', document.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetRawText()));
    }

    [Fact]
    public void Write_ProducesValidJsonArray()
    {
        using (var writer = new RollingJsonArrayWriter(_dir, "run", 1_000_000, _time))
        {
            writer.Write("""{"id":1}""");
            writer.Write("""{"id":2}""");
        }

        Assert.Equal("1,2", ReadIds(Path.Combine(_dir, "metrics_run.json")));
    }

    [Fact]
    public void Write_RotatesOnSizeAndDate_WithoutOverwritingEarlierFiles()
    {
        using (var writer = new RollingJsonArrayWriter(_dir, "run", 20, _time))
        {
            writer.Write("""{"id":1}""");
            writer.Write("""{"id":2}""");
            _time.Now = _time.Now.AddHours(2);
            writer.Write("""{"id":3}""");
        }

        Assert.Equal("1", ReadIds(Path.Combine(_dir, "metrics_run.json")));
        Assert.Equal("2", ReadIds(Path.Combine(_dir, "metrics_run_001.json")));
        Assert.Equal("3", ReadIds(Path.Combine(_dir, "metrics_run_002.json")));
    }
}
