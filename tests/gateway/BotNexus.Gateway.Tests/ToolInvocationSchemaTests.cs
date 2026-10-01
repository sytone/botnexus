using System.Security.Cryptography;
using System.Text;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

public sealed class ToolInvocationSchemaTests : IDisposable
{
    private readonly string _directoryPath = Path.Combine(AppContext.BaseDirectory, nameof(ToolInvocationSchemaTests), Guid.NewGuid().ToString("N"));
    private readonly InMemoryConversationStore _conversations = new();
    private string DatabasePath => Path.Combine(_directoryPath, "sessions.db");
    private string ConnectionString => $"Data Source={DatabasePath};Pooling=False";

    public ToolInvocationSchemaTests() => Directory.CreateDirectory(_directoryPath);

    [Fact]
    public async Task FreshStore_CreatesConstrainedNormalizedSchemaAndIndexes()
    {
        _ = await CreateStore().GetAsync(SessionId.From("probe"));
        await using var connection = await OpenAsync();
        var columns = await ReadStringsAsync(connection, "SELECT name FROM pragma_table_info('tool_invocations') ORDER BY cid");
        columns.ShouldBe(["id", "session_id", "tool_call_id", "tool_name", "arguments_json", "started_at", "completed_at", "status", "is_error", "result_content", "result_bytes", "result_sha256", "retention_state"]);
        var indexes = await ReadStringsAsync(connection, "SELECT name FROM sqlite_master WHERE type = 'index' AND name LIKE 'idx_%tool_invocation%' ORDER BY name");
        indexes.ShouldBe(["idx_session_history_tool_invocation_id", "idx_tool_invocations_retention_completed", "idx_tool_invocations_session_started"]);

        foreach (var sql in new[]
        {
            "INSERT INTO tool_invocations(session_id,tool_call_id,status) VALUES('s','bad-status','running')",
            "INSERT INTO tool_invocations(session_id,tool_call_id,status,retention_state) VALUES('s','bad-retention','unknown','gone')",
            "INSERT INTO tool_invocations(session_id,tool_call_id,status,is_error) VALUES('s','bad-bool','unknown',2)",
            "INSERT INTO tool_invocations(session_id,tool_call_id,status,result_bytes) VALUES('s','bad-bytes','unknown',-1)"
        })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await Should.ThrowAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        }
    }

    [Fact]
    public async Task SaveAsync_StartThenResult_NormalizesWithoutChangingHistoryProjection()
    {
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("paired"), AgentId.From("agent"));
        var startedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var completedAt = startedAt.AddSeconds(1);
        const string arguments = "{\"path\":\"α.txt\"}";
        const string result = "résult ✓";
        session.AddEntry(new SessionEntry { Role = MessageRole.Tool, Content = "started", ToolName = "read", ToolCallId = "call", ToolArgs = arguments, Kind = MessageKind.ToolStart, Timestamp = startedAt });
        await store.SaveAsync(session);
        session.AddEntry(new SessionEntry { Role = MessageRole.Tool, Content = result, ToolName = "read", ToolCallId = "call", ToolArgs = "{\"duplicate\":true}", Kind = MessageKind.ToolResult, Timestamp = completedAt });
        await store.SaveAsync(session);

        var invocation = await ReadInvocationAsync("paired", "call");
        invocation.Status.ShouldBe("success");
        invocation.ArgumentsJson.ShouldBe(arguments);
        invocation.StartedAt.ShouldBe(startedAt.ToString("O"));
        invocation.CompletedAt.ShouldBe(completedAt.ToString("O"));
        invocation.ResultContent.ShouldBe(result);
        invocation.ResultBytes.ShouldBe(Encoding.UTF8.GetByteCount(result));
        invocation.ResultSha256.ShouldBe(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result))).ToLowerInvariant());
        invocation.LinkedRows.ShouldBe(2);

        var reloaded = await CreateStore().GetAsync(SessionId.From("paired"));
        reloaded.ShouldNotBeNull();
        reloaded.GetHistorySnapshot().Select(entry => (entry.Content, entry.ToolArgs, entry.Kind)).ShouldBe([
            ("started", arguments, (MessageKind?)MessageKind.ToolStart),
            (result, "{\"duplicate\":true}", (MessageKind?)MessageKind.ToolResult)]);
    }

    [Fact]
    public async Task AppendEntriesAsync_ErrorAndResultOnly_UseErrorAndUnknownStatuses()
    {
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("append"), AgentId.From("agent"));
        await store.SaveAsync(session);
        await store.AppendEntriesAsync(SessionId.From("append"), [
            new SessionEntry { Role = MessageRole.Tool, Content = "start", ToolName = "exec", ToolCallId = "error", ToolArgs = "{}", Kind = MessageKind.ToolStart },
            new SessionEntry { Role = MessageRole.Tool, Content = "failed", ToolName = "exec", ToolCallId = "error", ToolIsError = true, Kind = MessageKind.ToolResult },
            new SessionEntry { Role = MessageRole.Tool, Content = "orphan", ToolName = "grep", ToolCallId = "result-only", ToolArgs = "{\"pattern\":\"x\"}", Kind = MessageKind.ToolResult }
        ]);
        (await ReadInvocationAsync("append", "error")).Status.ShouldBe("error");
        var resultOnly = await ReadInvocationAsync("append", "result-only");
        resultOnly.Status.ShouldBe("unknown");
        resultOnly.StartedAt.ShouldBeNull();
    }

    [Fact]
    public async Task SaveAsync_DuplicatePersistenceKey_LinksIdempotentlyWithoutRegressingInvocation()
    {
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("duplicate"), AgentId.From("agent"));
        var entry = new SessionEntry { PersistenceKey = "stable", Role = MessageRole.Tool, Content = "done", ToolName = "read", ToolCallId = "call", Kind = MessageKind.ToolResult };
        session.AddEntry(entry);
        await store.SaveAsync(session);
        session.AddEntry(entry with { PersistenceId = null, Content = "different", ToolIsError = true });
        await store.SaveAsync(session);

        var invocation = await ReadInvocationAsync("duplicate", "call");
        invocation.Status.ShouldBe("unknown");
        invocation.ResultContent.ShouldBe("done");
        invocation.LinkedRows.ShouldBe(1);
    }

    [Fact]
    public async Task SaveAsync_ReplacementUpdatesNormalizedResultAndMovesChangedIdentity()
    {
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("replacement"), AgentId.From("agent"));
        session.AddEntry(new SessionEntry { Role = MessageRole.Tool, Content = "start", ToolName = "read", ToolCallId = "old-call", ToolArgs = "{\"old\":true}", Kind = MessageKind.ToolStart });
        session.AddEntry(new SessionEntry { Role = MessageRole.Tool, Content = "old result", ToolName = "read", ToolCallId = "old-call", Kind = MessageKind.ToolResult });
        await store.SaveAsync(session);

        const string updatedResult = "updated ✓";
        session.ReplaceHistory(session.GetHistorySnapshot().Select(entry => entry.Kind == MessageKind.ToolStart
            ? entry with { ToolCallId = "new-call", ToolName = "grep", ToolArgs = "{\"pattern\":\"x\"}" }
            : entry with { ToolCallId = "new-call", ToolName = "grep", Content = updatedResult, ToolIsError = true }).ToArray());
        await store.SaveAsync(session);

        store.LastHistoryWriteReconciled.ShouldBeTrue();
        var invocation = await ReadInvocationAsync("replacement", "new-call");
        invocation.ToolName.ShouldBe("grep");
        invocation.ArgumentsJson.ShouldBe("{\"pattern\":\"x\"}");
        invocation.Status.ShouldBe("error");
        invocation.ResultContent.ShouldBe(updatedResult);
        invocation.ResultBytes.ShouldBe(Encoding.UTF8.GetByteCount(updatedResult));
        invocation.ResultSha256.ShouldBe(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(updatedResult))).ToLowerInvariant());
        invocation.LinkedRows.ShouldBe(2);

        var oldInvocation = await ReadInvocationAsync("replacement", "old-call");
        oldInvocation.ResultContent.ShouldBe("old result");
        oldInvocation.LinkedRows.ShouldBe(0);
    }

    [Fact]
    public async Task SaveAsync_ReplacementTurnsToolRowIntoOrdinaryRowWithoutCreatingInvocation()
    {
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("ordinary"), AgentId.From("agent"));
        session.AddEntry(new SessionEntry { Role = MessageRole.Tool, Content = "result", ToolName = "read", ToolCallId = "call", Kind = MessageKind.ToolResult });
        await store.SaveAsync(session);
        session.ReplaceHistory(session.GetHistorySnapshot().Select(entry => entry with
        {
            Role = MessageRole.Assistant,
            Content = "changed",
            ToolName = null,
            ToolCallId = null,
            Kind = MessageKind.Message
        }).ToArray());
        await store.SaveAsync(session);

        await using var connection = await OpenAsync();
        (await ScalarLongAsync(connection, "SELECT COUNT(*) FROM tool_invocations")).ShouldBe(1);
        (await ScalarLongAsync(connection, "SELECT COUNT(*) FROM session_history WHERE tool_invocation_id IS NOT NULL")).ShouldBe(0);
    }

    [Fact]
    public async Task InvocationLinks_RejectCrossSessionDanglingUpdateAndDelete()
    {
        var store = CreateStore();
        var sessionA = await store.GetOrCreateAsync(SessionId.From("a"), AgentId.From("agent"));
        var sessionB = await store.GetOrCreateAsync(SessionId.From("b"), AgentId.From("agent"));
        await store.SaveAsync(sessionA);
        await store.SaveAsync(sessionB);
        await store.AppendEntriesAsync(SessionId.From("a"), [new SessionEntry { Role = MessageRole.Tool, Content = "start", ToolName = "read", ToolCallId = "call", ToolArgs = "{}", Kind = MessageKind.ToolStart }]);
        await using var connection = await OpenAsync();
        var id = await ScalarLongAsync(connection, "SELECT id FROM tool_invocations WHERE session_id='a'");
        foreach (var sql in new[]
        {
            $"INSERT INTO session_history(session_id,role,content,timestamp,tool_invocation_id) VALUES('b','tool','x','now',{id})",
            $"UPDATE tool_invocations SET session_id='b' WHERE id={id}",
            $"UPDATE tool_invocations SET id={id + 100} WHERE id={id}",
            $"DELETE FROM tool_invocations WHERE id={id}"
        })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await Should.ThrowAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        }
    }

    private SqliteSessionStore CreateStore() => new(ConnectionString, NullLogger<SqliteSessionStore>.Instance, _conversations);
    private async Task<SqliteConnection> OpenAsync() { var connection = new SqliteConnection(ConnectionString); await connection.OpenAsync(); return connection; }
    private async Task<InvocationRow> ReadInvocationAsync(string sessionId, string toolCallId)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.tool_name,i.arguments_json,i.started_at,i.completed_at,i.status,i.result_content,i.result_bytes,i.result_sha256,
                   (SELECT COUNT(*) FROM session_history h WHERE h.tool_invocation_id=i.id)
            FROM tool_invocations i WHERE i.session_id=$sessionId AND i.tool_call_id=$toolCallId
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$toolCallId", toolCallId);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        return new InvocationRow(ReadNullable(reader,0),ReadNullable(reader,1),ReadNullable(reader,2),ReadNullable(reader,3),reader.GetString(4),ReadNullable(reader,5),reader.GetInt64(6),ReadNullable(reader,7),reader.GetInt64(8));
    }
    private static async Task<List<string>> ReadStringsAsync(SqliteConnection connection,string sql) { await using var command=connection.CreateCommand(); command.CommandText=sql; await using var reader=await command.ExecuteReaderAsync(); var values=new List<string>(); while(await reader.ReadAsync()) values.Add(reader.GetString(0)); return values; }
    private static async Task<long> ScalarLongAsync(SqliteConnection connection,string sql) { await using var command=connection.CreateCommand(); command.CommandText=sql; return Convert.ToInt64(await command.ExecuteScalarAsync()); }
    private static string? ReadNullable(SqliteDataReader reader,int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    public void Dispose() { SqlitePoolCleanup.ClearPoolForConnectionString(ConnectionString); if(Directory.Exists(_directoryPath)) Directory.Delete(_directoryPath,true); }
    private sealed record InvocationRow(string? ToolName,string? ArgumentsJson,string? StartedAt,string? CompletedAt,string Status,string? ResultContent,long ResultBytes,string? ResultSha256,long LinkedRows);
}
