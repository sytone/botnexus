using Shouldly;
using BotNexus.Domain.Primitives;
using BotNexus.Extensions.DataStore;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using System.Text.Json;

namespace BotNexus.Extensions.DataStore.Tests;

public sealed class DataStoreToolTests
{
    // ── Fixture helpers ───────────────────────────────────────────────────────

    private static DataStoreTool CreateTool(FakeDataStoreBackend? backend = null) =>
        new(backend ?? new FakeDataStoreBackend());

    private static IReadOnlyDictionary<string, object?> Args(string action, params (string key, string value)[] extras)
    {
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["action"] = action };
        foreach (var (k, v) in extras) d[k] = v;
        return d;
    }

    private static string TextOf(AgentToolResult r) => r.Content[0].Value;
    private static bool IsError(AgentToolResult r) => r.Content[0].Value.StartsWith("Error:", StringComparison.OrdinalIgnoreCase);

    // ── ValidActions ──────────────────────────────────────────────────────────

    [Fact]
    public void ValidActions_ContainsAllExpectedActions()
    {
        var expected = new[] { "ingest", "query", "insert", "update", "delete", "count", "schema", "tables", "drop" };
        foreach (var a in expected)
            DataStoreTool.ValidActions.Contains(a).ShouldBeTrue($"'{a}' missing from ValidActions");
    }

    // ── Unknown action ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("upsert")]
    [InlineData("merge")]
    [InlineData("")]
    [InlineData(null)]
    public async Task ExecuteAsync_UnknownAction_ReturnsError(string? action)
    {
        var tool = CreateTool();
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["action"] = action };
        var result = await tool.ExecuteAsync("tc1", args);
        IsError(result).ShouldBeTrue();
    }

    // ── Table name validation ─────────────────────────────────────────────────

    [Theory]
    [InlineData("my_table", true)]
    [InlineData("table1", true)]
    [InlineData("MyTable", false)]           // uppercase
    [InlineData("my-table", false)]          // hyphen
    [InlineData("", false)]                  // empty
    [InlineData("SELECT", false)]            // uppercase keyword
    public void IsValidTableName_ValidatesCorrectly(string name, bool expected) =>
        DataStoreTool.IsValidTableName(name).ShouldBe(expected);

    // ── ingest ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Ingest_MissingTable_ReturnsError()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("ingest", ("data", "[{\"x\":1}]")));
        IsError(result).ShouldBeTrue();
        TextOf(result).ShouldContain("table");
    }

    [Fact]
    public async Task ExecuteAsync_Ingest_MissingData_ReturnsError()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("ingest", ("table", "my_table")));
        IsError(result).ShouldBeTrue();
        TextOf(result).ShouldContain("data");
    }

    [Fact]
    public async Task ExecuteAsync_Ingest_InvalidTableName_ReturnsError()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("ingest", ("table", "Bad-Table"), ("data", "[{}]")));
        IsError(result).ShouldBeTrue();
        TextOf(result).ShouldContain("Invalid table name");
    }

    [Fact]
    public async Task ExecuteAsync_Ingest_HappyPath_DelegatesToBackend()
    {
        var backend = new FakeDataStoreBackend();
        var tool    = CreateTool(backend);
        var result  = await tool.ExecuteAsync("tc1", Args("ingest", ("table", "events"), ("data", "[{\"id\":1}]")));
        IsError(result).ShouldBeFalse();
        backend.LastAction.ShouldBe("ingest");
        backend.LastTable.ShouldBe("events");
    }

    // ── query ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Query_MissingSql_ReturnsError()
    {
        var tool   = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("query"));
        IsError(result).ShouldBeTrue();
        TextOf(result).ShouldContain("sql");
    }

    [Fact]
    public async Task ExecuteAsync_Query_NonSelectStatement_ReturnsError()
    {
        var tool   = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("query", ("sql", "DROP TABLE events")));
        IsError(result).ShouldBeTrue();
        TextOf(result).ShouldContain("SELECT");
    }

    [Fact]
    public async Task ExecuteAsync_Query_HappyPath_DelegatesToBackend()
    {
        var backend = new FakeDataStoreBackend();
        var tool    = CreateTool(backend);
        var result  = await tool.ExecuteAsync("tc1", Args("query", ("sql", "SELECT * FROM events")));
        IsError(result).ShouldBeFalse();
        backend.LastAction.ShouldBe("query");
    }

    [Fact]
    public async Task ExecuteAsync_Query_MultiStatementSelectThenDelete_ReturnsErrorAndDoesNotDelegate()
    {
        var backend = new FakeDataStoreBackend();
        var tool    = CreateTool(backend);
        var result  = await tool.ExecuteAsync("tc1", Args("query", ("sql", "SELECT 1; DELETE FROM events;")));
        IsError(result).ShouldBeTrue();
        TextOf(result).ShouldContain("single");
        backend.LastAction.ShouldBeNull(); // never reached the backend
    }

    [Fact]
    public async Task ExecuteAsync_Query_MultiStatementSelectThenDrop_ReturnsError()
    {
        var backend = new FakeDataStoreBackend();
        var tool    = CreateTool(backend);
        var result  = await tool.ExecuteAsync("tc1", Args("query", ("sql", "SELECT * FROM events; DROP TABLE events")));
        IsError(result).ShouldBeTrue();
        backend.LastAction.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_Query_TrailingSemicolon_IsAccepted()
    {
        var backend = new FakeDataStoreBackend();
        var tool    = CreateTool(backend);
        var result  = await tool.ExecuteAsync("tc1", Args("query", ("sql", "SELECT * FROM events;  ")));
        IsError(result).ShouldBeFalse();
        backend.LastAction.ShouldBe("query");
    }

    [Fact]
    public async Task ExecuteAsync_Query_SemicolonInsideStringLiteral_IsAccepted()
    {
        // A semicolon inside a quoted literal is not a statement separator.
        var backend = new FakeDataStoreBackend();
        var tool    = CreateTool(backend);
        var result  = await tool.ExecuteAsync("tc1", Args("query", ("sql", "SELECT * FROM events WHERE note = 'a; b'")));
        IsError(result).ShouldBeFalse();
        backend.LastAction.ShouldBe("query");
    }

    [Theory]
    [InlineData("SELECT * FROM t")]
    [InlineData("SELECT * FROM t;")]
    [InlineData("  select 1  ")]
    [InlineData("SELECT ';' AS sep FROM t")]
    public void IsSingleSelectStatement_AcceptsValidSelects(string sql)
        => DataStoreTool.IsSingleSelectStatement(sql).ShouldBeTrue(sql);

    [Theory]
    [InlineData("SELECT 1; DELETE FROM t")]
    [InlineData("SELECT 1; DROP TABLE t")]
    [InlineData("SELECT 1;SELECT 2")]
    [InlineData("DROP TABLE t")]               // not a SELECT at all
    [InlineData("DELETE FROM t")]              // not a SELECT at all
    [InlineData("")]
    [InlineData("   ")]
    public void IsSingleSelectStatement_RejectsInvalidOrMultiStatement(string sql)
        => DataStoreTool.IsSingleSelectStatement(sql).ShouldBeFalse(sql);

    // ── insert ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Insert_MissingTable_ReturnsError()
    {
        var tool   = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("insert", ("data", "{\"x\":1}")));
        IsError(result).ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_Insert_MissingData_ReturnsError()
    {
        var tool   = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("insert", ("table", "events")));
        IsError(result).ShouldBeTrue();
        TextOf(result).ShouldContain("data");
    }

    [Fact]
    public async Task ExecuteAsync_Insert_HappyPath_DelegatesToBackend()
    {
        var backend = new FakeDataStoreBackend();
        var tool    = CreateTool(backend);
        var result  = await tool.ExecuteAsync("tc1", Args("insert", ("table", "events"), ("data", "{\"id\":42}")));
        IsError(result).ShouldBeFalse();
        backend.LastAction.ShouldBe("insert");
    }

    // ── delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Delete_MissingWhere_ReturnsError()
    {
        var tool   = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("delete", ("table", "events")));
        IsError(result).ShouldBeTrue();
        TextOf(result).ShouldContain("where");
    }

    [Fact]
    public async Task ExecuteAsync_Delete_HappyPath_DelegatesToBackend()
    {
        var backend = new FakeDataStoreBackend();
        var tool    = CreateTool(backend);
        var result  = await tool.ExecuteAsync("tc1", Args("delete", ("table", "events"), ("where", "id = 1")));
        IsError(result).ShouldBeFalse();
        backend.LastAction.ShouldBe("delete");
    }

    // ── update ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Update_MissingTable_ReturnsError()
    {
        var tool   = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("update", ("set", "{\"x\":1}"), ("where", "id = 1")));
        IsError(result).ShouldBeTrue();
        TextOf(result).ShouldContain("table");
    }

    [Fact]
    public async Task ExecuteAsync_Update_MissingSet_ReturnsError()
    {
        var tool   = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("update", ("table", "events"), ("where", "id = 1")));
        IsError(result).ShouldBeTrue();
        TextOf(result).ShouldContain("set");
    }

    [Fact]
    public async Task ExecuteAsync_Update_MissingWhere_ReturnsError()
    {
        var tool   = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("update", ("table", "events"), ("set", "{\"x\":1}")));
        IsError(result).ShouldBeTrue();
        TextOf(result).ShouldContain("where");
    }

    [Fact]
    public async Task ExecuteAsync_Update_InvalidTableName_ReturnsError()
    {
        var tool   = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("update", ("table", "Bad-Table"), ("set", "{\"x\":1}"), ("where", "id = 1")));
        IsError(result).ShouldBeTrue();
        TextOf(result).ShouldContain("Invalid table name");
    }

    [Fact]
    public async Task ExecuteAsync_Update_HappyPath_DelegatesToBackend()
    {
        var backend = new FakeDataStoreBackend();
        var tool    = CreateTool(backend);
        var result  = await tool.ExecuteAsync("tc1", Args("update", ("table", "events"), ("set", "{\"status\":\"done\"}"), ("where", "id = 1")));
        IsError(result).ShouldBeFalse();
        backend.LastAction.ShouldBe("update");
        backend.LastTable.ShouldBe("events");
    }

    // ── count ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Count_MissingTable_ReturnsError()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("count"));
        IsError(result).ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_Count_InvalidTableName_ReturnsError()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("count", ("table", "My-Table")));
        IsError(result).ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_Count_FullTable_DelegatesToBackend()
    {
        var backend = new FakeDataStoreBackend();
        var tool = CreateTool(backend);
        var result = await tool.ExecuteAsync("tc1", Args("count", ("table", "events")));
        IsError(result).ShouldBeFalse();
        backend.LastAction.ShouldBe("count");
        backend.LastTable.ShouldBe("events");
        TextOf(result).ShouldContain("42");
    }

    [Fact]
    public async Task ExecuteAsync_Count_WithWhere_DelegatesToBackend()
    {
        var backend = new FakeDataStoreBackend();
        var tool = CreateTool(backend);
        var result = await tool.ExecuteAsync("tc1", Args("count", ("table", "events"), ("where", "status = 'done'")));
        IsError(result).ShouldBeFalse();
        backend.LastAction.ShouldBe("count");
    }

    // ── schema ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Schema_MissingTable_ReturnsError()
    {
        var tool   = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("schema"));
        IsError(result).ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_Schema_HappyPath_DelegatesToBackend()
    {
        var backend = new FakeDataStoreBackend();
        var tool    = CreateTool(backend);
        var result  = await tool.ExecuteAsync("tc1", Args("schema", ("table", "events")));
        IsError(result).ShouldBeFalse();
        backend.LastAction.ShouldBe("schema");
    }

    // ── tables ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Tables_DelegatesToBackend()
    {
        var backend = new FakeDataStoreBackend();
        var tool    = CreateTool(backend);
        var result  = await tool.ExecuteAsync("tc1", Args("tables"));
        IsError(result).ShouldBeFalse();
        backend.LastAction.ShouldBe("tables");
    }

    // ── drop ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Drop_MissingTable_ReturnsError()
    {
        var tool   = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args("drop"));
        IsError(result).ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_Drop_HappyPath_DelegatesToBackend()
    {
        var backend = new FakeDataStoreBackend();
        var tool    = CreateTool(backend);
        var result  = await tool.ExecuteAsync("tc1", Args("drop", ("table", "events")));
        IsError(result).ShouldBeFalse();
        backend.LastAction.ShouldBe("drop");
    }
}

