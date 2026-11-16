using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Sync;
using NetworkMonitor.Core.Sync.Databricks;
using NetworkMonitor.Core.Sync.Turso;
using NetworkMonitor.Tests.Fakes;
using Xunit;
using static NetworkMonitor.Tests.Fakes.Rollups;

namespace NetworkMonitor.Tests.Sync;

public sealed class SinkTests
{
    [Fact]
    public async Task Turso_PushChunksInsertsOf50RowsWith15ArgsEach()
    {
        var client = new FakeHranaClient();
        var sink = new TursoRollupSink(client, Options.Create(new RemoteSyncOptions { Url = "https://db", AuthToken = "t" }));

        await sink.PushAsync([.. Enumerable.Range(0, 120).Select(i => Make(HourMs, $"10.0.0.{i}"))], 42, TestContext.Current.CancellationToken);

        var statements = Assert.Single(client.Batches);
        Assert.Equal(3, statements.Count);
        int[] expected = [750, 750, 300];
        Assert.Equal(expected, statements.Select(s => s.Args.Count));
        Assert.StartsWith("INSERT OR REPLACE INTO check_rollups (machine, bucket_start,", statements[0].Sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Turso_SchemaUsesSanitizedTableName()
    {
        var client = new FakeHranaClient();
        var sink = new TursoRollupSink(client, Options.Create(new RemoteSyncOptions { TableName = "my-table;drop" }));

        await sink.EnsureSchemaAsync(TestContext.Current.CancellationToken);

        Assert.Equal("mytabledrop", sink.Destination);
        Assert.Contains("CREATE TABLE IF NOT EXISTS mytabledrop (", Assert.Single(client.Batches)[0].Sql, StringComparison.Ordinal);
        Assert.False(sink.IsEnabled);
    }

    [Fact]
    public void Hrana_EncodesEachSqlValueKind()
    {
        var payload = JsonNode.Parse(HranaClient.BuildPipeline(
            [new HranaStatement("SELECT ?, ?, ?, ?", [Sql.Of(7L), Sql.Of(1.5), Sql.Of("x"), Sql.Of((long?)null)])]))!;

        var args = payload["requests"]![0]!["stmt"]!["args"]!.AsArray();
        Assert.Equal("integer", args[0]!["type"]!.GetValue<string>());
        Assert.Equal("7", args[0]!["value"]!.GetValue<string>());
        Assert.Equal("float", args[1]!["type"]!.GetValue<string>());
        Assert.Equal(1.5, args[1]!["value"]!.GetValue<double>());
        Assert.Equal("text", args[2]!["type"]!.GetValue<string>());
        Assert.Equal("null", args[3]!["type"]!.GetValue<string>());
        Assert.Equal("close", payload["requests"]![1]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Databricks_MergeUsesNamedTypedParameters_AndOmitsNullValues()
    {
        var client = new FakeDatabricksClient();
        var sink = new DatabricksRollupSink(client, Options.Create(new DatabricksSyncOptions { Catalog = "my-cat" }));

        await sink.PushAsync([.. Enumerable.Range(0, 31).Select(i => Make(HourMs, $"h{i}", avgRtt: null))], 42, TestContext.Current.CancellationToken);

        var statements = Assert.Single(client.Batches);
        Assert.Equal(2, statements.Count);
        var merge = statements[0];
        Assert.StartsWith("MERGE INTO my_cat.default.network_monitor_rollups AS t USING (SELECT * FROM (VALUES (:p0, :p1,", merge.Sql, StringComparison.Ordinal);
        Assert.Contains("WHEN NOT MATCHED THEN INSERT *", merge.Sql, StringComparison.Ordinal);
        Assert.Equal(30 * RollupRow.Columns.Count, merge.Parameters.Count);
        Assert.Equal(new DatabricksParameter("p1", "3600000", "BIGINT"), merge.Parameters[1]);
        Assert.Equal(new DatabricksParameter("p7", null, "DOUBLE"), merge.Parameters[7]);
        Assert.Equal(new DatabricksParameter("p12", "8.3", "DOUBLE"), merge.Parameters[12]);
        Assert.Equal(RollupRow.Columns.Count, statements[1].Parameters.Count);
    }

    [Theory]
    [InlineData("libsql://db.turso.io", "https://db.turso.io/v2/pipeline")]
    [InlineData("https://db.turso.io/", "https://db.turso.io/v2/pipeline")]
    [InlineData("ws://localhost:8080", "http://localhost:8080/v2/pipeline")]
    public void Hrana_BuildsPipelineEndpoint(string url, string expected)
    {
        Assert.True(HranaClient.TryBuildPipelineEndpoint(url, out var endpoint));
        Assert.Equal(expected, endpoint!.ToString());
    }

    [Theory]
    [InlineData("dbc-123.cloud.databricks.com", "https://dbc-123.cloud.databricks.com/")]
    [InlineData("https://dbc-123.cloud.databricks.com/some/path?x=1", "https://dbc-123.cloud.databricks.com/")]
    public void Databricks_NormalizesWorkspaceUrl(string url, string expected)
    {
        Assert.True(DatabricksClient.TryBuildWorkspaceBase(url, out var baseUri));
        Assert.Equal(expected, baseUri!.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://host")]
    public void Endpoints_RejectGarbage(string url)
    {
        Assert.False(HranaClient.TryBuildPipelineEndpoint(url, out _));
        Assert.False(DatabricksClient.TryBuildWorkspaceBase(url, out _));
    }

    [Theory]
    [InlineData("good_name", false, "good_name")]
    [InlineData("bad-name", false, "badname")]
    [InlineData("bad-name", true, "bad_name")]
    [InlineData("1starts_with_digit", false, "fallback")]
    [InlineData("  ", false, "fallback")]
    public void SqlIdentifier_Sanitizes(string input, bool dashToUnderscore, string expected) =>
        Assert.Equal(expected, SqlIdentifier.Sanitize(input, "fallback", dashToUnderscore));
}
