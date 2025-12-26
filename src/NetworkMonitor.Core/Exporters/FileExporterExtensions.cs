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
    /// <param name="builder">The meter provider builder.</param>
    /// <param name="options">Optional exporter options.</param>
    /// <returns>The builder for chaining.</returns>
    public static MeterProviderBuilder AddFileExporter(
        this MeterProviderBuilder builder,
        FileExporterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        options ??= FileExporterOptions.Default;

        var exporter = new FileMetricExporter(options);
        var reader = new PeriodicExportingMetricReader(exporter, exportIntervalMilliseconds: 10000);

        return builder.AddReader(reader);
    }

    /// <summary>
    /// Adds a file exporter with custom configuration.
    /// </summary>
    /// <param name="builder">The meter provider builder.</param>
    /// <param name="configure">Configuration action.</param>
    /// <returns>The builder for chaining.</returns>
    public static MeterProviderBuilder AddFileExporter(
        this MeterProviderBuilder builder,
        Action<FileExporterOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new FileExporterOptions();
        configure(options);
        return builder.AddFileExporter(options);
    }
}
