using System.Globalization;
using System.Text;

namespace NetworkMonitor.Core.Telemetry;

public sealed class RollingJsonArrayWriter(string directory, string runId, long maxFileSizeBytes, TimeProvider timeProvider) : IDisposable
{
    private StreamWriter? _writer;
    private DateTime _fileDate;
    private long _size;
    private int _fileNumber = -1;
    private bool _first = true;

    public string? CurrentPath { get; private set; }

    public void Write(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var bytes = Encoding.UTF8.GetByteCount(json) + 2;
        var today = timeProvider.GetUtcNow().UtcDateTime.Date;

        if (_writer is null || _fileDate != today || _size + bytes > maxFileSizeBytes)
        {
            Open(today);
        }

        if (!_first)
        {
            _writer!.WriteLine(",");
        }

        _first = false;
        _writer!.Write(json);
        _size += bytes;
    }

    public void Flush() => _writer?.Flush();

    public void Dispose() => Close();

    private void Open(DateTime date)
    {
        Close();
        _fileNumber++;
        _fileDate = date;
        CurrentPath = Path.Combine(
            directory,
            _fileNumber == 0
                ? $"metrics_{runId}.json"
                : string.Create(CultureInfo.InvariantCulture, $"metrics_{runId}_{_fileNumber:D3}.json"));
        _writer = new StreamWriter(CurrentPath, append: false, Encoding.UTF8);
        _writer.WriteLine("[");
        _size = 2;
        _first = true;
    }

    private void Close()
    {
        if (_writer is null)
        {
            return;
        }

        _writer.WriteLine();
        _writer.WriteLine("]");
        _writer.Dispose();
        _writer = null;
    }
}
