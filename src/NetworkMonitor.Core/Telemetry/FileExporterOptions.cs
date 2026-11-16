using System.Globalization;

namespace NetworkMonitor.Core.Telemetry;

public sealed class FileExporterOptions
{
    public string Directory { get; set; } = DefaultDirectory();

    public long MaxFileSizeBytes { get; set; } = 25 * 1024 * 1024;

    public string RunId { get; set; } = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

    private static string DefaultDirectory()
    {
        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdgDataHome))
        {
            return Path.Combine(xdgDataHome, "NetworkMonitor", "telemetry");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            return Path.Combine(localAppData, "NetworkMonitor", "telemetry");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(home)
            ? Path.Combine(home, ".local", "share", "NetworkMonitor", "telemetry")
            : Path.Combine(Environment.CurrentDirectory, "telemetry");
    }
}