// ── Contributor tests ─────────────────────────────────────────────────────────

public sealed class DataStoreToolContributorTests
{
    [Fact]
    public async Task ContributeAsync_WhenDisabled_ReturnsNoTools()
    {
        var contributor = new DataStoreToolContributor();
        var context     = BuildContext(enabled: false);
        var result      = await contributor.ContributeAsync(context);
        result.Tools.Count.ShouldBe(0);
    }

    [Fact]
    public async Task ContributeAsync_WhenEnabled_ReturnsDataStoreTool()
    {
        var contributor = new DataStoreToolContributor();
        var context     = BuildContext(enabled: true);
        var result      = await contributor.ContributeAsync(context);
        result.Tools.Count.ShouldBe(1);
        result.Tools[0].Name.ShouldBe("data_store");
    }

    [Fact]
    public async Task ContributeAsync_WhenEnabled_ReturnsBackendAsDisposableResource()
    {
        var contributor = new DataStoreToolContributor();
        var context     = BuildContext(enabled: true);
        var result      = await contributor.ContributeAsync(context);
        result.ResourcesToDispose.ShouldNotBeNull();
        result.ResourcesToDispose!.Count.ShouldBe(1);
        result.ResourcesToDispose[0].ShouldBeOfType<SqliteDataStoreBackend>();
    }

