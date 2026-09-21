namespace NetworkMonitor.Core.RemoteSync;

/// <summary>
/// A single named parameter for a Databricks SQL statement. Named parameters use
/// <c>:name</c> markers in the SQL (no leading colon in <see cref="Name"/>).
/// </summary>
/// <param name="Name">The parameter name, without the leading colon.</param>
/// <param name="Value">
/// The value in its string form (all Databricks parameter values are sent as
/// strings). <c>null</c> binds a typed SQL NULL (the <c>value</c> field is
/// omitted from the request).
/// </param>
/// <param name="Type">
/// The Databricks SQL type, e.g. <c>STRING</c>, <c>BIGINT</c>, <c>DOUBLE</c>.
/// Supplying a type lets NULLs carry a definite type, which is required inside a
/// multi-row VALUES clause.
/// </param>
public sealed record DatabricksParameter(string Name, string? Value, string Type);

/// <summary>
/// A single SQL statement plus its named parameters, executed against a
/// Databricks SQL warehouse through the Statement Execution REST API.
/// </summary>
/// <param name="Sql">The SQL text with <c>:name</c> parameter markers.</param>
/// <param name="Parameters">One entry per marker referenced in <paramref name="Sql"/>.</param>
public sealed record DatabricksStatement(string Sql, IReadOnlyList<DatabricksParameter> Parameters);
