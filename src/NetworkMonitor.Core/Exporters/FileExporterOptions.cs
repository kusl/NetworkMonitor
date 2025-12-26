using System.Globalization;

namespace NetworkMonitor.Core.Exporters;

/// <summary>
/// Configuration for file-based OpenTelemetry exporters.
/// Follows XDG specification with fallbacks.
/// </summary>
public sealed class FileExporterOptions
{
    /// <summary>
    /// Directory where telemetry files will be written.
    /// Automatically determined based on XDG spec if not set.
    /// </summary>
    public string Directory { get; set; } = GetDefaultDirectory();

    /// <summary>
    /// Maximum file size before rotation (25MB default).
    /// </summary>
    public long MaxFileSizeBytes { get; set; } = 25 * 1024 * 1024;

    /// <summary>
    /// Application name for directory structure.
    /// </summary>
    public string ApplicationName { get; set; } = "NetworkMonitor";

    /// <summary>
    /// Unique run identifier for file naming.
    /// </summary>
    public string RunId { get; set; } = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

    private static string GetDefaultDirectory()
    {
        // XDG_DATA_HOME (Linux)
        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdgDataHome))
        {
            return Path.Combine(xdgDataHome, "NetworkMonitor", "telemetry");
        }

        // Platform-specific app data
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            return Path.Combine(localAppData, "NetworkMonitor", "telemetry");
        }

        // Fallback to ~/.local/share
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            return Path.Combine(home, ".local", "share", "NetworkMonitor", "telemetry");
        }

        // Final fallback: current directory
        return Path.Combine(Environment.CurrentDirectory, "telemetry");
    }

    /// <summary>
    /// Gets default options instance.
    /// </summary>
    public static FileExporterOptions Default => new();
}