    [Fact]
    public async Task ContributeAsync_MissingExtensionConfig_DefaultsToDisabled()
    {
        var contributor = new DataStoreToolContributor();
        var context     = BuildContext(hasConfig: false);
        var result      = await contributor.ContributeAsync(context);
        result.Tools.Count.ShouldBe(0);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AgentToolContributionContext BuildContext(bool enabled = false, bool hasConfig = true)
    {
        var configJson = enabled
            ? """{"enabled":true,"maxSizeBytes":52428800}"""
            : """{"enabled":false}""";

        var extensionConfig = new Dictionary<string, JsonElement>();
        if (hasConfig)
            extensionConfig["botnexus-data-store"] = JsonDocument.Parse(configJson).RootElement;

        var descriptor = new AgentDescriptor
        {
            AgentId      = AgentId.From("test-agent"),
            DisplayName  = "Test Agent",
            ModelId      = "test-model",
            ApiProvider  = "test-provider",
            ExtensionConfig = extensionConfig
        };

        return new AgentToolContributionContext(
            descriptor,
            new AgentExecutionContext { SessionId = SessionId.Create() },
            Path.Combine(Path.GetTempPath(), "test-workspace"),
            new AllowAllPathValidator(),
            _ => null,
            (_, _) => Task.FromResult<string?>(null));
    }

    private sealed class AllowAllPathValidator : BotNexus.Gateway.Abstractions.Security.IPathValidator
    {
        public bool CanRead(string absolutePath) => true;
        public bool CanWrite(string absolutePath) => true;
        public string? ValidateAndResolve(string rawPath, BotNexus.Gateway.Abstractions.Security.FileAccessMode mode) => rawPath;
    }
}

// ── Fakes ──────────────────────────────────────────────────────────────────────

internal sealed class FakeDataStoreBackend : IDataStoreBackend
{
    public string? LastAction { get; private set; }
    public string? LastTable  { get; private set; }

