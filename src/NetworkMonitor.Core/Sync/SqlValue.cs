namespace NetworkMonitor.Core.Sync;

public union SqlValue(long, double, string);

public static class Sql
{
    public static SqlValue Of(long value) => new(value);

    public static SqlValue Of(double value) => new(value);

    public static SqlValue Of(string value) => new(value);

    public static SqlValue Of(long? value) => value is { } v ? new SqlValue(v) : default;

    public static SqlValue Of(double? value) => value is { } v ? new SqlValue(v) : default;
}
