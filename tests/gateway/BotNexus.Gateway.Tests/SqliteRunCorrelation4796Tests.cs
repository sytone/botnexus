using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using static BotNexus.Gateway.Tests.RunCorrelation4796Fixture;

namespace BotNexus.Gateway.Tests;

public sealed class SqliteRunCorrelation4796Tests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), nameof(SqliteRunCorrelation4796Tests), Guid.NewGuid().ToString("N"));
    private readonly InMemoryConversationStore _conversations = new();
    private string ConnectionString => $"Data Source={Path.Combine(_directory, "sessions.db")};Pooling=False";
    public SqliteRunCorrelation4796Tests() => Directory.CreateDirectory(_directory);
    private SqliteSessionStore Store() => new(ConnectionString, NullLogger<SqliteSessionStore>.Instance, _conversations);

    [Fact]
    public async Task NormalizedInvocations_CorrelationSurvivesIncrementalSaveReloadAndPayloadCleanup()
    {
        var store = Store();
        var session = await store.GetOrCreateAsync(SessionId.From("correlated"), AgentId.From("agent"));
        var start = Row("call", MessageKind.ToolStart, "start");
        SetId(start, "agent-run-a");
        session.AddEntry(start);
        await store.SaveAsync(session);
        var result = Row("call", MessageKind.ToolResult, "result");
        SetId(result, "agent-run-a");
        session.AddEntry(result);
        await store.SaveAsync(session);
        await AssertColumnAsync();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT agent_run_id FROM tool_invocations WHERE session_id='correlated' AND provider_tool_call_id='call'";
        (await command.ExecuteScalarAsync()).ShouldBe("agent-run-a");
        var reloaded = (await Store().GetAsync(session.SessionId)).ShouldNotBeNull();
        reloaded.GetHistorySnapshot().Count.ShouldBe(2);
        reloaded.GetHistorySnapshot().ShouldAllBe(row => Id(row) == "agent-run-a");
        store.CleanupLegacyToolPayloads(10).CleanedInvocations.ShouldBe(1);
        reloaded = (await Store().GetAsync(session.SessionId)).ShouldNotBeNull();
        reloaded.GetHistorySnapshot().ShouldAllBe(row => Id(row) == "agent-run-a");
        reloaded.GetHistorySnapshot().Last().Content.ShouldBe("result");
        reloaded.ReplaceHistory(reloaded.GetHistorySnapshot());
        await Store().SaveAsync(reloaded);
        (await command.ExecuteScalarAsync()).ShouldBe("agent-run-a");
    }

    [Fact]
    public async Task Migration_PreCorrelationDatabase_AddsNullableColumnWithoutInventingLegacyIdentity()
    {
        var store = Store();
        var session = await store.GetOrCreateAsync(SessionId.From("legacy"), AgentId.From("agent"));
        session.AddEntry(Row("legacy-call", MessageKind.ToolResult, "old result"));
        await store.SaveAsync(session);
        // Turn an initialized database into the immediately preceding schema. On baseline it
        // already has that schema; after implementation, remove only the additive correlation.
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('tool_invocations') WHERE name='agent_run_id'";
            if (Convert.ToInt64(await command.ExecuteScalarAsync()) != 0)
            {
                command.CommandText = "DROP INDEX IF EXISTS idx_tool_invocations_session_run; ALTER TABLE tool_invocations DROP COLUMN agent_run_id;";
                await command.ExecuteNonQueryAsync();
            }
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('session_history') WHERE name='agent_run_id'";
            if (Convert.ToInt64(await command.ExecuteScalarAsync()) != 0)
            {
                command.CommandText = "ALTER TABLE session_history DROP COLUMN agent_run_id";
                await command.ExecuteNonQueryAsync();
            }
        }
        var legacy = (await Store().GetAsync(session.SessionId)).ShouldNotBeNull();
        legacy.GetHistorySnapshot().ShouldHaveSingleItem().Content.ShouldBe("old result");
        await AssertColumnAsync();
        var property = typeof(SessionEntry).GetProperty("AgentRunId").ShouldNotBeNull();
        property.GetValue(legacy.GetHistorySnapshot()[0]).ShouldBeNull();
        await using var verify = new SqliteConnection(ConnectionString);
        await verify.OpenAsync();
        await using var query = verify.CreateCommand();
        query.CommandText = "SELECT agent_run_id FROM tool_invocations WHERE tool_call_id='legacy-call'";
        (await query.ExecuteScalarAsync()).ShouldBe(DBNull.Value);
        var correlated = Row("new-call", MessageKind.ToolResult, "new result");
        SetId(correlated, "new-run");
        legacy.AddEntry(correlated);
        await Store().SaveAsync(legacy);
        var roundTrip = (await Store().GetAsync(session.SessionId)).ShouldNotBeNull().GetHistorySnapshot();
        property.GetValue(roundTrip[0]).ShouldBeNull();
        Id(roundTrip[1]).ShouldBe("new-run");
    }

    [Fact]
    public async Task Measurement_RunScopedCounts_UseNormalizedEvidenceAndBoundedIndexedQuery()
    {
        var store = Store();
        var session = await store.GetOrCreateAsync(SessionId.From("measurement"), AgentId.From("agent"));
        for (var run = 0; run < 3; run++)
        {
            for (var call = 0; call < 4; call++)
            {
                var start = Row($"call-{run}-{call}", MessageKind.ToolStart, "start");
                var result = Row($"call-{run}-{call}", MessageKind.ToolResult, "private-payload-4796");
                SetId(start, $"run-{run}");
                SetId(result, $"run-{run}");
                session.AddEntry(start);
                session.AddEntry(result);
            }
        }
        session.AddEntry(Row("legacy", MessageKind.ToolResult, "old"));
        await store.SaveAsync(session);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        const string sql = "SELECT id, status, is_error, result_bytes FROM tool_invocations WHERE session_id=$session AND agent_run_id=$run ORDER BY id LIMIT $limit";
        command.Parameters.AddWithValue("$session", "measurement");
        command.Parameters.AddWithValue("$run", "run-1");
        command.Parameters.AddWithValue("$limit", 2);
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        await using (var plan = await command.ExecuteReaderAsync())
        {
            var details = new List<string>();
            while (await plan.ReadAsync()) details.Add(plan.GetString(3));
            details.ShouldContain(detail => detail.Contains("idx_tool_invocations_session_run", StringComparison.Ordinal));
        }
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = 0;
        while (await reader.ReadAsync())
        {
            rows++;
            reader.GetString(1).ShouldBe("success");
            reader.GetInt32(2).ShouldBe(0);
            reader.GetInt32(3).ShouldBe(System.Text.Encoding.UTF8.GetByteCount("private-payload-4796"));
        }
        rows.ShouldBe(2, "measurement must be bounded and must not count both transcript start/result rows");
    }

    [Fact]
    public async Task NormalizedInvocations_ReusedProviderIdAcrossRuns_RemainDistinctAfterReplacementAndCleanup()
    {
        var store = Store();
        var session = await store.GetOrCreateAsync(SessionId.From("reused"), AgentId.From("agent"));
        for (var run = 0; run < 2; run++)
        {
            var start = Row("same-provider-id", MessageKind.ToolStart, "start");
            var result = Row("same-provider-id", MessageKind.ToolResult, $"result-{run}");
            SetId(start, $"run-{run}");
            SetId(result, $"run-{run}");
            session.AddEntries([start, result]);
            await store.SaveAsync(session);
        }
        var reopened = (await Store().GetAsync(session.SessionId)).ShouldNotBeNull();
        reopened.GetHistorySnapshot().Count.ShouldBe(4);
        reopened.GetHistorySnapshot().ShouldAllBe(row => row.ToolCallId == "same-provider-id");
        reopened.ReplaceHistory(reopened.GetHistorySnapshot());
        await Store().SaveAsync(reopened);
        store.CleanupLegacyToolPayloads(10).CleanedInvocations.ShouldBe(2);
        var rows = (await Store().GetAsync(session.SessionId)).ShouldNotBeNull().GetHistorySnapshot();
        rows.Where(row => row.Kind == MessageKind.ToolResult).Select(row => row.Content).ShouldBe(["result-0", "result-1"]);
        rows.Select(Id).ShouldBe(["run-0", "run-0", "run-1", "run-1"]);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(DISTINCT tool_call_id) FROM tool_invocations WHERE session_id='reused' AND provider_tool_call_id='same-provider-id'";
        Convert.ToInt64(await command.ExecuteScalarAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task NormalizedInvocations_SyntheticInterruption_IsUnknownNotProvenResult()
    {
        var store = Store();
        var session = await store.GetOrCreateAsync(SessionId.From("interrupted"), AgentId.From("agent"));
        var sink = BotNexus.Gateway.Audit.DefaultToolAuditSink.Instance;
        var start = sink.ProjectStart("call", "probe", "{}");
        var interrupted = sink.ProjectIncomplete("call", "probe", "{}");
        SetId(start, "interrupted-run");
        SetId(interrupted, "interrupted-run");
        session.AddEntries([start, interrupted]);
        await store.SaveAsync(session);
        var loaded = (await Store().GetAsync(session.SessionId)).ShouldNotBeNull();
        loaded.GetHistorySnapshot().Last().ToolIsIncomplete.ShouldBeTrue();
        loaded.ReplaceHistory(loaded.GetHistorySnapshot());
        await Store().SaveAsync(loaded);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, completed_at, agent_run_id FROM tool_invocations WHERE session_id='interrupted'";
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        reader.GetString(0).ShouldBe("unknown");
        reader.IsDBNull(1).ShouldBeTrue("a synthetic transcript row is not an actual execution result");
        reader.GetString(2).ShouldBe("interrupted-run");
    }

    private async Task AssertColumnAsync()
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, [notnull] FROM pragma_table_info('tool_invocations') WHERE name='agent_run_id'";
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue("#4796 requires normalized nullable agent_run_id, not a JSON-only tag");
        reader.GetString(0).ShouldBe("TEXT");
        reader.GetInt32(1).ShouldBe(0);
    }

    private static SessionEntry Row(string call, MessageKind kind, string content) => new()
    {
        Role = MessageRole.Tool, Content = content, ToolCallId = call, ToolName = "probe", ToolArgs = "{}", Kind = kind,
        Timestamp = DateTimeOffset.Parse("2026-01-01T00:00:00Z")
    };

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
