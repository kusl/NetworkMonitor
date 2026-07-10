namespace NetworkMonitor.Core.RemoteSync;

/// <summary>
/// A single SQL statement plus its positional arguments, to be sent over the
/// libSQL HTTP "Hrana" pipeline. Use <c>?</c> placeholders in <paramref name="Sql"/>
/// and provide one argument per placeholder in <paramref name="Args"/>.
/// </summary>
/// <param name="Sql">The SQL text with positional <c>?</c> placeholders.</param>
/// <param name="Args">
/// Positional argument values. Supported CLR types: <c>null</c>, <see cref="bool"/>,
/// <see cref="int"/>, <see cref="long"/>, <see cref="double"/>, and <see cref="string"/>.
/// </param>
public sealed record HranaStatement(string Sql, IReadOnlyList<object?> Args);
