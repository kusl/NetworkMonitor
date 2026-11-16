using System.Text;

namespace NetworkMonitor.Core.Sync;

public static class SqlIdentifier
{
    public static string Sanitize(string? name, string fallback, bool dashToUnderscore = false)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                builder.Append(ch);
            }
            else if (dashToUnderscore && ch == '-')
            {
                builder.Append('_');
            }
        }

        return builder.Length == 0 || char.IsDigit(builder[0]) ? fallback : builder.ToString();
    }
}
