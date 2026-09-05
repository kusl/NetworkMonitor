using System.Globalization;
namespace NetworkMonitor.Core.Models;

/// <summary>
/// Configuration for local SQLite storage.
/// Follows XDG Base Directory Specification with graceful fallbacks.
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>
    /// Application name used for directory structure.
    /// </summary>
    public string ApplicationName { get; set; } = "NetworkMonitor";

    /// <summary>
    /// SQLite database file name (inside the resolved data directory).
    /// </summary>
    public string DatabaseFileName { get; set; } = "network-monitor.db";

    /// <summary>
    /// Explicit data directory override. When set to a writable path it is used
    /// verbatim, bypassing XDG/app-data resolution. Primarily for tests and for
    /// users who want the database in a specific location. Empty/null means
    /// "resolve automatically".
    /// </summary>
    public string? DataDirectoryOverride { get; set; }

    /// <summary>
    /// Maximum file size in bytes before rotation (25MB default). Retained for
    /// compatibility with the telemetry file exporter.
    /// </summary>
    public long MaxFileSizeBytes { get; set; } = 25 * 1024 * 1024;

    /// <summary>
    /// How many days of data to retain in SQLite.
    /// Default: 30 days
    /// </summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>
    /// How often the periodic retention prune runs, measured in successful saves.
    /// The prune deletes cycles/measurements older than <see cref="RetentionDays"/>
    /// relative to the current time, then reclaims free pages.
    ///
    /// A fixed cadence (rather than a random draw) makes this destructive step
    /// deterministic: every Nth save triggers a prune, so behaviour is predictable
    /// in production and reproducible in tests. Set to <c>0</c> to disable the
    /// periodic prune entirely (used by round-trip tests whose fixture timestamps
    /// are intentionally far in the past and must not be swept away by retention).
    ///
    /// Default: 200 (roughly one prune per few hours at typical cycle intervals).
    /// </summary>
    public int PruneEveryNSaves { get; set; } = 200;

    /// <summary>
    /// Get the data directory following XDG specification with fallbacks.
    /// Priority:
    /// 0. DataDirectoryOverride (if set and writable)
    /// 1. XDG_DATA_HOME (Linux)
    /// 2. LocalApplicationData (Windows/macOS)
    /// 3. ~/.local/share (Linux fallback)
    /// 4. Current directory (final fallback)
    /// </summary>
    public string GetDataDirectory()
    {
        // Explicit override wins when it is usable.
        if (!string.IsNullOrWhiteSpace(DataDirectoryOverride) &&
            CanWriteToDirectory(DataDirectoryOverride))
        {
            return DataDirectoryOverride;
        }

        // Try XDG_DATA_HOME first (Linux)
        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdgDataHome) && CanWriteToDirectory(xdgDataHome))
        {
            return Path.Combine(xdgDataHome, ApplicationName);
        }

        // Try platform-specific app data
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData) && CanWriteToDirectory(localAppData))
        {
            return Path.Combine(localAppData, ApplicationName);
        }

        // Try ~/.local/share (Linux fallback)
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(homeDir))
        {
            var localShare = Path.Combine(homeDir, ".local", "share");
            if (CanWriteToDirectory(localShare) || CanWriteToDirectory(homeDir))
            {
                return Path.Combine(localShare, ApplicationName);
            }
        }

        // Final fallback: current directory with timestamp subfolder
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(Environment.CurrentDirectory, $"{ApplicationName}_{timestamp}");
    }

    private static bool CanWriteToDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            // Test write access
            var testFile = Path.Combine(path, $".write_test_{Guid.NewGuid()}");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