    public Task<DataStoreResult> IngestAsync(string table, string json, CancellationToken ct = default)
    { LastAction = "ingest"; LastTable = table; return Task.FromResult(DataStoreResult.Ok("1 row ingested", 1)); }

    public Task<DataStoreResult> QueryAsync(string sql, CancellationToken ct = default)
    { LastAction = "query"; return Task.FromResult(DataStoreResult.Ok("[]", 0)); }

    public Task<DataStoreResult> InsertAsync(string table, string json, CancellationToken ct = default)
    { LastAction = "insert"; LastTable = table; return Task.FromResult(DataStoreResult.Ok("inserted", 1)); }

    public Task<DataStoreResult> DeleteAsync(string table, string where, CancellationToken ct = default)
    { LastAction = "delete"; LastTable = table; return Task.FromResult(DataStoreResult.Ok("1 deleted", 1)); }

    public Task<DataStoreResult> UpdateAsync(string table, string set, string where, CancellationToken ct = default)
    { LastAction = "update"; LastTable = table; return Task.FromResult(DataStoreResult.Ok("1 row updated.", 1)); }

    public Task<DataStoreResult> SchemaAsync(string table, CancellationToken ct = default)
    { LastAction = "schema"; LastTable = table; return Task.FromResult(DataStoreResult.Ok("id INTEGER", 0)); }

    public Task<DataStoreResult> TablesAsync(CancellationToken ct = default)
    { LastAction = "tables"; return Task.FromResult(DataStoreResult.Ok("events", 0)); }

    public Task<DataStoreResult> CountAsync(string table, string? where = null, CancellationToken ct = default)
    { LastAction = "count"; LastTable = table; return Task.FromResult(DataStoreResult.Ok("{\"table\":\"" + table + "\",\"count\":42}", 42)); }
    public Task<DataStoreResult> DropAsync(string table, CancellationToken ct = default)
    { LastAction = "drop"; LastTable = table; return Task.FromResult(DataStoreResult.Ok("dropped", 0)); }

    public void Dispose() { }
}
