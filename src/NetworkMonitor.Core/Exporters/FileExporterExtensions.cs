using OpenTelemetry.Metrics;

namespace NetworkMonitor.Core.Exporters;

/// <summary>
/// Extension methods for registering file exporters.
/// </summary>
public static class FileExporterExtensions
{
    /// <summary>
    /// Adds a file exporter for metrics.
    /// </summary>
    public static MeterProviderBuilder AddFileExporter(
        this MeterProviderBuilder builder,
        FileExporterOptions? options = null)
    {
        options ??= FileExporterOptions.Default;

        return builder.AddReader(
            new PeriodicExportingMetricReader(
                new FileMetricExporter(options),
                exportIntervalMilliseconds: 10000));
    }

    /// <summary>
    /// Adds a file exporter with custom configuration.
    /// </summary>
    public static MeterProviderBuilder AddFileExporter(
        this MeterProviderBuilder builder,
        Action<FileExporterOptions> configure)
    {
        var options = new FileExporterOptions();
        configure(options);
        return builder.AddFileExporter(options);
    }
}
