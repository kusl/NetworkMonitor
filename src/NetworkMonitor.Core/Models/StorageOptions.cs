using System.Globalization;
namespace NetworkMonitor.Core.Models;

/// <summary>
/// Configuration for telemetry storage.
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
    /// Maximum file size in bytes before rotation (25MB default).
    /// </summary>
    public long MaxFileSizeBytes { get; set; } = 25 * 1024 * 1024;

    /// <summary>
    /// How many days of data to retain in SQLite.
    /// Default: 30 days
    /// </summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>
    /// Get the data directory following XDG specification with fallbacks.
    /// Priority:
    /// 1. XDG_DATA_HOME (Linux)
    /// 2. LocalApplicationData (Windows/macOS)
    /// 3. Current directory (final fallback)
    /// </summary>
    public string GetDataDirectory()
    {
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
