using NetworkMonitor.Core.RemoteSync;

namespace NetworkMonitor.Tests.Fakes;

/// <summary>
/// In-memory fake of <see cref="IDatabricksClient"/> for testing the Databricks
/// sync service without any network access. It records every batch of statements
/// it is asked to execute and can be toggled to report "not configured" or to
/// fail its calls (simulating a network, auth, or statement error).
/// </summary>
internal sealed class FakeDatabricksClient : IDatabricksClient
{
    // Each rollup MERGE statement carries 15 named parameters per row.
    private const int ParametersPerRollupRow = 15;

    private readonly List<IReadOnlyList<DatabricksStatement>> _batches = new();

    /// <inheritdoc />
    public bool IsConfigured { get; set; } = true;

    /// <summary>
    /// When false, <see cref="ExecuteAsync"/> returns false without recording
    /// anything, simulating a failed call.
    /// </summary>
    public bool SucceedCalls { get; set; } = true;

    /// <summary>Total batches this client was asked to execute, including failures.</summary>
    public int CallCount { get; private set; }

    /// <summary>Every batch that was executed successfully, in order.</summary>
    public IReadOnlyList<IReadOnlyList<DatabricksStatement>> ExecutedBatches => _batches;

    public Task<bool> ExecuteAsync(
        IReadOnlyList<DatabricksStatement> statements,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statements);
        CallCount++;

        if (!SucceedCalls)
        {
            return Task.FromResult(false);
        }

        _batches.Add(statements);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Total number of rollup rows merged across every successful batch. Each
    /// MERGE statement carries 15 named parameters per row, so the row count is
    /// the parameter count divided by 15.
    /// </summary>
    public int TotalMergedRows => _batches
        .SelectMany(b => b)
        .Where(s => s.Sql.StartsWith("MERGE", StringComparison.OrdinalIgnoreCase))
        .Sum(s => s.Parameters.Count / ParametersPerRollupRow);
}
