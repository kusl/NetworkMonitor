using NetworkMonitor.Core.RemoteSync;

namespace NetworkMonitor.Tests.Fakes;

/// <summary>
/// In-memory fake of <see cref="IRemoteDatabaseClient"/> for testing the remote
/// sync service without any network access. It records every pipeline it is
/// asked to execute and can be toggled to report "not configured" or to fail
/// its calls (simulating a network or protocol error).
/// </summary>
internal sealed class FakeRemoteDatabaseClient : IRemoteDatabaseClient
{
    private readonly List<IReadOnlyList<HranaStatement>> _pipelines = new();

    /// <inheritdoc />
    public bool IsConfigured { get; set; } = true;

    /// <summary>
    /// When false, <see cref="ExecutePipelineAsync"/> returns false without
    /// recording anything, simulating a failed remote call.
    /// </summary>
    public bool SucceedCalls { get; set; } = true;

    /// <summary>Total pipelines this client was asked to execute, including failures.</summary>
    public int CallCount { get; private set; }

    /// <summary>Every pipeline that was executed successfully, in order.</summary>
    public IReadOnlyList<IReadOnlyList<HranaStatement>> ExecutedPipelines => _pipelines;

    public Task<bool> ExecutePipelineAsync(
        IReadOnlyList<HranaStatement> statements,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statements);
        CallCount++;

        if (!SucceedCalls)
        {
            return Task.FromResult(false);
        }

        _pipelines.Add(statements);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Total number of rows inserted across every successful pipeline. Each
    /// INSERT statement carries 11 bound arguments per row, so the row count is
    /// the argument count divided by 11.
    /// </summary>
    public int TotalInsertedRows => _pipelines
        .SelectMany(p => p)
        .Where(s => s.Sql.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))
        .Sum(s => s.Args.Count / 11);
}
