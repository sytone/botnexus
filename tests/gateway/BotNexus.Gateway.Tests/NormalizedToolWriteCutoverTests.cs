using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

public sealed class NormalizedToolWriteCutoverTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), nameof(NormalizedToolWriteCutoverTests), Guid.NewGuid().ToString("N"));
    private readonly InMemoryConversationStore _conversations = new();
    private string ConnectionString => $"Data Source={Path.Combine(_directory, "sessions.db")};Pooling=False";
    public NormalizedToolWriteCutoverTests() => Directory.CreateDirectory(_directory);
    private SqliteSessionStore Store() => new(ConnectionString, NullLogger<SqliteSessionStore>.Instance, _conversations);

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task SaveAsync_TypedToolPayloadsAreNarrowAndRoundTrip(bool error, bool incomplete, bool resultFirst)
    {
        var store = Store();
        var session = await store.GetOrCreateAsync(SessionId.From("cutover"), AgentId.From("agent"));
        var start = new SessionEntry { Role = MessageRole.Tool, Content = "start", ToolCallId = "call", ToolName = "read", ToolArgs = "{\"α\":true}", Kind = MessageKind.ToolStart };
        var result = new SessionEntry { Role = MessageRole.Tool, Content = "résult ✓", ToolCallId = "call", ToolName = "read", Kind = MessageKind.ToolResult, ToolIsError = error, ToolIsIncomplete = incomplete };
        session.AddEntry(resultFirst ? result : start);
        await store.SaveAsync(session);
        await AssertNarrowAsync();
        session.AddEntry(resultFirst ? start : result);
        await store.SaveAsync(session);
        await AssertNarrowAsync();
        var loaded = (await Store().GetAsync(session.SessionId)).ShouldNotBeNull();
        loaded.GetHistorySnapshot().Select(e => (e.Content, e.ToolArgs, e.Kind, e.ToolIsError, e.ToolIsIncomplete))
            .ShouldBe(session.GetHistorySnapshot().Select(e => (e.Content, e.ToolArgs, e.Kind, e.ToolIsError, e.ToolIsIncomplete)));
        loaded.ReplaceHistory(loaded.GetHistorySnapshot().Select(e => e.Kind == MessageKind.ToolResult
            ? e with { Content = "edited", ToolIsError = true } : e with { ToolArgs = "{\"edited\":true}" }).ToArray());
        await Store().SaveAsync(loaded);
        await AssertNarrowAsync();
        var edited = (await Store().GetAsync(session.SessionId)).ShouldNotBeNull().GetHistorySnapshot();
        edited.Single(e => e.Kind == MessageKind.ToolStart).ToolArgs.ShouldBe("{\"edited\":true}");
        edited.Single(e => e.Kind == MessageKind.ToolResult).Content.ShouldBe("edited");
        edited.Single(e => e.Kind == MessageKind.ToolResult).ToolIsError.ShouldBeTrue();
    }

    [Fact]
    public async Task AppendAsync_RepeatedResultsPreserveEarlierProjectionAndLegacyRows()
    {
        var store = Store();
        var session = await store.GetOrCreateAsync(SessionId.From("cutover"), AgentId.From("agent"));
        await store.SaveAsync(session);
        await store.AppendEntriesAsync(session.SessionId, [
            new SessionEntry { Role = MessageRole.Tool, Content = "first", ToolCallId = "call", Kind = MessageKind.ToolResult },
            new SessionEntry { Role = MessageRole.Tool, Content = "second", ToolCallId = "call", Kind = MessageKind.ToolResult },
            new SessionEntry { Role = MessageRole.Tool, Content = "legacy", ToolCallId = "legacy", ToolArgs = "{}" }
        ]);
        var loaded = (await Store().GetAsync(session.SessionId)).ShouldNotBeNull();
        loaded.GetHistorySnapshot().Select(e => e.Content).ShouldBe(["first", "second", "legacy"]);
        loaded.ReplaceHistory(loaded.GetHistorySnapshot());
        await Store().SaveAsync(loaded);
        (await Store().GetAsync(session.SessionId)).ShouldNotBeNull().GetHistorySnapshot().Select(e => e.Content).ShouldBe(["first", "second", "legacy"]);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT tool_args FROM session_history WHERE tool_call_id='legacy'";
        (await command.ExecuteScalarAsync()).ShouldBe("{}");
    }

    [Fact]
    public async Task Maintenance_EmptyBatchesDoNotRequestWriterLock()
    {
        var store = Store();
        var session = await store.GetOrCreateAsync(SessionId.From("cutover"), AgentId.From("agent"));
        await store.SaveAsync(session);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var writer = connection.BeginTransaction();
        store.BackfillLegacyToolInvocations(10).ScannedRows.ShouldBe(0);
        store.CleanupLegacyToolPayloads(10).CleanedInvocations.ShouldBe(0);
    }

    [Fact]
    public async Task SaveAsync_TypedInsertsNeverWritePayloadColumnsAndFailureRollsBackMarkers()
    {
        var store = Store();
        var session = await store.GetOrCreateAsync(SessionId.From("cutover"), AgentId.From("agent"));
        await store.SaveAsync(session);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER require_narrow BEFORE INSERT ON session_history
            WHEN (NEW.message_kind='tool-start' AND NEW.tool_args IS NOT NULL)
              OR (NEW.message_kind='tool-result' AND NEW.content IS NOT NULL)
            BEGIN SELECT RAISE(ABORT, 'payload inserted in history'); END;
            CREATE TRIGGER fail_invocation BEFORE INSERT ON tool_invocations
            BEGIN SELECT RAISE(ABORT, 'injected invocation failure'); END;
            """;
        await command.ExecuteNonQueryAsync();
        session.AddEntry(new SessionEntry { Role = MessageRole.Tool, Content = "result", ToolCallId = "call", Kind = MessageKind.ToolResult });
        var failure = await Should.ThrowAsync<SqliteException>(() => store.SaveAsync(session));
        failure.Message.ShouldContain("injected invocation failure");
        command.CommandText = "SELECT COUNT(*) FROM session_history";
        Convert.ToInt64(await command.ExecuteScalarAsync()).ShouldBe(0);
        command.CommandText = "DROP TRIGGER fail_invocation";
        await command.ExecuteNonQueryAsync();
        await store.SaveAsync(session);
        await AssertNarrowAsync();
        (await Store().GetAsync(session.SessionId)).ShouldNotBeNull().GetHistorySnapshot().Single().Content.ShouldBe("result");
    }

    [Fact]
    public async Task Cleanup_InvalidatesOnlyAffectedCachedSession()
    {
        var store = Store();
        var affected = await store.GetOrCreateAsync(SessionId.From("affected"), AgentId.From("agent"));
        var unrelated = await store.GetOrCreateAsync(SessionId.From("unrelated"), AgentId.From("agent"));
        affected.AddEntries([
            new SessionEntry { Role = MessageRole.Tool, Content = "start", ToolCallId = "call", ToolArgs = "{}", Kind = MessageKind.ToolStart },
            new SessionEntry { Role = MessageRole.Tool, Content = "result", ToolCallId = "call", Kind = MessageKind.ToolResult }
        ]);
        await store.SaveAsync(affected);
        await store.SaveAsync(unrelated);
        affected = (await store.GetAsync(affected.SessionId)).ShouldNotBeNull();
        unrelated = (await store.GetAsync(unrelated.SessionId)).ShouldNotBeNull();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE session_history AS h SET
                tool_args=CASE WHEN message_kind='tool-start' THEN i.arguments_json END,
                content=CASE WHEN message_kind='tool-result' THEN i.result_content ELSE h.content END
            FROM tool_invocations i WHERE h.tool_invocation_id=i.id;
            """;
        await command.ExecuteNonQueryAsync();
        store.CleanupLegacyToolPayloads(10).CleanedInvocations.ShouldBe(1);
        ReferenceEquals(await store.GetAsync(unrelated.SessionId), unrelated).ShouldBeTrue();
        ReferenceEquals(await store.GetAsync(affected.SessionId), affected).ShouldBeFalse();
    }

    [Fact]
    public async Task Cleanup_OrphanDiscoveryPrecedesWriterTransactionAndDeletionIsBounded()
    {
        _ = await Store().GetAsync(SessionId.From("probe"));
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var seed = connection.CreateCommand();
        seed.CommandText = "INSERT INTO tool_invocations(session_id,tool_call_id,status) VALUES('missing-a','a','unknown'),('missing-b','b','unknown')";
        await seed.ExecuteNonQueryAsync();
        var statements = new List<string>();
        var handle = connection.Handle ?? throw new InvalidOperationException("Connection must be open.");
        SQLitePCL.raw.sqlite3_trace(handle, new SQLitePCL.strdelegate_trace((_, sql) => statements.Add(sql)), null);
        try
        {
            var delete = typeof(LegacyToolPayloadCleanup).GetMethod("DeleteOrphanedInvocations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).ShouldNotBeNull();
            delete.Invoke(null, [connection, 1]).ShouldBe(1);
        }
        finally { SQLitePCL.raw.sqlite3_trace(handle, (SQLitePCL.strdelegate_trace?)null, null); }
        var discovery = statements.FindIndex(s => s.Contains("SELECT i.id FROM tool_invocations", StringComparison.Ordinal));
        var begin = statements.FindIndex(s => s.Contains("BEGIN", StringComparison.OrdinalIgnoreCase));
        discovery.ShouldBeGreaterThanOrEqualTo(0);
        begin.ShouldBeGreaterThan(discovery);
        var deletion = statements.Single(s => s.Contains("DELETE FROM tool_invocations", StringComparison.Ordinal));
        deletion.ShouldContain("WHERE id=");
        deletion.ShouldContain("NOT EXISTS");
        seed.CommandText = "SELECT COUNT(*) FROM tool_invocations";
        Convert.ToInt64(await seed.ExecuteScalarAsync()).ShouldBe(1);
    }

    [Theory]
    [InlineData("error", false)]
    [InlineData("incomplete", false)]
    [InlineData("non-hot", false)]
    [InlineData("ambiguous", false)]
    [InlineData("error", true)]
    [InlineData("incomplete", true)]
    [InlineData("non-hot", true)]
    [InlineData("ambiguous", true)]
    public async Task SaveAsync_ReplacementPreservesProtectedHistoricalPayloadBytes(string protection, bool unrelatedEdit)
    {
        var store = Store();
        var session = await store.GetOrCreateAsync(SessionId.From("protected"), AgentId.From("agent"));
        session.AddEntries([
            new SessionEntry { Role = MessageRole.Tool, Content = "start", ToolCallId = "call", ToolArgs = "{ \"α\": true }", Kind = MessageKind.ToolStart },
            new SessionEntry { Role = MessageRole.Tool, Content = "résult ✓\r\n", ToolCallId = "call", Kind = MessageKind.ToolResult },
            new SessionEntry { Role = MessageRole.User, Content = "unrelated" }
        ]);
        await store.SaveAsync(session);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var seed = connection.CreateCommand();
        seed.CommandText = """
            UPDATE session_history AS h SET
                tool_args=CASE WHEN message_kind='tool-start' THEN i.arguments_json ELSE h.tool_args END,
                content=CASE WHEN message_kind='tool-result' THEN i.result_content ELSE h.content END
            FROM tool_invocations i WHERE h.tool_invocation_id=i.id;
            """;
        await seed.ExecuteNonQueryAsync();
        seed.CommandText = protection switch
        {
            "error" => "UPDATE tool_invocations SET status='error',is_error=1; UPDATE session_history SET tool_is_error=1 WHERE message_kind='tool-result'",
            "incomplete" => "UPDATE tool_invocations SET status='incomplete',completed_at=NULL; UPDATE session_history SET tool_is_incomplete=1 WHERE message_kind='tool-result'",
            "non-hot" => "UPDATE tool_invocations SET retention_state='expired'",
            _ => "INSERT INTO session_history(session_id,role,content,timestamp,tool_call_id,tool_args,message_kind,tool_invocation_id) SELECT session_id,role,content,timestamp,tool_call_id,tool_args,message_kind,tool_invocation_id FROM session_history WHERE message_kind='tool-start'"
        };
        await seed.ExecuteNonQueryAsync();
        store.CleanupLegacyToolPayloads(10).CleanedInvocations.ShouldBe(0);
        var before = await ReadHistoricalPayloadBytesAsync(connection);
        var loaded = (await Store().GetAsync(session.SessionId)).ShouldNotBeNull();
        var snapshot = loaded.GetHistorySnapshot();
        loaded.ReplaceHistory(unrelatedEdit
            ? snapshot.Select(e => e.Role == MessageRole.User ? e with { Content = "actually edited" } : e).ToArray()
            : snapshot);
        await Store().SaveAsync(loaded);
        (await ReadHistoricalPayloadBytesAsync(connection)).ShouldBe(before);
        var reloaded = (await Store().GetAsync(session.SessionId)).ShouldNotBeNull();
        reloaded.GetHistorySnapshot().Single(e => e.Role == MessageRole.User).Content.ShouldBe(unrelatedEdit ? "actually edited" : "unrelated");
        reloaded.ReplaceHistory(reloaded.GetHistorySnapshot());
        await Store().SaveAsync(reloaded);
        (await ReadHistoricalPayloadBytesAsync(connection)).ShouldBe(before);

        // Historical protection must not prevent an intentional payload edit from taking effect,
        // nor does that edit authorize removal of its payload-bearing history representation.
        reloaded.ReplaceHistory(reloaded.GetHistorySnapshot().Select(e => e.Kind == MessageKind.ToolResult
            ? e with { Content = "legitimate edit ✓" } : e).ToArray());
        await Store().SaveAsync(reloaded);
        seed.CommandText = "SELECT content FROM session_history WHERE message_kind='tool-result'";
        (await seed.ExecuteScalarAsync()).ShouldBe("legitimate edit ✓");
        seed.CommandText = "SELECT result_content FROM tool_invocations";
        (await seed.ExecuteScalarAsync()).ShouldBe("legitimate edit ✓");
        (await Store().GetAsync(session.SessionId)).ShouldNotBeNull().GetHistorySnapshot()
            .Single(e => e.Kind == MessageKind.ToolResult).Content.ShouldBe("legitimate edit ✓");
    }

    [Fact]
    public async Task AppendAsync_NewResultDoesNotNarrowHistoricalPayloadsForSameInvocation()
    {
        var store = Store();
        var session = await store.GetOrCreateAsync(SessionId.From("protected"), AgentId.From("agent"));
        session.AddEntries([
            new SessionEntry { Role = MessageRole.Tool, Content = "start", ToolCallId = "call", ToolArgs = "{}", Kind = MessageKind.ToolStart },
            new SessionEntry { Role = MessageRole.Tool, Content = "old error", ToolCallId = "call", ToolIsError = true, Kind = MessageKind.ToolResult }
        ]);
        await store.SaveAsync(session);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var seed = connection.CreateCommand();
        seed.CommandText = """
            UPDATE session_history AS h SET
                tool_args=CASE WHEN message_kind='tool-start' THEN i.arguments_json END,
                content=CASE WHEN message_kind='tool-result' THEN i.result_content ELSE h.content END
            FROM tool_invocations i WHERE h.tool_invocation_id=i.id;
            """;
        await seed.ExecuteNonQueryAsync();
        var before = await ReadHistoricalPayloadBytesAsync(connection);
        await store.AppendEntriesAsync(session.SessionId, [new SessionEntry { Role = MessageRole.Tool, Content = "new result", ToolCallId = "call", Kind = MessageKind.ToolResult }]);
        // Compare only the original rows; the new result is independently normalized authority.
        (await ReadHistoricalPayloadBytesAsync(connection)).Take(before.Count).ShouldBe(before);
        seed.CommandText = "SELECT content FROM session_history WHERE message_kind='tool-result' ORDER BY id DESC LIMIT 1";
        (await seed.ExecuteScalarAsync()).ShouldBe(DBNull.Value);
        (await Store().GetAsync(session.SessionId)).ShouldNotBeNull().GetHistorySnapshot().Select(e => e.Content).ShouldBe(["start", "old error", "new result"]);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    public async Task MaterializeToolPayloads_KeyedLookupIsBoundedByInvocationNotSession(int unrelatedCount)
    {
        var store = Store();
        var session = await store.GetOrCreateAsync(SessionId.From("materialize-cost"), AgentId.From("agent"));
        await store.SaveAsync(session);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var seed = connection.CreateCommand();
        seed.CommandText = """
            WITH RECURSIVE sequence(value) AS (
                SELECT 1 UNION ALL SELECT value+1 FROM sequence WHERE value<$count)
            INSERT INTO tool_invocations(session_id,tool_call_id,arguments_json,result_content,status)
            SELECT 'materialize-cost','unrelated-' || value,'{}','old result','success' FROM sequence;
            INSERT INTO tool_invocations(session_id,tool_call_id,arguments_json,result_content,status)
            VALUES('materialize-cost','target','target args','target result','success');
            INSERT INTO session_history(session_id,role,timestamp,message_kind,tool_call_id,tool_invocation_id)
            SELECT session_id,'tool','a','tool-start',tool_call_id,id FROM tool_invocations;
            INSERT INTO session_history(session_id,role,timestamp,message_kind,tool_call_id,tool_invocation_id)
            SELECT session_id,'tool','b','tool-result',tool_call_id,id FROM tool_invocations;
            """;
        seed.Parameters.AddWithValue("$count", unrelatedCount);
        await seed.ExecuteNonQueryAsync();
        var materialize = typeof(SqliteSessionStore).GetMethod("MaterializeToolPayloadsAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).ShouldNotBeNull();
        await using var transaction = connection.BeginTransaction();
        var handle = connection.Handle ?? throw new InvalidOperationException("Connection must be open.");
        foreach (var key in new[] { "new-call", "target" })
        {
            long vmSteps = 0;
            var statements = new List<string>();
            SQLitePCL.raw.sqlite3_trace(handle, new SQLitePCL.strdelegate_trace((_, sql) => statements.Add(sql)), null);
            SQLitePCL.raw.sqlite3_progress_handler(handle, 1, _ => { vmSteps++; return 0; }, null);
            HashSet<long> rows;
            try
            {
                var task = materialize.Invoke(null, [connection, transaction, session.SessionId, CancellationToken.None, key])
                    .ShouldBeOfType<Task<HashSet<long>>>();
                rows = await task;
            }
            finally
            {
                SQLitePCL.raw.sqlite3_progress_handler(handle, 0, null, null);
                SQLitePCL.raw.sqlite3_trace(handle, (SQLitePCL.strdelegate_trace?)null, null);
            }
            rows.Count.ShouldBe(key == "new-call" ? 0 : 2);
            statements.Count.ShouldBe(1);
            await using var explain = connection.CreateCommand();
            explain.Transaction = transaction;
            explain.CommandText = "EXPLAIN QUERY PLAN " + statements[0];
            var plan = new List<string>();
            await using (var reader = await explain.ExecuteReaderAsync())
                while (await reader.ReadAsync()) plan.Add(reader.GetString(3));
            Console.WriteLine($"MATERIALIZE unrelated={unrelatedCount} key={key} vmSteps={vmSteps}; {string.Join("; ", plan)}");
            vmSteps.ShouldBeLessThan(2000L, "a keyed write must not visit unrelated session history");
            plan.ShouldContain(detail => detail.Contains("session_id=? AND tool_call_id=?", StringComparison.Ordinal));
            plan.ShouldContain(detail => detail.Contains("idx_session_history_tool_invocation_id (tool_invocation_id=?)", StringComparison.Ordinal));
            plan.ShouldNotContain(detail => detail.Contains("idx_session_history_session_id", StringComparison.Ordinal));
        }
        // Keyed work must not materialize unrelated rows. Null remains whole-session reconciliation.
        seed.Transaction = transaction;
        seed.CommandText = "SELECT COUNT(*) FROM session_history WHERE tool_call_id LIKE 'unrelated-%' AND (tool_args IS NOT NULL OR content IS NOT NULL)";
        Convert.ToInt64(await seed.ExecuteScalarAsync()).ShouldBe(0);
        var reconcile = materialize.Invoke(null, [connection, transaction, session.SessionId, CancellationToken.None, null])
            .ShouldBeOfType<Task<HashSet<long>>>();
        (await reconcile).Count.ShouldBe(2 * unrelatedCount);
        seed.CommandText = "SELECT tool_args FROM session_history WHERE tool_call_id='target' AND message_kind='tool-start'";
        (await seed.ExecuteScalarAsync()).ShouldBe("target args");
        seed.CommandText = "SELECT content FROM session_history WHERE tool_call_id='target' AND message_kind='tool-result'";
        (await seed.ExecuteScalarAsync()).ShouldBe("target result");
        transaction.Rollback();
    }

    private static async Task<List<string>> ReadHistoricalPayloadBytesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, CASE WHEN tool_args IS NULL THEN '<null>' ELSE hex(CAST(tool_args AS BLOB)) END, CASE WHEN content IS NULL THEN '<null>' ELSE hex(CAST(content AS BLOB)) END FROM session_history WHERE message_kind IN ('tool-start','tool-result') ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync()) rows.Add($"{reader.GetInt64(0)}:{reader.GetString(1)}:{reader.GetString(2)}");
        return rows;
    }

    private async Task AssertNarrowAsync()
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM session_history WHERE tool_args IS NOT NULL OR (message_kind='tool-result' AND content IS NOT NULL)";
        Convert.ToInt64(await command.ExecuteScalarAsync()).ShouldBe(0);
    }

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolForConnectionString(ConnectionString);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
