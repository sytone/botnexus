using System.Security.Cryptography;
using System.Text;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace BotNexus.Gateway.Tests;

public sealed class ToolInvocationSchemaTests : IDisposable
{
    private readonly string _directoryPath = Path.Combine(AppContext.BaseDirectory, nameof(ToolInvocationSchemaTests), Guid.NewGuid().ToString("N"));
    private readonly InMemoryConversationStore _conversations = new();
    private string DatabasePath => Path.Combine(_directoryPath, "sessions.db");
    private string ConnectionString => $"Data Source={DatabasePath};Pooling=False";

    private readonly ITestOutputHelper _output;

    public ToolInvocationSchemaTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_directoryPath);
    }

    [Fact]
    public async Task FreshStore_CreatesConstrainedNormalizedSchemaAndIndexes()
    {
        _ = await CreateStore().GetAsync(SessionId.From("probe"));
        await using var connection = await OpenAsync();
        var columns = await ReadStringsAsync(connection, "SELECT name FROM pragma_table_info('tool_invocations') ORDER BY cid");
        columns.ShouldBe(["id", "session_id", "tool_call_id", "tool_name", "arguments_json", "started_at", "completed_at", "status", "is_error", "result_content", "result_bytes", "result_sha256", "retention_state", "agent_run_id", "provider_tool_call_id"]);
        var indexes = await ReadStringsAsync(connection, "SELECT name FROM sqlite_master WHERE type = 'index' AND (name LIKE 'idx_%tool_invocation%' OR name LIKE 'idx_session_history_unlinked_tool_%') ORDER BY name");
        indexes.ShouldBe(["idx_session_history_tool_invocation_id", "idx_session_history_unlinked_tool_rows", "idx_session_history_unlinked_tool_sibling", "idx_tool_invocations_retention_completed", "idx_tool_invocations_session_run", "idx_tool_invocations_session_started"]);

        var plan = await ReadStringsAsync(connection, """
            EXPLAIN QUERY PLAN
            SELECT id
            FROM session_history
            WHERE tool_invocation_id IS NULL
              AND tool_call_id IS NOT NULL
              AND (message_kind IN ('tool-start', 'tool-result')
                   OR (message_kind IS NULL AND role = 'tool'))
            ORDER BY id
            LIMIT 100
            """, ordinal: 3);
        plan.ShouldContain(detail => detail.Contains("USING INDEX idx_session_history_unlinked_tool_rows", StringComparison.Ordinal));

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
    public async Task ExistingStore_AddsSeekableSiblingIndexIdempotently()
    {
        _ = await CreateStore().GetAsync(SessionId.From("probe"));
        await using var connection = await OpenAsync();
        await using var drop = connection.CreateCommand();
        drop.CommandText = "DROP INDEX idx_session_history_unlinked_tool_sibling";
        await drop.ExecuteNonQueryAsync();

        _ = await CreateStore().GetAsync(SessionId.From("probe"));
        _ = await CreateStore().GetAsync(SessionId.From("probe"));

        // Reopen after another connection changes the schema; inspect the migrated schema,
        // not this connection's cached schema/plan from before the index was restored.
        await using var migrated = await OpenAsync();
        (await ReadStringsAsync(migrated, "SELECT name FROM sqlite_master WHERE name='idx_session_history_unlinked_tool_sibling'")).ShouldBe(["idx_session_history_unlinked_tool_sibling"]);
        var plan = await ReadStringsAsync(migrated, """
            EXPLAIN QUERY PLAN SELECT 1 FROM session_history
            WHERE session_id='s' AND tool_call_id='call' AND agent_run_id IS NULL
              AND tool_invocation_id IS NULL
              AND (message_kind IN ('tool-start','tool-result') OR (message_kind IS NULL AND role='tool'))
            """, ordinal: 3);
        plan.ShouldContain(detail => detail.Contains("idx_session_history_unlinked_tool_sibling (session_id=? AND tool_call_id=? AND agent_run_id=?)", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, null, "tool-start", true)]
    [InlineData(null, null, null, true)]
    [InlineData("run", "run", "tool-result", true)]
    [InlineData("run", "other-run", "tool-result", false)]
    [InlineData("run", null, "tool-result", false)]
    [InlineData(null, "run", "tool-result", false)]
    [InlineData("run", "run", "text", false)]
    public async Task CleanupLegacyToolPayloads_SiblingProbePreservesProviderCallAndRunProtection(string? run, string? siblingRun, string? kind, bool protects)
    {
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("sibling"), AgentId.From("agent"));
        await store.SaveAsync(session);
        await using var connection = await OpenAsync();
        await using var seed = connection.CreateCommand();
        seed.CommandText = """
            INSERT INTO tool_invocations(session_id,tool_call_id,provider_tool_call_id,agent_run_id,arguments_json,started_at,completed_at,status,result_content,result_bytes,result_sha256)
            VALUES('sibling','normalized-call','provider-call',$run,'{}','a','b','success','r',1,'454349e422f05297191ead13e21d3db520e5abef52055e4964b82fb213f593a1');
            INSERT INTO session_history(session_id,role,content,timestamp,tool_call_id,tool_args,message_kind,tool_invocation_id)
            SELECT 'sibling','tool','start','a','provider-call','{}','tool-start',id FROM tool_invocations;
            INSERT INTO session_history(session_id,role,content,timestamp,tool_call_id,message_kind,tool_invocation_id)
            SELECT 'sibling','tool','r','b','provider-call','tool-result',id FROM tool_invocations;
            INSERT INTO session_history(session_id,role,content,timestamp,tool_call_id,message_kind,agent_run_id)
            VALUES('sibling','tool','unlinked','b','provider-call',$kind,$siblingRun);
            """;
        seed.Parameters.AddWithValue("$run", (object?)run ?? DBNull.Value);
        seed.Parameters.AddWithValue("$siblingRun", (object?)siblingRun ?? DBNull.Value);
        seed.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        await seed.ExecuteNonQueryAsync();

        var report = store.CleanupLegacyToolPayloads(1);

        report.ScannedInvocations.ShouldBe(1);
        report.LastScannedInvocationId.ShouldBe(await ScalarLongAsync(connection, "SELECT id FROM tool_invocations"));
        report.HasMore.ShouldBeFalse();
        report.CleanedInvocations.ShouldBe(protects ? 0 : 1);
        report.CleanedRows.ShouldBe(protects ? 0 : 2);
        report.ClearedBytes.ShouldBe(protects ? 0 : 3);
        (await ScalarLongAsync(connection, "SELECT COUNT(*) FROM session_history WHERE tool_invocation_id IS NOT NULL AND (tool_args IS NOT NULL OR (message_kind='tool-result' AND content IS NOT NULL))")).ShouldBe(protects ? 2 : 0);
        (await ReadStringsAsync(connection, "SELECT content FROM session_history WHERE tool_invocation_id IS NULL")).ShouldBe(["unlinked"]);
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
    public async Task CleanupLegacyToolPayloads_SafePairClearsDuplicatesAndReloadHydratesAuthoritativeEvidence()
    {
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("cleanup"), AgentId.From("agent"));
        var startedAt = DateTimeOffset.Parse("2026-02-01T00:00:00Z");
        var completedAt = startedAt.AddSeconds(2);
        const string arguments = "{\"path\":\"safe.txt\"}";
        const string result = "safe result";
        session.AddEntry(new SessionEntry { Role = MessageRole.Tool, Content = "started", ToolName = "read", ToolCallId = "safe", ToolArgs = arguments, Kind = MessageKind.ToolStart, Timestamp = startedAt });
        session.AddEntry(new SessionEntry { Role = MessageRole.Tool, Content = result, ToolName = "read", ToolCallId = "safe", ToolArgs = "duplicate", Kind = MessageKind.ToolResult, Timestamp = completedAt });
        await store.SaveAsync(session);

        // Seed the pre-cutover duplicated layout explicitly: live writes are narrow now.
        await RestoreLegacyPayloadsAsync("cleanup");
        var first = store.CleanupLegacyToolPayloads(10);
        var second = store.CleanupLegacyToolPayloads(10);

        first.CleanedInvocations.ShouldBe(1);
        first.CleanedRows.ShouldBe(2);
        first.ClearedBytes.ShouldBe(Encoding.UTF8.GetByteCount(arguments) + Encoding.UTF8.GetByteCount(result) + Encoding.UTF8.GetByteCount("duplicate"));
        first.ProtectedInvocations.ShouldBe(-1);
        first.RemainingInvocations.ShouldBe(-1);
        second.CleanedInvocations.ShouldBe(0);
        second.ClearedBytes.ShouldBe(0);
        await using (var connection = await OpenAsync())
        {
            (await ScalarLongAsync(connection, "SELECT COUNT(*) FROM session_history WHERE session_id='cleanup' AND tool_invocation_id IS NOT NULL")).ShouldBe(2);
            (await ScalarLongAsync(connection, "SELECT COUNT(*) FROM session_history WHERE session_id='cleanup' AND (tool_args IS NOT NULL OR (message_kind='tool-result' AND content IS NOT NULL))")).ShouldBe(0);
        }

        var reloadedStore = CreateStore();
        var reloaded = await reloadedStore.GetAsync(SessionId.From("cleanup"));
        reloaded.ShouldNotBeNull();
        reloaded.GetHistorySnapshot().Select(entry => (entry.Content, entry.ToolArgs, entry.Kind, entry.Timestamp)).ShouldBe([
            ("started", arguments, (MessageKind?)MessageKind.ToolStart, startedAt),
            (result, (string?)null, (MessageKind?)MessageKind.ToolResult, completedAt)]);

        reloaded.ReplaceHistory(reloaded.GetHistorySnapshot());
        await reloadedStore.SaveAsync(reloaded);
        (await ReadInvocationAsync("cleanup", "safe")).ResultContent.ShouldBe(result);
    }

    [Fact]
    public Task CleanupLegacyToolPayloads_MidBatchAbortRollsBackAndFreshStoreRetryConverges() =>
        AssertCleanupRestartAsync(abortSecondInvocation: true);

    [Fact]
    public Task CleanupLegacyToolPayloads_CommittedBatchRemainsUnchangedAcrossFreshStores() =>
        AssertCleanupRestartAsync(abortSecondInvocation: false);

    private async Task AssertCleanupRestartAsync(bool abortSecondInvocation)
    {
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("restart"), AgentId.From("agent"));
        var startedAt = DateTimeOffset.Parse("2026-02-01T00:00:00Z");
        const string arguments = "{\"path\":\"α.txt\"}";
        const string result = "résult ✓";
        const string duplicate = "duplicate β";
        foreach (var call in new[] { "first", "second" })
        {
            session.AddEntry(new SessionEntry { Role = MessageRole.Tool, Content = "started", ToolName = "read", ToolCallId = call, ToolArgs = arguments, Kind = MessageKind.ToolStart, Timestamp = startedAt });
            session.AddEntry(new SessionEntry { Role = MessageRole.Tool, Content = result, ToolName = "read", ToolCallId = call, ToolArgs = duplicate, Kind = MessageKind.ToolResult, Timestamp = startedAt.AddSeconds(2) });
        }
        await store.SaveAsync(session);
        await RestoreLegacyPayloadsAsync("restart");
        await using var connection = await OpenAsync();
        var originalHistory = await ReadTableBytesAsync(connection, "session_history");
        var originalInvocations = await ReadTableBytesAsync(connection, "tool_invocations");
        originalHistory.Count.ShouldBe(4);
        originalInvocations.Count.ShouldBe(2);
        var expectedHistory = await ReadTableBytesAsync(connection, "session_history", projectCleanup: true);
        expectedHistory.ShouldNotBe(originalHistory);
        var lastId = await ScalarLongAsync(connection, "SELECT MAX(id) FROM tool_invocations");

        if (abortSecondInvocation)
        {
            await using (var trigger = connection.CreateCommand())
            {
                // The predicate also proves an earlier invocation's UPDATE completed before
                // the injected statement abort. SQLite ABORT alone does not roll that back.
                trigger.CommandText = """
                    CREATE TRIGGER abort_second_cleanup BEFORE UPDATE ON session_history
                    WHEN OLD.tool_call_id='second'
                      AND EXISTS(SELECT 1 FROM session_history WHERE tool_call_id='first' AND message_kind='tool-result' AND content IS NULL AND tool_args IS NULL)
                    BEGIN SELECT RAISE(ABORT, 'injected second invocation cleanup failure'); END;
                    """;
                await trigger.ExecuteNonQueryAsync();
            }
            var failure = Should.Throw<SqliteException>(() => store.CleanupLegacyToolPayloads(2));
            failure.SqliteErrorCode.ShouldBe(19);
            failure.Message.ShouldContain("injected second invocation cleanup failure");
            (await ReadTableBytesAsync(connection, "session_history")).ShouldBe(originalHistory);
            (await ReadTableBytesAsync(connection, "tool_invocations")).ShouldBe(originalInvocations);
            await using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TRIGGER abort_second_cleanup";
            await drop.ExecuteNonQueryAsync();
        }

        // A new instance has no cached session or progress from the failed/committed batch.
        var restartedStore = CreateStore();
        var cleaned = restartedStore.CleanupLegacyToolPayloads(2);
        cleaned.ScannedInvocations.ShouldBe(2);
        cleaned.CleanedInvocations.ShouldBe(2);
        cleaned.CleanedRows.ShouldBe(4);
        cleaned.ClearedBytes.ShouldBe(2L * (Encoding.UTF8.GetByteCount(arguments) + Encoding.UTF8.GetByteCount(result) + Encoding.UTF8.GetByteCount(duplicate)));
        cleaned.OrphanedInvocationsDeleted.ShouldBe(0);
        cleaned.LastScannedInvocationId.ShouldBe(lastId);
        cleaned.HasMore.ShouldBeFalse();
        (await ReadTableBytesAsync(connection, "session_history")).ShouldBe(expectedHistory);
        (await ReadTableBytesAsync(connection, "tool_invocations")).ShouldBe(originalInvocations);

        // Fail even a same-value UPDATE on a repeated cleanup, not just visible changes.
        await using (var guard = connection.CreateCommand())
        {
            guard.CommandText = """
                CREATE TRIGGER reject_history_rewrite BEFORE UPDATE ON session_history
                BEGIN SELECT RAISE(ABORT, 'unexpected history rewrite'); END;
                CREATE TRIGGER reject_evidence_rewrite BEFORE UPDATE ON tool_invocations
                BEGIN SELECT RAISE(ABORT, 'unexpected evidence rewrite'); END;
                """;
            await guard.ExecuteNonQueryAsync();
        }
        for (var restart = 0; restart < 2; restart++)
        {
            var freshStore = CreateStore();
            var reloaded = await freshStore.GetAsync(SessionId.From("restart"));
            reloaded.ShouldNotBeNull();
            reloaded.GetHistorySnapshot().Select(entry => (entry.ToolCallId, entry.Content, entry.ToolArgs, entry.Kind, entry.Timestamp)).ShouldBe([
                ("first", "started", arguments, (MessageKind?)MessageKind.ToolStart, startedAt),
                ("first", result, (string?)null, (MessageKind?)MessageKind.ToolResult, startedAt.AddSeconds(2)),
                ("second", "started", arguments, (MessageKind?)MessageKind.ToolStart, startedAt),
                ("second", result, (string?)null, (MessageKind?)MessageKind.ToolResult, startedAt.AddSeconds(2))]);
            // Restart without a persisted cursor must also be a no-op.
            var repeated = freshStore.CleanupLegacyToolPayloads(2);
            repeated.ScannedInvocations.ShouldBe(0);
            repeated.CleanedInvocations.ShouldBe(0);
            repeated.CleanedRows.ShouldBe(0);
            repeated.ClearedBytes.ShouldBe(0);
            repeated.OrphanedInvocationsDeleted.ShouldBe(0);
            repeated.LastScannedInvocationId.ShouldBe(0);
            repeated.HasMore.ShouldBeFalse();
            (await ReadTableBytesAsync(connection, "session_history")).ShouldBe(expectedHistory);
            (await ReadTableBytesAsync(connection, "tool_invocations")).ShouldBe(originalInvocations);
        }
    }

    private static async Task<List<string>> ReadTableBytesAsync(SqliteConnection connection, string table, bool projectCleanup = false, string predicate = "1=1")
    {
        // Snapshot every column, preserving NULL/type and exact SQLite payload bytes.
        // ORDER BY id protects row/link/order evidence, not only the duplicate columns.
        var columns = await ReadStringsAsync(connection, $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid");
        var values = columns.Select(column =>
        {
            var expression = projectCleanup && column == "tool_args" ? "NULL"
                : projectCleanup && column == "content" ? "CASE WHEN message_kind='tool-result' THEN NULL ELSE content END"
                : $"\"{column}\"";
            return $"typeof({expression}),hex(CAST({expression} AS BLOB))";
        });
        return await ReadStringsAsync(connection, $"SELECT json_array({string.Join(',', values)}) FROM {table} WHERE {predicate} ORDER BY id");
    }

    [Theory]
    [InlineData("tool-start", false, 1)]
    [InlineData("tool-start", false, 10)]
    [InlineData(null, false, 1)]
    [InlineData(null, false, 10)]
    [InlineData("tool-start", true, 1)]
    [InlineData("tool-start", true, 10)]
    public async Task CleanupLegacyToolPayloads_MissingResultEvidencePreservesHistoryAndAdvancesPastCandidate(string? messageKind, bool addLegacySibling, int batchSize)
    {
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("missing-result"), AgentId.From("agent"));
        await store.SaveAsync(session);
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tool_invocations(session_id,tool_call_id,tool_name,arguments_json,started_at,completed_at,status,is_error,result_content,result_bytes,result_sha256,retention_state)
            VALUES
              ('missing-result','protected','x','{}','a','b','success',0,'r',1,'454349e422f05297191ead13e21d3db520e5abef52055e4964b82fb213f593a1','hot'),
              ('missing-result','safe','x','{}','a','b','success',0,'r',1,'454349e422f05297191ead13e21d3db520e5abef52055e4964b82fb213f593a1','hot');
            INSERT INTO session_history(session_id,role,content,timestamp,tool_name,tool_call_id,tool_args,tool_is_error,message_kind,tool_invocation_id)
            SELECT 'missing-result','tool','start','a','x','protected','{}',0,$kind,id FROM tool_invocations WHERE tool_call_id='protected';
            INSERT INTO session_history(session_id,role,content,timestamp,tool_name,tool_call_id,tool_args,tool_is_error,message_kind,tool_invocation_id)
            SELECT 'missing-result','tool','legacy result','b','x','protected','legacy args',0,NULL,id FROM tool_invocations WHERE tool_call_id='protected' AND $addSibling=1;
            INSERT INTO session_history(session_id,role,content,timestamp,tool_name,tool_call_id,tool_args,tool_is_error,message_kind,tool_invocation_id)
            SELECT 'missing-result','tool','start','a','x','safe','{}',0,'tool-start',id FROM tool_invocations WHERE tool_call_id='safe';
            INSERT INTO session_history(session_id,role,content,timestamp,tool_name,tool_call_id,tool_args,tool_is_error,message_kind,tool_invocation_id)
            SELECT 'missing-result','tool','r','b','x','safe',NULL,0,'tool-result',id FROM tool_invocations WHERE tool_call_id='safe';
            """;
        command.Parameters.AddWithValue("$kind", (object?)messageKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$addSibling", addLegacySibling ? 1 : 0);
        await command.ExecuteNonQueryAsync();
        var protectedId = await ScalarLongAsync(connection, "SELECT id FROM tool_invocations WHERE tool_call_id='protected'");
        var safeId = await ScalarLongAsync(connection, "SELECT id FROM tool_invocations WHERE tool_call_id='safe'");
        var protectedInvocation = await ReadInvocationAsync("missing-result", "protected");
        const string protectedHistorySql = "SELECT json_array(id,session_id,role,content,timestamp,tool_name,tool_call_id,tool_args,tool_is_error,message_kind,tool_invocation_id) FROM session_history WHERE tool_call_id='protected' ORDER BY id";
        var protectedHistory = await ReadStringsAsync(connection, protectedHistorySql);

        var first = store.CleanupLegacyToolPayloads(batchSize);
        first.ScannedInvocations.ShouldBe(batchSize == 1 ? 1 : 2);
        first.CleanedInvocations.ShouldBe(batchSize == 1 ? 0 : 1);
        first.CleanedRows.ShouldBe(batchSize == 1 ? 0 : 2);
        first.ClearedBytes.ShouldBe(batchSize == 1 ? 0 : 3);
        first.LastScannedInvocationId.ShouldBe(batchSize == 1 ? protectedId : safeId);
        first.HasMore.ShouldBe(batchSize == 1);
        if (batchSize == 1)
        {
            var next = store.CleanupLegacyToolPayloads(batchSize, first.LastScannedInvocationId);
            next.ScannedInvocations.ShouldBe(1);
            next.CleanedInvocations.ShouldBe(1);
            next.CleanedRows.ShouldBe(2);
            next.ClearedBytes.ShouldBe(3);
            next.LastScannedInvocationId.ShouldBe(safeId);
            next.HasMore.ShouldBeFalse();
        }

        var finished = store.CleanupLegacyToolPayloads(batchSize, safeId);
        finished.ScannedInvocations.ShouldBe(0);
        finished.CleanedInvocations.ShouldBe(0);
        finished.LastScannedInvocationId.ShouldBe(safeId);
        finished.HasMore.ShouldBeFalse();
        (await ReadStringsAsync(connection, protectedHistorySql)).ShouldBe(protectedHistory);
        (await ReadInvocationAsync("missing-result", "protected")).ShouldBe(protectedInvocation);
        (await ScalarLongAsync(connection, "SELECT COUNT(*) FROM session_history WHERE tool_call_id='safe' AND (tool_args IS NOT NULL OR (message_kind='tool-result' AND content IS NOT NULL))")).ShouldBe(0);
    }

    // These are the actual constrained schema values, not invented timeout/cancelled or
    // external/access-denied retention policies. Each case changes only one safe pair.
    [Theory]
    [InlineData("status-error")]
    [InlineData("status-incomplete")]
    [InlineData("status-unknown")]
    [InlineData("retention-warm")]
    [InlineData("retention-cold")]
    [InlineData("retention-expired")]
    [InlineData("invocation-is-error")]
    [InlineData("result-is-error")]
    [InlineData("missing-arguments")]
    [InlineData("incorrect-arguments")]
    [InlineData("missing-start-arguments")]
    [InlineData("incorrect-start-arguments")]
    [InlineData("missing-result")]
    [InlineData("incorrect-result")]
    [InlineData("missing-hash")]
    [InlineData("incorrect-hash")]
    [InlineData("incorrect-bytes")]
    [InlineData("incorrect-start-time")]
    [InlineData("incorrect-result-time")]
    [InlineData("incorrect-start-call")]
    [InlineData("incorrect-result-call")]
    [InlineData("duplicate-start")]
    [InlineData("duplicate-result")]
    [InlineData("missing-start-row")]
    [InlineData("missing-result-row")]
    [InlineData("unlinked-result")]
    public async Task CleanupLegacyToolPayloads_ProtectedDispositionPreservesEveryEvidenceByte(string disposition)
    {
        const string arguments = "{\"path\":\"α.txt\"}";
        const string result = "résult ✓";
        const string duplicate = "duplicate β";
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("disposition"), AgentId.From("agent"));
        var startedAt = DateTimeOffset.Parse("2026-02-01T00:00:00Z");
        foreach (var call in new[] { "protected", "safe" })
        {
            session.AddEntry(new SessionEntry { Role = MessageRole.Tool, Content = "started", ToolName = "read", ToolCallId = call, ToolArgs = arguments, Kind = MessageKind.ToolStart, Timestamp = startedAt });
            session.AddEntry(new SessionEntry { Role = MessageRole.Tool, Content = result, ToolName = "read", ToolCallId = call, ToolArgs = duplicate, Kind = MessageKind.ToolResult, Timestamp = startedAt.AddSeconds(2) });
        }
        await store.SaveAsync(session);
        // This fixture exercises historical duplicate cleanup, not the normalized-only live
        // writer. Restore the pre-cutover payload layout before applying each protection.
        await RestoreLegacyPayloadsAsync("disposition");
        await using var connection = await OpenAsync();
        await using (var change = connection.CreateCommand())
        {
            var mutation = disposition switch
            {
                "status-error" => "UPDATE tool_invocations SET status='error'",
                "status-incomplete" => "UPDATE tool_invocations SET status='incomplete'",
                "status-unknown" => "UPDATE tool_invocations SET status='unknown'",
                "retention-warm" => "UPDATE tool_invocations SET retention_state='warm'",
                "retention-cold" => "UPDATE tool_invocations SET retention_state='cold'",
                "retention-expired" => "UPDATE tool_invocations SET retention_state='expired'",
                "invocation-is-error" => "UPDATE tool_invocations SET is_error=1",
                "result-is-error" => "UPDATE session_history SET tool_is_error=1 WHERE message_kind='tool-result' AND",
                "missing-arguments" => "UPDATE tool_invocations SET arguments_json=NULL",
                "incorrect-arguments" => "UPDATE tool_invocations SET arguments_json='different'",
                "missing-start-arguments" => "UPDATE session_history SET tool_args=NULL WHERE message_kind='tool-start' AND",
                "incorrect-start-arguments" => "UPDATE session_history SET tool_args='different' WHERE message_kind='tool-start' AND",
                "missing-result" => "UPDATE tool_invocations SET result_content=NULL",
                "incorrect-result" => "UPDATE session_history SET content='different' WHERE message_kind='tool-result' AND",
                "missing-hash" => "UPDATE tool_invocations SET result_sha256=NULL",
                "incorrect-hash" => "UPDATE tool_invocations SET result_sha256='different'",
                "incorrect-bytes" => "UPDATE tool_invocations SET result_bytes=result_bytes+1",
                "incorrect-start-time" => "UPDATE session_history SET timestamp='2026-02-02T00:00:00+00:00' WHERE message_kind='tool-start' AND",
                "incorrect-result-time" => "UPDATE session_history SET timestamp='2026-02-02T00:00:00+00:00' WHERE message_kind='tool-result' AND",
                "incorrect-start-call" => "UPDATE session_history SET tool_call_id='different' WHERE message_kind='tool-start' AND",
                "incorrect-result-call" => "UPDATE session_history SET tool_call_id='different' WHERE message_kind='tool-result' AND",
                "missing-start-row" => "DELETE FROM session_history WHERE message_kind='tool-start' AND",
                "missing-result-row" => "DELETE FROM session_history WHERE message_kind='tool-result' AND",
                "unlinked-result" => "UPDATE session_history SET tool_invocation_id=NULL WHERE message_kind='tool-result' AND",
                "duplicate-start" or "duplicate-result" => """
                    INSERT INTO session_history(session_id,role,content,timestamp,tool_name,tool_call_id,tool_args,tool_is_error,message_kind,tool_invocation_id)
                    SELECT session_id,role,content,timestamp,tool_name,tool_call_id,tool_args,tool_is_error,message_kind,tool_invocation_id
                    FROM session_history WHERE message_kind=$kind AND
                    """,
                _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unknown protected disposition")
            };
            // Restrict by invocation link, so even the incorrect-call cases retain their row identity.
            var predicate = mutation.Contains("tool_invocations SET", StringComparison.Ordinal)
                ? "tool_call_id='protected'"
                : "tool_invocation_id=(SELECT id FROM tool_invocations WHERE tool_call_id='protected')";
            change.CommandText = mutation + (mutation.EndsWith("AND", StringComparison.Ordinal) ? " " : " WHERE ") + predicate;
            if (disposition is "duplicate-start" or "duplicate-result")
                change.Parameters.AddWithValue("$kind", disposition == "duplicate-start" ? "tool-start" : "tool-result");
            (await change.ExecuteNonQueryAsync()).ShouldBe(1);
        }

        const string protectedHistory = "tool_invocation_id=(SELECT id FROM tool_invocations WHERE tool_call_id='protected') OR tool_call_id='protected' OR tool_call_id='different'";
        var invocationBefore = await ReadTableBytesAsync(connection, "tool_invocations");
        var historyBefore = await ReadTableBytesAsync(connection, "session_history", predicate: protectedHistory);
        historyBefore.Count.ShouldBe(disposition is "duplicate-start" or "duplicate-result" ? 3 : disposition is "missing-start-row" or "missing-result-row" ? 1 : 2);
        var safeExpected = await ReadTableBytesAsync(connection, "session_history", projectCleanup: true, predicate: "tool_call_id='safe'");
        safeExpected.ShouldNotBe(await ReadTableBytesAsync(connection, "session_history", predicate: "tool_call_id='safe'"));

        var report = store.CleanupLegacyToolPayloads(10);

        // Check evidence before telemetry: a removed guard must fail on actual evidence loss.
        (await ReadTableBytesAsync(connection, "tool_invocations")).ShouldBe(invocationBefore, disposition);
        (await ReadTableBytesAsync(connection, "session_history", predicate: protectedHistory)).ShouldBe(historyBefore, disposition);
        (await ReadTableBytesAsync(connection, "session_history", predicate: "tool_call_id='safe'")).ShouldBe(safeExpected);
        report.CleanedInvocations.ShouldBe(1);
        report.CleanedRows.ShouldBe(2);
        report.ClearedBytes.ShouldBe(Encoding.UTF8.GetByteCount(arguments) + Encoding.UTF8.GetByteCount(result) + Encoding.UTF8.GetByteCount(duplicate));
        report.OrphanedInvocationsDeleted.ShouldBe(0);
        report.HasMore.ShouldBeFalse();

        var freshStore = CreateStore();
        (await freshStore.GetAsync(SessionId.From("disposition"))).ShouldNotBeNull();
        var repeated = freshStore.CleanupLegacyToolPayloads(10);
        repeated.CleanedInvocations.ShouldBe(0);
        repeated.CleanedRows.ShouldBe(0);
        repeated.ClearedBytes.ShouldBe(0);
        repeated.OrphanedInvocationsDeleted.ShouldBe(0);
        repeated.HasMore.ShouldBeFalse();
        await using var reopened = await OpenAsync();
        (await ReadTableBytesAsync(reopened, "tool_invocations")).ShouldBe(invocationBefore, disposition);
        (await ReadTableBytesAsync(reopened, "session_history", predicate: protectedHistory)).ShouldBe(historyBefore, disposition);
        (await ReadTableBytesAsync(reopened, "session_history", predicate: "tool_call_id='safe'")).ShouldBe(safeExpected);
    }

    [Fact]
    public async Task CleanupLegacyToolPayloads_ProtectsAmbiguousOrNonAuthoritativeInvocations()
    {
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("protected"), AgentId.From("agent"));
        await store.SaveAsync(session);
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tool_invocations(session_id,tool_call_id,tool_name,arguments_json,started_at,completed_at,status,is_error,result_content,result_bytes,result_sha256,retention_state)
            VALUES
              ('protected','error','x','{}','a','b','error',1,'r',1,'x','hot'),
              ('protected','warm','x','{}','a','b','success',0,'r',1,'x','warm'),
              ('protected','null-payload','x',NULL,'a','b','success',0,'r',1,'x','hot'),
              ('protected','mismatch','x','{}','a','b','success',0,'authoritative',13,'x','hot'),
              ('protected','duplicate','x','{}','a','b','success',0,'r',1,'x','hot');
            INSERT INTO session_history(session_id,role,content,timestamp,tool_name,tool_call_id,tool_args,tool_is_error,message_kind,tool_invocation_id)
            SELECT 'protected','tool','start','a','x',tool_call_id,'{}',0,'tool-start',id FROM tool_invocations;
            INSERT INTO session_history(session_id,role,content,timestamp,tool_name,tool_call_id,tool_args,tool_is_error,message_kind,tool_invocation_id)
            SELECT 'protected','tool','r','b','x',tool_call_id,NULL,CASE WHEN tool_call_id='error' THEN 1 ELSE 0 END,'tool-result',id FROM tool_invocations;
            INSERT INTO session_history(session_id,role,content,timestamp,tool_name,tool_call_id,tool_args,tool_is_error,message_kind,tool_invocation_id)
            SELECT 'protected','tool','second start','a','x','duplicate','{}',0,'tool-start',id FROM tool_invocations WHERE tool_call_id='duplicate';
            INSERT INTO session_history(session_id,role,content,timestamp,tool_name,tool_call_id,tool_args,tool_is_error,message_kind,tool_invocation_id)
            VALUES('protected','tool','unlinked','b','x','warm',NULL,0,'tool-result',NULL);
            """;
        await command.ExecuteNonQueryAsync();

        var report = store.CleanupLegacyToolPayloads(20);

        report.CleanedInvocations.ShouldBe(0);
        report.ProtectedInvocations.ShouldBe(-1);
        report.RemainingInvocations.ShouldBe(-1);
        (await ScalarLongAsync(connection, "SELECT COUNT(*) FROM session_history WHERE tool_args IS NOT NULL OR content='r'")).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task CleanupLegacyToolPayloads_LargeProtectedPopulationScansOnlyBatchBound()
    {
        const int protectedCount = 250;
        const int batchSize = 17;
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("cursor"), AgentId.From("agent"));
        await store.SaveAsync(session);
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE sequence(value) AS (
                SELECT 1
                UNION ALL SELECT value + 1 FROM sequence WHERE value < $protectedCount)
            INSERT INTO tool_invocations(session_id,tool_call_id,tool_name,arguments_json,started_at,completed_at,status,is_error,result_content,result_bytes,result_sha256,retention_state)
            SELECT 'cursor','protected-' || value,'x','{}','a','b','success',0,'different',9,'bad','hot' FROM sequence;
            INSERT INTO tool_invocations(session_id,tool_call_id,tool_name,arguments_json,started_at,completed_at,status,is_error,result_content,result_bytes,result_sha256,retention_state)
            VALUES ('cursor','safe','x','{}','a','b','success',0,'r',1,'454349e422f05297191ead13e21d3db520e5abef52055e4964b82fb213f593a1','hot');
            INSERT INTO session_history(session_id,role,content,timestamp,tool_name,tool_call_id,tool_args,tool_is_error,message_kind,tool_invocation_id)
            SELECT 'cursor','tool','start','a','x',tool_call_id,'{}',0,'tool-start',id FROM tool_invocations WHERE session_id='cursor';
            INSERT INTO session_history(session_id,role,content,timestamp,tool_name,tool_call_id,tool_args,tool_is_error,message_kind,tool_invocation_id)
            SELECT 'cursor','tool','r','b','x',tool_call_id,NULL,0,'tool-result',id FROM tool_invocations WHERE session_id='cursor';
            """;
        command.Parameters.AddWithValue("$protectedCount", protectedCount);
        await command.ExecuteNonQueryAsync();

        long cursor = 0;
        var calls = 0;
        LegacyToolPayloadCleanupReport report;
        do
        {
            report = store.CleanupLegacyToolPayloads(batchSize, cursor);
            report.ScannedInvocations.ShouldBeLessThanOrEqualTo(batchSize);
            report.LastScannedInvocationId.ShouldBeGreaterThanOrEqualTo(cursor);
            cursor = report.LastScannedInvocationId;
            calls++;
        } while (report.HasMore);

        calls.ShouldBe((int)Math.Ceiling((protectedCount + 1d) / batchSize));
        report.CleanedInvocations.ShouldBe(1);
        report.ScannedInvocations.ShouldBe((protectedCount + 1) % batchSize);
        (await ScalarLongAsync(connection, "SELECT COUNT(*) FROM session_history WHERE tool_call_id='safe' AND (tool_args IS NOT NULL OR (message_kind='tool-result' AND content IS NOT NULL))")).ShouldBe(0);
        (await ScalarLongAsync(connection, "SELECT COUNT(*) FROM session_history WHERE tool_call_id LIKE 'protected-%' AND (tool_args IS NOT NULL OR (message_kind='tool-result' AND content IS NOT NULL))")).ShouldBe(protectedCount * 2L);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    public async Task CleanupLegacyToolPayloads_CandidateReaderDoesNotAggregatePayloadsBeyondWindow(int invocationCount)
    {
        const int batchSize = 17;
        const int payloadBytes = 8192;
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("discovery-cost"), AgentId.From("agent"));
        await store.SaveAsync(session);
        await using var connection = await OpenAsync();
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                WITH RECURSIVE sequence(value) AS (
                    SELECT 1 UNION ALL SELECT value + 1 FROM sequence WHERE value < $count)
                INSERT INTO tool_invocations(session_id,tool_call_id,arguments_json,status,is_error,result_content,retention_state)
                SELECT 'discovery-cost','call-' || value,'{}','success',0,$payload,'hot' FROM sequence;
                INSERT INTO session_history(session_id,role,content,timestamp,tool_call_id,tool_args,message_kind,tool_invocation_id)
                SELECT session_id,'tool','start','a',tool_call_id,'{}','tool-start',id
                FROM tool_invocations ORDER BY id DESC;
                INSERT INTO session_history(session_id,role,content,timestamp,tool_call_id,message_kind,tool_invocation_id)
                SELECT session_id,'tool',$payload,'b',tool_call_id,'tool-result',id
                FROM tool_invocations ORDER BY id DESC;
                """;
            seed.Parameters.AddWithValue("$count", invocationCount);
            seed.Parameters.AddWithValue("$payload", new string('x', payloadBytes));
            await seed.ExecuteNonQueryAsync();
        }

        // Count actual payload aggregate work, not elapsed time or merely returned rows.
        // Override only this connection's length(BLOB), preserving its SQLite semantics.
        long measuredBytes = 0;
        connection.CreateFunction<byte[]?, long?>("length", value =>
        {
            if (value is null) return null;
            measuredBytes += value.Length;
            return value.LongLength;
        });
        var readWindow = typeof(LegacyToolPayloadCleanup).GetMethod("ReadCandidateWindow",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        readWindow.ShouldNotBeNull();
        // length(BLOB) observes only emitted aggregates: a GROUP BY sorter can already have
        // copied the entire payload corpus. Count VM work on the real production command.
        // Neither native callback re-enters SQLite or enumerates statement handles.
        long vmSteps = 0;
        var statements = new List<string>();
        var handle = connection.Handle ?? throw new InvalidOperationException("SQLite connection must be open.");
        SQLitePCL.raw.sqlite3_trace(handle,
            new SQLitePCL.strdelegate_trace((_, sql) => statements.Add(sql)), null);
        SQLitePCL.raw.sqlite3_progress_handler(handle, 1, _ => { vmSteps++; return 0; }, null);
        object? window;
        try
        {
            window = readWindow.Invoke(null, [connection, batchSize, 0L]);
        }
        finally
        {
            SQLitePCL.raw.sqlite3_progress_handler(handle, 0, null, null);
            SQLitePCL.raw.sqlite3_trace(handle, (SQLitePCL.strdelegate_trace?)null, null);
        }
        var firstWindowSteps = vmSteps;
        var candidates = window as System.Collections.ICollection
            ?? throw new InvalidOperationException("Candidate reader must return a collection.");
        candidates.Count.ShouldBe(batchSize);
        measuredBytes.ShouldBe(batchSize * (payloadBytes + 2L),
            "payload aggregate work must stop at the ID window even when the corpus grows");

        measuredBytes = 0;
        vmSteps = 0;
        SQLitePCL.raw.sqlite3_progress_handler(handle, 1, _ => { vmSteps++; return 0; }, null);
        try
        {
            window = readWindow.Invoke(null, [connection, batchSize, (long)invocationCount - 1]);
        }
        finally
        {
            SQLitePCL.raw.sqlite3_progress_handler(handle, 0, null, null);
        }
        var finalCandidates = window as System.Collections.ICollection
            ?? throw new InvalidOperationException("Candidate reader must return a collection.");
        finalCandidates.Count.ShouldBe(1);
        measuredBytes.ShouldBe(payloadBytes + 2L, "the cursor must seek rather than reaggregate prior payloads");

        // Explain the exact traced production statement, not a copied SQL approximation.
        statements.Count.ShouldBe(1);
        var plan = await ReadStringsAsync(connection, "EXPLAIN QUERY PLAN " + statements[0], ordinal: 3);
        var diagnostic = $"corpus={invocationCount}, window={batchSize}, first-window VM steps={firstWindowSteps}, "
            + $"cursor VM steps={vmSteps}; actual production plan: {string.Join("; ", plan)}";
        // A generous per-candidate instruction budget tolerates planner/version differences,
        // but rejects the whole-corpus payload sorter (about 195k/1.5m steps for these seeds).
        // The budget depends only on the requested window, never on invocationCount.
        firstWindowSteps.ShouldBeLessThanOrEqualTo(batchSize * 2000L,
            "candidate discovery must bound payload-bearing work before aggregation; " + diagnostic);
        vmSteps.ShouldBeLessThanOrEqualTo(2000L, "the cursor must seek with bounded VM work; " + diagnostic);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(2048)]
    public async Task CleanupLegacyToolPayloads_CandidateReaderBoundsTemporaryWriteBytes(int invocationCount)
    {
        const int batchSize = 17;
        const int payloadBytes = 16384;
        const int cacheKibibytes = 64;
        // Allow multiple payload copies and record overhead per candidate, but never a
        // corpus-sized allowance. The largest seed has only 64 MiB of stored payloads.
        const long writeBudget = batchSize * (4L * payloadBytes + 4096);
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("scratch-cost"), AgentId.From("agent"));
        await store.SaveAsync(session);
        await using var connection = await OpenAsync();
        var handle = connection.Handle ?? throw new InvalidOperationException("SQLite connection must be open.");
        var sqliteVersion = SQLitePCL.raw.sqlite3_libversion_number();
        _output.WriteLine($"sqlite_version={sqliteVersion}, corpus={invocationCount}, window={batchSize}, payload_bytes={payloadBytes}, write_budget={writeBudget}");
        sqliteVersion.ShouldBe(3053003, "exercise the pinned SQLite 3.53.3 native library");

        // Probe support before allocating the corpus; unsupported native verbs fail, not skip.
        _ = ReadTemporaryWriteBytes(handle, reset: true);
        ReadTemporaryWriteBytes(handle).ShouldBe(0);
        await using (var configure = connection.CreateCommand())
        {
            // These settings belong only to this unpooled connection. main.cache_size
            // controls the external sorter's PMA threshold (subject to SQLite's minimum).
            // Do not use a nonexistent sorter pragma or process-global temp directory.
            configure.CommandText = $"PRAGMA temp_store=FILE; PRAGMA main.cache_size=-{cacheKibibytes}; PRAGMA threads=0;";
            await configure.ExecuteNonQueryAsync();
        }
        (await ScalarLongAsync(connection, "PRAGMA temp_store")).ShouldBe(1);
        (await ScalarLongAsync(connection, "PRAGMA main.cache_size")).ShouldBe(-cacheKibibytes);
        (await ScalarLongAsync(connection, "PRAGMA threads")).ShouldBe(0);
        _output.WriteLine($"temp_store=1, main_cache_kibibytes={cacheKibibytes}, sorter_threads=0");

        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                WITH RECURSIVE sequence(value) AS (
                    SELECT 1 UNION ALL SELECT value + 1 FROM sequence WHERE value < $count)
                INSERT INTO tool_invocations(session_id,tool_call_id,arguments_json,status,is_error,result_content,retention_state)
                SELECT 'scratch-cost','call-' || value,'{}','success',0,$payload,'hot' FROM sequence;
                INSERT INTO session_history(session_id,role,content,timestamp,tool_call_id,tool_args,message_kind,tool_invocation_id)
                SELECT session_id,'tool','start','a',tool_call_id,'{}','tool-start',id
                FROM tool_invocations ORDER BY id DESC;
                INSERT INTO session_history(session_id,role,content,timestamp,tool_call_id,message_kind,tool_invocation_id)
                SELECT session_id,'tool',$payload,'b',tool_call_id,'tool-result',id
                FROM tool_invocations ORDER BY id DESC;
                """;
            seed.Parameters.AddWithValue("$count", invocationCount);
            seed.Parameters.AddWithValue("$payload", new string('x', payloadBytes));
            await seed.ExecuteNonQueryAsync();
        }

        // Positive control: a computed order key prevents index-order streaming, and
        // returning content carries the payload into the whole-corpus sorter. Drain it
        // without retaining payloads in managed memory; a row count alone proves no spill.
        _ = ReadTemporaryWriteBytes(handle, reset: true);
        ReadTemporaryWriteBytes(handle).ShouldBe(0);
        var controlRows = 0;
        await using (var control = connection.CreateCommand())
        {
            control.CommandText = """
                SELECT content FROM session_history
                WHERE message_kind='tool-result'
                ORDER BY -tool_invocation_id
                """;
            await using var reader = await control.ExecuteReaderAsync();
            while (await reader.ReadAsync()) controlRows++;
        }
        var controlWriteBytes = ReadTemporaryWriteBytes(handle);
        var controlDiagnostic = $"sqlite_version={sqliteVersion}, corpus={invocationCount}, control_rows={controlRows}, control_write_bytes={controlWriteBytes}, write_budget={writeBudget}";
        _output.WriteLine(controlDiagnostic);
        controlRows.ShouldBe(invocationCount, controlDiagnostic);
        controlWriteBytes.ShouldBeGreaterThan(writeBudget,
            "the payload sorter must actually spill, proving the temporary-write observer is live; " + controlDiagnostic);

        // sqlite3_db_status returns the pre-reset value. Verify both that value and a
        // fresh zero before measuring only the exact production ReadCandidateWindow.
        ReadTemporaryWriteBytes(handle, reset: true).ShouldBe(controlWriteBytes, controlDiagnostic);
        ReadTemporaryWriteBytes(handle).ShouldBe(0, controlDiagnostic);
        var readWindow = typeof(LegacyToolPayloadCleanup).GetMethod("ReadCandidateWindow",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        readWindow.ShouldNotBeNull();
        var window = readWindow.Invoke(null, [connection, batchSize, 0L]);
        var firstWindowWriteBytes = ReadTemporaryWriteBytes(handle);
        var candidates = window as System.Collections.ICollection
            ?? throw new InvalidOperationException("Candidate reader must return a collection.");
        var diagnostic = $"sqlite_version={sqliteVersion}, corpus={invocationCount}, window={batchSize}, control_write_bytes={controlWriteBytes}, first_window_write_bytes={firstWindowWriteBytes}, write_budget={writeBudget}";
        _output.WriteLine(diagnostic);
        candidates.Count.ShouldBe(batchSize, diagnostic);
        firstWindowWriteBytes.ShouldBeLessThanOrEqualTo(writeBudget,
            "candidate discovery must bound temporary-file write bytes independently of corpus size; " + diagnostic);

        _ = ReadTemporaryWriteBytes(handle, reset: true);
        ReadTemporaryWriteBytes(handle).ShouldBe(0, diagnostic);
        window = readWindow.Invoke(null, [connection, batchSize, (long)invocationCount - 1]);
        var cursorWriteBytes = ReadTemporaryWriteBytes(handle);
        var finalCandidates = window as System.Collections.ICollection
            ?? throw new InvalidOperationException("Candidate reader must return a collection.");
        var cursorDiagnostic = $"corpus={invocationCount}, cursor={invocationCount - 1}, cursor_write_bytes={cursorWriteBytes}, write_budget={4L * payloadBytes + 4096}";
        _output.WriteLine(cursorDiagnostic);
        finalCandidates.Count.ShouldBe(1, cursorDiagnostic);
        cursorWriteBytes.ShouldBeLessThanOrEqualTo(4L * payloadBytes + 4096,
            "the cursor must not spill payloads from earlier candidates; " + cursorDiagnostic);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(2048)]
    public async Task CleanupLegacyToolPayloads_CandidateReaderBoundsTemporaryLogicalHighwater(int invocationCount)
    {
        const int batchSize = 17;
        const int payloadBytes = 16384;
        const int cacheKibibytes = 64;
        // Simultaneous logical EOF is independent of cumulative temporary writes and
        // physical allocation. Both allowances depend on the window, never the corpus.
        const long candidateBudget = 4L * payloadBytes + 4096; // 69,632 bytes.
        const long windowBudget = batchSize * candidateBudget; // 1,183,744 bytes.
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("logical-scratch-cost"), AgentId.From("agent"));
        await store.SaveAsync(session);
        using var observer = new Sessions.TemporaryFileLogicalFootprintVfs();
        using (var lease = observer.OpenConnection(DatabasePath))
        {
            // Trusted borrow: all commands/readers and native observations stay inside
            // this lease. Only the lease owns connection disposal; no raw resources escape.
            var connection = lease.Connection;
            observer.VerifySameNativeLibrary(connection);
            var handle = connection.Handle ?? throw new InvalidOperationException("SQLite connection must be open.");
            var sqliteVersion = SQLitePCL.raw.sqlite3_libversion_number();
            sqliteVersion.ShouldBe(3053003, "exercise the pinned SQLite 3.53.3 native library");
            _ = ReadTemporaryWriteBytes(handle, reset: true);
            ReadTemporaryWriteBytes(handle).ShouldBe(0);
            await using (var configure = connection.CreateCommand())
            {
                configure.CommandText = $"PRAGMA temp_store=FILE; PRAGMA main.cache_size=-{cacheKibibytes}; PRAGMA threads=0;";
                await configure.ExecuteNonQueryAsync();
            }
            (await ScalarLongAsync(connection, "PRAGMA temp_store")).ShouldBe(1);
            (await ScalarLongAsync(connection, "PRAGMA main.cache_size")).ShouldBe(-cacheKibibytes);
            (await ScalarLongAsync(connection, "PRAGMA threads")).ShouldBe(0);
            _output.WriteLine($"sqlite_version={sqliteVersion}, corpus={invocationCount}, window={batchSize}, payload_bytes={payloadBytes}, temp_store=1, main_cache_kibibytes={cacheKibibytes}, sorter_threads=0");

            await using (var seed = connection.CreateCommand())
            {
                seed.CommandText = """
                    WITH RECURSIVE sequence(value) AS (
                        SELECT 1 UNION ALL SELECT value + 1 FROM sequence WHERE value < $count)
                    INSERT INTO tool_invocations(session_id,tool_call_id,arguments_json,status,is_error,result_content,retention_state)
                    SELECT 'logical-scratch-cost','call-' || value,'{}','success',0,$payload,'hot' FROM sequence;
                    INSERT INTO session_history(session_id,role,content,timestamp,tool_call_id,tool_args,message_kind,tool_invocation_id)
                    SELECT session_id,'tool','start','a',tool_call_id,'{}','tool-start',id
                    FROM tool_invocations ORDER BY id DESC;
                    INSERT INTO session_history(session_id,role,content,timestamp,tool_call_id,message_kind,tool_invocation_id)
                    SELECT session_id,'tool',$payload,'b',tool_call_id,'tool-result',id
                    FROM tool_invocations ORDER BY id DESC;
                    """;
                seed.Parameters.AddWithValue("$count", invocationCount);
                seed.Parameters.AddWithValue("$payload", new string('x', payloadBytes));
                await seed.ExecuteNonQueryAsync();
            }

            observer.EnsureValid();
            observer.CurrentOpenTemporaryLogicalBytes.ShouldBe(0);
            observer.ScratchCloseCount.ShouldBe(observer.ScratchOpenCount);
            observer.Reset();
            _ = ReadTemporaryWriteBytes(handle, reset: true);
            ReadTemporaryWriteBytes(handle).ShouldBe(0);
            observer.PeakOpenTemporaryLogicalBytes.ShouldBe(0);
            var controlRows = 0;
            await using (var control = connection.CreateCommand())
            {
                // Deliberately non-index order, carrying payload through a real sorter.
                // This is a spill control, not a copy of the production candidate SQL.
                control.CommandText = """
                    SELECT content FROM session_history
                    WHERE message_kind='tool-result'
                    ORDER BY -tool_invocation_id
                    """;
                await using var reader = await control.ExecuteReaderAsync();
                while (await reader.ReadAsync()) controlRows++;
            }
            observer.EnsureValid();
            var controlWrites = ReadTemporaryWriteBytes(handle);
            var controlPeak = observer.PeakOpenTemporaryLogicalBytes;
            var controlDiagnostic = $"phase=spill-control, sqlite_version={sqliteVersion}, corpus={invocationCount}, window={batchSize}, cursor=none, rows={controlRows}, cumulative_temporary_write_bytes={controlWrites}, peak_open_temporary_logical_bytes={controlPeak}, current_open_temporary_logical_bytes={observer.CurrentOpenTemporaryLogicalBytes}, scratch_opens={observer.ScratchOpenCount}, scratch_closes={observer.ScratchCloseCount}, flags={string.Join(',', observer.ScratchFlags)}, budget={windowBudget}";
            _output.WriteLine(controlDiagnostic);
            controlRows.ShouldBe(invocationCount, controlDiagnostic);
            observer.ScratchOpenCount.ShouldBeGreaterThan(0, controlDiagnostic);
            controlPeak.ShouldBeGreaterThan(windowBudget,
                "the logical EOF observer must detect a real whole-corpus spill; " + controlDiagnostic);
            controlWrites.ShouldBeGreaterThan(windowBudget,
                "the independent cumulative-write counter must detect the same spill; " + controlDiagnostic);
            observer.CurrentOpenTemporaryLogicalBytes.ShouldBe(0, controlDiagnostic);
            observer.ScratchCloseCount.ShouldBe(observer.ScratchOpenCount, controlDiagnostic);

            var readWindow = typeof(LegacyToolPayloadCleanup).GetMethod("ReadCandidateWindow",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            readWindow.ShouldNotBeNull();
            var statements = new List<string>();
            var priorWrites = controlWrites;
            foreach (var (cursor, expectedRows, budget) in new[]
            {
                (0L, batchSize, windowBudget),
                ((long)invocationCount - 1, 1, candidateBudget),
                ((long)invocationCount, 0, candidateBudget)
            })
            {
                // Reset only after every scratch file has closed. Prove a clean epoch
                // separately for logical residency and the cumulative-write counter.
                observer.EnsureValid();
                observer.CurrentOpenTemporaryLogicalBytes.ShouldBe(0);
                observer.ScratchCloseCount.ShouldBe(observer.ScratchOpenCount);
                observer.Reset();
                observer.PeakOpenTemporaryLogicalBytes.ShouldBe(0);
                observer.ScratchOpenCount.ShouldBe(0);
                observer.ScratchCloseCount.ShouldBe(0);
                ReadTemporaryWriteBytes(handle, reset: true).ShouldBe(priorWrites);
                ReadTemporaryWriteBytes(handle).ShouldBe(0);

                object? window;
                SQLitePCL.raw.sqlite3_trace(handle,
                    new SQLitePCL.strdelegate_trace((_, sql) => statements.Add(sql)), null);
                try
                {
                    // Execute the actual private reader, never a copied SQL approximation.
                    window = readWindow.Invoke(null, [connection, batchSize, cursor]);
                }
                finally
                {
                    SQLitePCL.raw.sqlite3_trace(handle, (SQLitePCL.strdelegate_trace?)null, null);
                }
                var candidates = window as System.Collections.ICollection
                    ?? throw new InvalidOperationException("Candidate reader must return a collection.");
                observer.EnsureValid();
                var writes = ReadTemporaryWriteBytes(handle);
                var peak = observer.PeakOpenTemporaryLogicalBytes;
                var diagnostic = $"phase=actual-reader, sqlite_version={sqliteVersion}, corpus={invocationCount}, window={batchSize}, cursor={cursor}, rows={candidates.Count}, cumulative_temporary_write_bytes={writes}, peak_open_temporary_logical_bytes={peak}, current_open_temporary_logical_bytes={observer.CurrentOpenTemporaryLogicalBytes}, scratch_opens={observer.ScratchOpenCount}, scratch_closes={observer.ScratchCloseCount}, flags={string.Join(',', observer.ScratchFlags)}, budget={budget}";
                _output.WriteLine(diagnostic);
                candidates.Count.ShouldBe(expectedRows, diagnostic);
                peak.ShouldBeLessThanOrEqualTo(budget,
                    "candidate discovery must bound simultaneous temporary logical EOF before payload aggregation; " + diagnostic);
                writes.ShouldBeLessThanOrEqualTo(budget,
                    "cumulative temporary writes remain a separate bounded metric; " + diagnostic);
                observer.CurrentOpenTemporaryLogicalBytes.ShouldBe(0, diagnostic);
                observer.ScratchCloseCount.ShouldBe(observer.ScratchOpenCount, diagnostic);
                priorWrites = writes;
            }

            // Explain only after all measured epochs; use the exact traced production
            // statements, so diagnostics cannot silently drift to a test-owned query.
            statements.Count.ShouldBe(3);
            foreach (var statement in statements)
            {
                var plan = await ReadStringsAsync(connection, "EXPLAIN QUERY PLAN " + statement, ordinal: 3);
                _output.WriteLine($"sqlite_version={sqliteVersion}, corpus={invocationCount}, actual production statement: {statement}; plan: {string.Join("; ", plan)}");
            }
        }
        observer.ActiveConnectionCount.ShouldBe(0);
        observer.OpenFileCount.ShouldBe(0);
        observer.EnsureValid();
    }

    private long ReadTemporaryWriteBytes(SQLitePCL.sqlite3 handle, bool reset = false)
    {
        // SQLITE_DBSTATUS_TEMPBUF_SPILL=13 reports cumulative temporary-file WRITE
        // BYTES, not peak allocation or page counts; its high-water value is always 0.
        // https://www.sqlite.org/c3ref/c_dbstatus%20options.html
        // The managed raw binding uses 32-bit outputs (SQLite masks to 31 bits).
        // Keep this fixture's cumulative writes far below 2 GiB to avoid truncation.
        const int temporaryBufferSpill = 13;
        var rc = SQLitePCL.raw.sqlite3_db_status(handle, temporaryBufferSpill,
            out var current, out var highwater, reset ? 1 : 0);
        var diagnostic = $"dbstatus_verb={temporaryBufferSpill}, rc={rc}, current_write_bytes={current}, highwater={highwater}, reset={(reset ? 1 : 0)}";
        _output.WriteLine(diagnostic);
        rc.ShouldBe(SQLitePCL.raw.SQLITE_OK, "the pinned native library must support the temporary-write counter; " + diagnostic);
        highwater.ShouldBe(0, diagnostic);
        current.ShouldBeGreaterThanOrEqualTo(0, diagnostic);
        return current;
    }

    [Fact]
    public async Task CleanupLegacyToolPayloads_RemovesOrphanedInvocationsInBoundedBatches()
    {
        var store = CreateStore();
        _ = await store.GetAsync(SessionId.From("probe"));
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tool_invocations(session_id,tool_call_id,status) VALUES
              ('missing-1','a','unknown'),
              ('missing-2','b','unknown');
            """;
        await command.ExecuteNonQueryAsync();

        var first = store.CleanupLegacyToolPayloads(1);
        var second = store.CleanupLegacyToolPayloads(1, first.LastScannedInvocationId);

        first.OrphanedInvocationsDeleted.ShouldBe(1);
        first.HasMore.ShouldBeTrue();
        second.OrphanedInvocationsDeleted.ShouldBe(1);
        second.HasMore.ShouldBeFalse();
        (await ScalarLongAsync(connection, "SELECT COUNT(*) FROM tool_invocations WHERE session_id LIKE 'missing-%'")).ShouldBe(0);
    }

    [Fact]
    public async Task DeleteAsync_RemovesHistoryInvocationAndSessionAtomically()
    {
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("delete-all"), AgentId.From("agent"));
        session.AddEntry(new SessionEntry { Role = MessageRole.Tool, Content = "result", ToolName = "read", ToolCallId = "call", Kind = MessageKind.ToolResult });
        await store.SaveAsync(session);

        await store.DeleteAsync(SessionId.From("delete-all"));

        await using var connection = await OpenAsync();
        (await ScalarLongAsync(connection, "SELECT COUNT(*) FROM sessions WHERE id='delete-all'")).ShouldBe(0);
        (await ScalarLongAsync(connection, "SELECT COUNT(*) FROM session_history WHERE session_id='delete-all'")).ShouldBe(0);
        (await ScalarLongAsync(connection, "SELECT COUNT(*) FROM tool_invocations WHERE session_id='delete-all'")).ShouldBe(0);
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

    private async Task RestoreLegacyPayloadsAsync(string sessionId)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE session_history AS h SET
                tool_args=CASE WHEN h.message_kind='tool-start' THEN i.arguments_json ELSE h.tool_args END,
                content=CASE WHEN h.message_kind='tool-result' THEN i.result_content ELSE h.content END
            FROM tool_invocations i WHERE h.tool_invocation_id=i.id AND h.session_id=$session
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        await command.ExecuteNonQueryAsync();
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
    private static async Task<List<string>> ReadStringsAsync(SqliteConnection connection,string sql,int ordinal=0) { await using var command=connection.CreateCommand(); command.CommandText=sql; await using var reader=await command.ExecuteReaderAsync(); var values=new List<string>(); while(await reader.ReadAsync()) values.Add(reader.GetString(ordinal)); return values; }
    private static async Task<long> ScalarLongAsync(SqliteConnection connection,string sql) { await using var command=connection.CreateCommand(); command.CommandText=sql; return Convert.ToInt64(await command.ExecuteScalarAsync()); }
    private static string? ReadNullable(SqliteDataReader reader,int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    public void Dispose() { SqlitePoolCleanup.ClearPoolForConnectionString(ConnectionString); if(Directory.Exists(_directoryPath)) Directory.Delete(_directoryPath,true); }
    private sealed record InvocationRow(string? ToolName,string? ArgumentsJson,string? StartedAt,string? CompletedAt,string Status,string? ResultContent,long ResultBytes,string? ResultSha256,long LinkedRows);
}
