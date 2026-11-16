using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Sync.Turso;

public sealed class TursoRollupSink(IHranaClient client, IOptions<RemoteSyncOptions> options) : IRollupSink
{
    public const string CheckpointKeyName = "remote_rollup_next_bucket_ms";
    private const int RowsPerStatement = 50;

    private readonly RemoteSyncOptions _options = options.Value;
    private readonly string _table = SqlIdentifier.Sanitize(options.Value.TableName, "check_rollups");
    private readonly string _machine = Environment.MachineName;

    public string Name => "Remote sync";

    public string Destination => _table;

    public string CheckpointKey => CheckpointKeyName;

    public RollupSyncOptions Options => _options;

    public bool IsEnabled => _options.IsConfigured && client.IsConfigured;

    public Task<bool> EnsureSchemaAsync(CancellationToken cancellationToken) =>
        client.ExecuteAsync(
        [
            new(
                $"CREATE TABLE IF NOT EXISTS {_table} (" +
                "machine TEXT NOT NULL, " +
                "bucket_start INTEGER NOT NULL, " +
                "target_name TEXT NOT NULL, " +
                "target_address TEXT NOT NULL, " +
                "target_category TEXT NOT NULL, " +
                "samples INTEGER NOT NULL, " +
                "ok INTEGER NOT NULL, " +
                "avg_rtt_ms REAL, " +
                "min_rtt_ms INTEGER, " +
                "max_rtt_ms INTEGER, " +
                "avg_jitter_ms REAL, " +
                "avg_dns_ms REAL, " +
                "avg_loss_pct REAL NOT NULL, " +
                "bucket_minutes INTEGER NOT NULL, " +
                "synced_at INTEGER NOT NULL, " +
                "PRIMARY KEY (machine, target_address, bucket_start))",
                []),
            new($"CREATE INDEX IF NOT EXISTS idx_{_table}_bucket ON {_table}(bucket_start)", []),
        ],
        cancellationToken);

    public Task<bool> PushAsync(IReadOnlyList<CheckRollup> rows, long syncedAtMs, CancellationToken cancellationToken) =>
        client.ExecuteAsync([.. rows.Chunk(RowsPerStatement).Select(chunk => BuildInsert(chunk, syncedAtMs))], cancellationToken);

    internal HranaStatement BuildInsert(IReadOnlyList<CheckRollup> rows, long syncedAtMs)
    {
        var placeholders = "(" + string.Join(", ", Enumerable.Repeat("?", RollupRow.Columns.Count)) + ")";
        var sql =
            $"INSERT OR REPLACE INTO {_table} ({string.Join(", ", RollupRow.Columns)}) " +
            $"VALUES {string.Join(", ", Enumerable.Repeat(placeholders, rows.Count))}";
        return new HranaStatement(sql, [.. rows.SelectMany(r => RollupRow.Values(r, _machine, syncedAtMs))]);
    }
}
