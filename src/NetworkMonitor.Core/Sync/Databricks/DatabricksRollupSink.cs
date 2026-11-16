using System.Globalization;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Sync.Databricks;

public sealed class DatabricksRollupSink(IDatabricksClient client, IOptions<DatabricksSyncOptions> options) : IRollupSink
{
    public const string CheckpointKeyName = "databricks_rollup_next_bucket_ms";
    private const int RowsPerStatement = 30;

    private static readonly string[] ColumnTypes =
    [
        "STRING", "BIGINT", "STRING", "STRING", "STRING",
        "BIGINT", "BIGINT", "DOUBLE", "BIGINT", "BIGINT", "DOUBLE",
        "DOUBLE", "DOUBLE", "BIGINT", "BIGINT",
    ];

    private readonly DatabricksSyncOptions _options = options.Value;
    private readonly string _table = string.Join(
        '.',
        SqlIdentifier.Sanitize(options.Value.Catalog, "workspace", dashToUnderscore: true),
        SqlIdentifier.Sanitize(options.Value.Schema, "default", dashToUnderscore: true),
        SqlIdentifier.Sanitize(options.Value.TableName, "network_monitor_rollups", dashToUnderscore: true));
    private readonly string _machine = Environment.MachineName;

    public string Name => "Databricks sync";

    public string Destination => _table;

    public string CheckpointKey => CheckpointKeyName;

    public RollupSyncOptions Options => _options;

    public bool IsEnabled => _options.IsConfigured && client.IsConfigured;

    public Task<bool> EnsureSchemaAsync(CancellationToken cancellationToken) =>
        client.ExecuteAsync(
        [
            new(
                $"CREATE TABLE IF NOT EXISTS {_table} (" +
                "machine STRING NOT NULL, " +
                "bucket_start BIGINT NOT NULL, " +
                "target_name STRING NOT NULL, " +
                "target_address STRING NOT NULL, " +
                "target_category STRING NOT NULL, " +
                "samples BIGINT NOT NULL, " +
                "ok BIGINT NOT NULL, " +
                "avg_rtt_ms DOUBLE, " +
                "min_rtt_ms BIGINT, " +
                "max_rtt_ms BIGINT, " +
                "avg_jitter_ms DOUBLE, " +
                "avg_dns_ms DOUBLE, " +
                "avg_loss_pct DOUBLE NOT NULL, " +
                "bucket_minutes BIGINT NOT NULL, " +
                "synced_at BIGINT NOT NULL" +
                ") USING DELTA",
                []),
        ],
        cancellationToken);

    public Task<bool> PushAsync(IReadOnlyList<CheckRollup> rows, long syncedAtMs, CancellationToken cancellationToken) =>
        client.ExecuteAsync([.. rows.Chunk(RowsPerStatement).Select(chunk => BuildMerge(chunk, syncedAtMs))], cancellationToken);

    internal DatabricksStatement BuildMerge(IReadOnlyList<CheckRollup> rows, long syncedAtMs)
    {
        List<DatabricksParameter> parameters = [with(capacity: rows.Count * ColumnTypes.Length)];
        List<string> tuples = [with(capacity: rows.Count)];

        foreach (var row in rows)
        {
            var values = RollupRow.Values(row, _machine, syncedAtMs);
            List<string> markers = [with(capacity: values.Length)];
            for (var c = 0; c < values.Length; c++)
            {
                var name = string.Create(CultureInfo.InvariantCulture, $"p{parameters.Count}");
                markers.Add(":" + name);
                parameters.Add(new DatabricksParameter(name, Format(values[c]), ColumnTypes[c]));
            }

            tuples.Add("(" + string.Join(", ", markers) + ")");
        }

        var sql =
            $"MERGE INTO {_table} AS t " +
            $"USING (SELECT * FROM (VALUES {string.Join(", ", tuples)}) AS raw({string.Join(", ", RollupRow.Columns)})) AS s " +
            "ON t.machine = s.machine AND t.target_address = s.target_address " +
            "AND t.bucket_start = s.bucket_start " +
            "WHEN MATCHED THEN UPDATE SET * " +
            "WHEN NOT MATCHED THEN INSERT *";

        return new DatabricksStatement(sql, parameters);
    }

    private static string? Format(SqlValue value) => value switch
    {
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString(CultureInfo.InvariantCulture),
        string s => s,
        null => null,
    };
}
