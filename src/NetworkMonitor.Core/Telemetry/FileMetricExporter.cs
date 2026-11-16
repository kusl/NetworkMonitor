using System.Globalization;
using System.Text.Json;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace NetworkMonitor.Core.Telemetry;

public sealed class FileMetricExporter : BaseExporter<Metric>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Lock _lock = new();
    private readonly RollingJsonArrayWriter _writer;

    public FileMetricExporter(FileExporterOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            Directory.CreateDirectory(options.Directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            options.Directory = Environment.CurrentDirectory;
        }

        _writer = new RollingJsonArrayWriter(options.Directory, options.RunId, options.MaxFileSizeBytes, timeProvider);
    }

    public override ExportResult Export(in Batch<Metric> batch)
    {
        try
        {
            lock (_lock)
            {
                foreach (var metric in batch)
                {
                    foreach (ref readonly var point in metric.GetMetricPoints())
                    {
                        _writer.Write(JsonSerializer.Serialize(ToRecord(metric, point), JsonOptions));
                    }
                }

                _writer.Flush();
            }

            return ExportResult.Success;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FileMetricExporter] Export failed: {ex.Message}");
            return ExportResult.Failure;
        }
    }

    protected override bool OnShutdown(int timeoutMilliseconds)
    {
        lock (_lock)
        {
            _writer.Dispose();
        }

        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_lock)
            {
                _writer.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private static object ToRecord(Metric metric, in MetricPoint point)
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
            MetricType.Histogram => new { Count = point.GetHistogramCount(), Sum = point.GetHistogramSum() },
            _ => null,
        };

        return new
        {
            Timestamp = point.EndTime.ToString("O", CultureInfo.InvariantCulture),
            metric.Name,
            metric.Description,
            metric.Unit,
            Type = metric.MetricType.ToString(),
            Tags = tags,
            Value = value,
        };
    }
}
