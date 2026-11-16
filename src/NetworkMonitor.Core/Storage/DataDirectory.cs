using System.Globalization;
using NetworkMonitor.Core.Configuration;

namespace NetworkMonitor.Core.Storage;

public static class DataDirectory
{
    public static string Resolve(StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.DataDirectoryOverride) && CanWrite(options.DataDirectoryOverride))
        {
            return options.DataDirectoryOverride;
        }

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdgDataHome) && CanWrite(xdgDataHome))
        {
            return Path.Combine(xdgDataHome, options.ApplicationName);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData) && CanWrite(localAppData))
        {
            return Path.Combine(localAppData, options.ApplicationName);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            var localShare = Path.Combine(home, ".local", "share");
            if (CanWrite(localShare) || CanWrite(home))
            {
                return Path.Combine(localShare, options.ApplicationName);
            }
        }

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(Environment.CurrentDirectory, $"{options.ApplicationName}_{timestamp}");
    }

    private static bool CanWrite(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".write_test_{Guid.NewGuid():N}");
            File.WriteAllText(probe, "test");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
