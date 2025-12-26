using System.Text;
using System.Text.Json;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace NetworkMonitor.Core.Exporters;

/// <summary>
/// Exports OpenTelemetry metrics to JSON files.
/// Files are rotated based on size and date.
/// Failures are logged but don't stop the application.
/// </summary>
public sealed class FileMetricExporter : BaseExporter<Metric>
{
    private readonly FileExporterOptions _options;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string _currentFilePath = string.Empty;
    private DateTime _currentDate;
    private long _currentSize;
    private int _fileNumber;
    private bool _firstRecord = true;
    private readonly JsonSerializerOptions _jsonOptions;

    public FileMetricExporter(FileExporterOptions? options = null)
    {
        _options = options ?? FileExporterOptions.Default;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        EnsureDirectory();
    }

    public override ExportResult Export(in Batch<Metric> batch)
    {
        try
        {
            lock (_lock)
            {
                EnsureWriter();

                foreach (var metric in batch)
                {
                    foreach (var point in metric.GetMetricPoints())
                    {
                        var record = SerializeMetricPoint(metric, point);
                        var json = JsonSerializer.Serialize(record, _jsonOptions);
                        WriteJson(json);
                    }
                }

                _writer?.Flush();
            }

            return ExportResult.Success;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FileMetricExporter] Export failed: {ex.Message}");
            return ExportResult.Failure;
        }
    }

    private object SerializeMetricPoint(Metric metric, MetricPoint point)
    {
        var tags = new Dictionary<string, string?>();
        foreach (var tag in point.Tags)
        {
            tags[tag.Key] = tag.Value?.ToString();
        }

        object? value = metric.MetricType switch
        {
            MetricType.LongSum => point.GetSumLong(),
            MetricType.DoubleSum => point.GetSumDouble(),
            MetricType.LongGauge => point.GetGaugeLastValueLong(),
            MetricType.DoubleGauge => point.GetGaugeLastValueDouble(),
            MetricType.Histogram => new
            {
                Count = point.GetHistogramCount(),
                Sum = point.GetHistogramSum()
            },
            _ => null
        };

        return new
        {
            Timestamp = point.EndTime.ToString("O"),
            Name = metric.Name,
            Description = metric.Description,
            Unit = metric.Unit,
            Type = metric.MetricType.ToString(),
            Tags = tags,
            Value = value
        };
    }

    private void WriteJson(string json)
    {
        var bytes = Encoding.UTF8.GetByteCount(json) + 2;

        if (ShouldRotate(bytes))
        {
            RotateFile();
        }

        if (!_firstRecord)
        {
            _writer!.WriteLine(",");
        }
        else
        {
            _firstRecord = false;
        }

        _writer!.Write(json);
        _currentSize += bytes;
    }

    private bool ShouldRotate(long bytes) =>
        _currentSize + bytes > _options.MaxFileSizeBytes ||
        _currentDate != DateTime.UtcNow.Date;

    private void EnsureDirectory()
    {
        try
        {
            Directory.CreateDirectory(_options.Directory);
        }
        catch
        {
            // Fallback to current directory
            _options.Directory = Environment.CurrentDirectory;
        }
    }

    private void EnsureWriter()
    {
        if (_writer == null)
        {
            OpenNewFile();
        }
        else if (_currentDate != DateTime.UtcNow.Date)
        {
            RotateFile();
        }
    }

    private void OpenNewFile()
    {
        _currentDate = DateTime.UtcNow.Date;
        _fileNumber = 0;
        _currentFilePath = GetFilePath();

        _writer = new StreamWriter(_currentFilePath, append: false, Encoding.UTF8);
        _currentSize = 0;
        _firstRecord = true;

        _writer.WriteLine("[");
        _currentSize = 2;
    }

    private void RotateFile()
    {
        CloseWriter();
        _fileNumber++;
        OpenNewFile();
    }

    private string GetFilePath()
    {
        var fileName = _fileNumber == 0
            ? $"metrics_{_options.RunId}.json"
            : $"metrics_{_options.RunId}_{_fileNumber:D3}.json";
        return Path.Combine(_options.Directory, fileName);
    }

    private void CloseWriter()
    {
        if (_writer != null)
        {
            _writer.WriteLine();
            _writer.WriteLine("]");
            _writer.Flush();
            _writer.Dispose();
            _writer = null;
        }
    }

    protected override bool OnShutdown(int timeoutMilliseconds)
    {
        lock (_lock)
        {
            CloseWriter();
        }
        return true;
    }
}
