using System.Text;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Audit;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

public sealed class SingleCopyToolResultArgumentsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), nameof(SingleCopyToolResultArgumentsTests), Guid.NewGuid().ToString("N"));
    private readonly InMemoryConversationStore _conversations = new();
    private string ConnectionString => $"Data Source={Path.Combine(_directory, "sessions.db")};Pooling=False";

    public SingleCopyToolResultArgumentsTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DefaultAuditProjections_StoreArgumentsOnceAndReconstructRichHistory(bool isError, bool incomplete)
    {
        const string arguments = "{\"marker\":\"result-args-should-live-once\",\"unicode\":\"\u03b1\u2713\"}";
        const string resultContent = "tool result \u2713";
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("single-copy"), AgentId.From("agent"));
        await using (var raw = await OpenAsync())
        await using (var guard = raw.CreateCommand())
        {
            guard.CommandText = """
                CREATE TRIGGER no_fresh_tool_payload BEFORE INSERT ON session_history
                WHEN NEW.message_kind IN ('tool-start','tool-result') AND NEW.tool_call_id IS NOT NULL
                  AND (NEW.tool_args IS NOT NULL OR (NEW.message_kind='tool-result' AND NEW.content IS NOT NULL))
                BEGIN SELECT RAISE(ABORT,'history must contain references, not tool payloads'); END
                """;
            await guard.ExecuteNonQueryAsync();
        }
        var sink = new DefaultToolAuditSink();
        var start = sink.ProjectStart("provider-call", "probe", arguments);
        var result = incomplete
            ? sink.ProjectIncomplete("provider-call", "probe", arguments)
            : sink.ProjectResult("provider-call", "probe", resultContent, isError, 0, arguments);

        start.ToolArgs.ShouldBe(arguments);
        result.ToolArgs.ShouldBe(arguments);
        session.AddEntry(start);
        await store.SaveAsync(session);
        await store.AppendEntriesAsync(session.SessionId, [result]);

        await AssertOnlyInvocationOwnsDuplicateArgumentsAsync();
        var loaded = (await CreateStore().GetAsync(session.SessionId)).ShouldNotBeNull();
        loaded.GetHistorySnapshot().Select(entry => (entry.Kind, entry.ToolArgs, entry.Content, entry.ToolIsError, entry.ToolIsIncomplete))
            .ShouldBe([
                ((MessageKind?)MessageKind.ToolStart, arguments, "Tool 'probe' started.", false, false),
                ((MessageKind?)MessageKind.ToolResult, arguments, incomplete
                    ? "Tool 'probe' did not complete \u2014 result synthesized for transcript consistency."
                    : resultContent, isError || incomplete, incomplete)
            ]);

        // Aggregate replacement/reconciliation and a repeated save must keep the narrowed shape
        // while preserving the original in-memory history projection.
        loaded.ReplaceHistory(loaded.GetHistorySnapshot());
        await CreateStore().SaveAsync(loaded);
        await CreateStore().SaveAsync(loaded);
        await AssertOnlyInvocationOwnsDuplicateArgumentsAsync();

        var bounded = await CreateStore().ScanHistoryAsync(session.SessionId, new SessionHistoryScan(AgentId.From("agent"), 2, 4096));
        bounded.Complete.ShouldBeTrue();
        bounded.Entries.Select(entry => entry.Content).ShouldBe(loaded.GetHistorySnapshot().Select(entry => entry.Content));
        var truncated = await CreateStore().ScanHistoryAsync(session.SessionId, new SessionHistoryScan(AgentId.From("agent"), 1, 4096));
        truncated.Complete.ShouldBeFalse();
        truncated.Entries.ShouldHaveSingleItem().Content.ShouldBe(start.Content);

        var invocation = await ReadInvocationAsync();
        invocation.Arguments.ShouldBe(arguments);
        invocation.ResultContent.ShouldBe(incomplete ? result.Content : resultContent);
    }

    [Fact]
    public async Task DefaultAuditResult_WithIncorrectArguments_UsesCanonicalInvocationArguments()
    {
        const string startArguments = "{\"source\":\"start\"}";
        const string resultArguments = "{\"source\":\"distinct-result-projection\"}";
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("distinct-result"), AgentId.From("agent"));
        var sink = new DefaultToolAuditSink();
        session.AddEntries([
            sink.ProjectStart("provider-call", "probe", startArguments),
            sink.ProjectResult("provider-call", "probe", "distinct projection result", false, 0, resultArguments)
        ]);

        await store.SaveAsync(session);

        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT tool_args FROM session_history WHERE message_kind='tool-result'";
        (await command.ExecuteScalarAsync()).ShouldBe(DBNull.Value);
        var loaded = (await CreateStore().GetAsync(session.SessionId)).ShouldNotBeNull();
        loaded.GetHistorySnapshot().Single(entry => entry.Kind == MessageKind.ToolResult).ToolArgs.ShouldBe(startArguments);
    }

    [Fact]
    public async Task CleanupPlan_IncludesReconstructedResultBytes()
    {
        const string arguments = "{\"ignored-for-this-measure\":true}";
        const string resultContent = "reconstructed result \u2713";
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("result-bytes"), AgentId.From("agent"));
        var sink = new DefaultToolAuditSink();
        session.AddEntries([
            sink.ProjectStart("provider-call", "probe", arguments),
            sink.ProjectResult("provider-call", "probe", resultContent, false, 0, arguments)
        ]);
        await store.SaveAsync(session);

        var planRow = (await store.ListCleanupPlanAsync(10, includeBytes: true)).Rows.ShouldHaveSingleItem();
        var expected = Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(session.Metadata))
            + 2 * 64
            + Encoding.UTF8.GetByteCount("Tool 'probe' started.")
            + Encoding.UTF8.GetByteCount(resultContent)
            + 2 * Encoding.UTF8.GetByteCount("probe")
            + 2 * Encoding.UTF8.GetByteCount("provider-call");
        planRow.Bytes.ShouldBe(expected);
    }

    [Fact]
    public async Task HistoricallyAbsentResultArguments_RemainAbsentAfterReloadAndReplacement()
    {
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("absent-result-args"), AgentId.From("agent"));
        session.AddEntries([
            new SessionEntry { Role = MessageRole.Tool, Content = "started", ToolCallId = "call", ToolName = "probe", ToolArgs = "{}", Kind = MessageKind.ToolStart },
            new SessionEntry { Role = MessageRole.Tool, Content = "result", ToolCallId = "call", ToolName = "probe", Kind = MessageKind.ToolResult }
        ]);
        await store.SaveAsync(session);

        var loaded = (await CreateStore().GetAsync(session.SessionId)).ShouldNotBeNull();
        loaded.GetHistorySnapshot().Last().ToolArgs.ShouldBeNull();
        loaded.ReplaceHistory(loaded.GetHistorySnapshot());
        await CreateStore().SaveAsync(loaded);
        var reloaded = (await CreateStore().GetAsync(session.SessionId)).ShouldNotBeNull();
        reloaded.GetHistorySnapshot().Last().ToolArgs.ShouldBeNull();
    }

    [Fact]
    public async Task Replacement_ChangesInvocationArgumentsAndReconstructsCanonicalResultArguments()
    {
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("replace-args"), AgentId.From("agent"));
        var sink = new DefaultToolAuditSink();
        session.AddEntries([
            sink.ProjectStart("call", "probe", "{\"version\":1}"),
            sink.ProjectResult("call", "probe", "result", false, 0, "{\"version\":1}")
        ]);
        await store.SaveAsync(session);
        var loaded = (await CreateStore().GetAsync(session.SessionId)).ShouldNotBeNull();
        var entries = loaded.GetHistorySnapshot();
        loaded.ReplaceHistory([entries[0] with { ToolArgs = "{\"version\":2}" }, entries[1]]);
        await CreateStore().SaveAsync(loaded);

        var reloaded = (await CreateStore().GetAsync(session.SessionId)).ShouldNotBeNull();
        reloaded.GetHistorySnapshot().Select(entry => entry.ToolArgs).ShouldBe(["{\"version\":2}", "{\"version\":2}"]);
    }

    [Fact]
    public async Task BulkMigration_CanonicalizesIncorrectArgumentsAcrossAllOutcomesAndPersistsCompletion()
    {
        const string canonical = "{\"canonical\":true}";
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("bulk-outcomes"), AgentId.From("agent"));
        var sink = new DefaultToolAuditSink();
        for (var index = 0; index < 301; index++)
        {
            var call = $"bulk-{index}";
            session.AddEntries([sink.ProjectStart(call,"probe",canonical),
                index % 3 == 2 ? sink.ProjectIncomplete(call,"probe",canonical)
                    : sink.ProjectResult(call,"probe",$"résultat {index}",index % 3 == 1,0,canonical)]);
        }
        await store.SaveAsync(session);
        await using (var connection = await OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE session_history AS h
                SET tool_args=CASE WHEN h.message_kind='tool-start' THEN i.arguments_json ELSE '{"incorrect":true}' END,
                    content=CASE WHEN h.message_kind='tool-result' THEN i.result_content ELSE h.content END,
                    tool_args_from_invocation=0
                FROM tool_invocations i WHERE i.id=h.tool_invocation_id;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var report = store.MigrateLegacyToolData();
        report.Cleanup.CleanedRows.ShouldBe(602);
        await using (var connection = await OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM session_history WHERE tool_args IS NOT NULL OR (message_kind='tool-result' AND content IS NOT NULL)";
            Convert.ToInt64(await command.ExecuteScalarAsync()).ShouldBe(0);
            command.CommandText = "SELECT COUNT(*) FROM legacy_tool_bulk_migration WHERE version=1";
            Convert.ToInt64(await command.ExecuteScalarAsync()).ShouldBe(1);
        }
        var reloaded = (await CreateStore().GetAsync(session.SessionId)).ShouldNotBeNull();
        reloaded.GetHistorySnapshot().ShouldAllBe(entry => entry.ToolArgs == canonical);
        reloaded.GetHistorySnapshot().Count.ShouldBe(602);
        var results = reloaded.GetHistorySnapshot().Where(entry => entry.Kind == MessageKind.ToolResult).ToArray();
        results.Select(entry => entry.Content).ShouldBe(Enumerable.Range(0, 301).Select(index => index % 3 == 2
            ? sink.ProjectIncomplete($"bulk-{index}", "probe", canonical).Content : $"résultat {index}"));
        results.Select(entry => (entry.ToolIsError, entry.ToolIsIncomplete))
            .ShouldBe(Enumerable.Range(0, 301).Select(index => (index % 3 != 0, index % 3 == 2)));
        store.MigrateLegacyToolData().Cleanup.CleanedRows.ShouldBe(0);
    }

    [Fact]
    public async Task BulkMigration_CleanupFailureRollsBackAndRestartCommits()
    {
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("bulk-rollback"), AgentId.From("agent"));
        var sink = new DefaultToolAuditSink();
        session.AddEntries([sink.ProjectStart("call","probe","{}"),sink.ProjectResult("call","probe","result",false,0,"{}")]);
        await store.SaveAsync(session);
        await using (var connection = await OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE session_history AS h SET tool_args=i.arguments_json,
                    content=CASE WHEN h.message_kind='tool-result' THEN i.result_content ELSE h.content END,
                    tool_args_from_invocation=0 FROM tool_invocations i WHERE i.id=h.tool_invocation_id;
                CREATE TRIGGER reject_bulk_clear BEFORE UPDATE ON session_history
                WHEN NEW.message_kind='tool-result' AND NEW.content IS NULL
                BEGIN SELECT RAISE(ABORT,'injected bulk cleanup failure'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }
        Should.Throw<SqliteException>(() => store.MigrateLegacyToolData()).Message.ShouldContain("injected bulk cleanup failure");
        await using (var connection = await OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM session_history WHERE tool_args IS NOT NULL";
            Convert.ToInt64(await command.ExecuteScalarAsync()).ShouldBe(2);
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='legacy_tool_bulk_migration'";
            Convert.ToInt64(await command.ExecuteScalarAsync()).ShouldBe(0);
            command.CommandText = "DROP TRIGGER reject_bulk_clear";
            await command.ExecuteNonQueryAsync();
        }
        CreateStore().MigrateLegacyToolData().Cleanup.CleanedRows.ShouldBe(2);
        var reloaded = (await CreateStore().GetAsync(session.SessionId)).ShouldNotBeNull();
        reloaded.GetHistorySnapshot().Select(entry => (entry.ToolArgs, entry.Content))
            .ShouldBe([("{}", "Tool 'probe' started."), ("{}", "result")]);
    }

    [Fact]
    public async Task BulkMigration_UnlinkedRunsKeepSeparateExecutionsAndUniqueEarlierResults()
    {
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("legacy-runs"), AgentId.From("agent"));
        var sink = new DefaultToolAuditSink();
        var run1 = AgentRunId.From("run-one");
        var run2 = AgentRunId.From("run-two");
        session.AddEntries([
            sink.ProjectStart("reused","probe","{\"run\":1}") with { AgentRunId = run1 },
            sink.ProjectResult("reused","probe","first result",true,0,"{\"run\":1}") with { AgentRunId = run1 },
            sink.ProjectStart("reused","probe","{\"run\":2}") with { AgentRunId = run2 },
            sink.ProjectIncomplete("reused","probe","{\"run\":2}") with { AgentRunId = run2 }
        ]);
        await store.SaveAsync(session);
        var before = (await CreateStore().GetAsync(session.SessionId)).ShouldNotBeNull().GetHistorySnapshot();
        await using (var connection = await OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE session_history AS h SET tool_args=i.arguments_json,
                    content=CASE WHEN h.message_kind='tool-result' THEN i.result_content ELSE h.content END,
                    tool_args_from_invocation=0 FROM tool_invocations i WHERE i.id=h.tool_invocation_id;
                UPDATE session_history SET tool_invocation_id=NULL;
                DELETE FROM tool_invocations;
                """;
            await command.ExecuteNonQueryAsync();
        }
        store.MigrateLegacyToolData().Backfill.InvocationCount.ShouldBe(2);
        var after = (await CreateStore().GetAsync(session.SessionId)).ShouldNotBeNull().GetHistorySnapshot();
        after.Select(entry => (entry.Content,entry.ToolArgs,entry.AgentRunId,entry.ToolIsError,entry.ToolIsIncomplete))
            .ShouldBe(before.Select(entry => (entry.Content,entry.ToolArgs,entry.AgentRunId,entry.ToolIsError,entry.ToolIsIncomplete)));
        await using var verify = await OpenAsync();
        await using var query = verify.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM tool_invocations";
        Convert.ToInt64(await query.ExecuteScalarAsync()).ShouldBe(2);
        query.CommandText = "SELECT COUNT(*) FROM session_history WHERE tool_args IS NOT NULL OR (message_kind='tool-result' AND content IS NOT NULL)";
        Convert.ToInt64(await query.ExecuteScalarAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public async Task BulkMigration_NullTimestampPairUsesRowEvidenceForOutcome(bool isError, bool incomplete, bool linkedResult)
    {
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("legacy-null-time"), AgentId.From("agent"));
        var sink = new DefaultToolAuditSink();
        session.AddEntries([sink.ProjectStart("call", "probe", "{\"start\":true}"), incomplete
            ? sink.ProjectIncomplete("call", "probe", "{\"start\":true}")
            : sink.ProjectResult("call", "probe", "retained result", isError, 0, "{\"start\":true}")]);
        await store.SaveAsync(session);
        await using (var connection = await OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE session_history AS h SET timestamp=NULL,tool_args_from_invocation=0,
                    tool_args=CASE WHEN h.message_kind='tool-start' THEN '{"start":true}' ELSE '{"fallback":true}' END,
                    content=CASE WHEN h.message_kind='tool-result' THEN i.result_content ELSE h.content END,
                    tool_invocation_id=CASE WHEN h.message_kind='tool-result' AND $linked=1 THEN h.tool_invocation_id END
                FROM tool_invocations i WHERE i.id=h.tool_invocation_id;
                UPDATE tool_invocations SET arguments_json='{"fallback":true}',tool_name='result-fallback',
                    started_at=NULL,completed_at=NULL,status='unknown';
                DELETE FROM tool_invocations WHERE $linked=0;
                """;
            command.Parameters.AddWithValue("$linked", linkedResult ? 1 : 0);
            await command.ExecuteNonQueryAsync();
        }
        store.MigrateLegacyToolData();
        await using var verify = await OpenAsync();
        await using var query = verify.CreateCommand();
        query.CommandText = "SELECT arguments_json,tool_name,status,started_at,completed_at,is_error,result_content FROM tool_invocations";
        await using var reader = await query.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        reader.GetString(0).ShouldBe("{\"start\":true}");
        reader.GetString(1).ShouldBe("probe");
        reader.GetString(2).ShouldBe(incomplete ? "unknown" : isError ? "error" : "success");
        reader.IsDBNull(3).ShouldBeTrue();
        reader.IsDBNull(4).ShouldBeTrue();
        reader.GetInt32(5).ShouldBe(isError || incomplete ? 1 : 0);
        reader.GetString(6).ShouldBe(session.GetHistorySnapshot().Last().Content);
    }

    [Fact]
    public async Task BulkMigration_LegacyStartOverridesResultOnlyFallbackArguments()
    {
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("legacy-late-start"), AgentId.From("agent"));
        var sink = new DefaultToolAuditSink();
        session.AddEntries([sink.ProjectStart("call", "probe", "{\"start\":true}"),
            sink.ProjectResult("call", "probe", "retained result", false, 0, "{\"start\":true}")]);
        await store.SaveAsync(session);
        await using (var connection = await OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE session_history SET tool_args='{"start":true}',tool_invocation_id=NULL
                WHERE message_kind='tool-start';
                UPDATE tool_invocations SET arguments_json='{"fallback":true}',started_at=NULL,status='unknown';
                """;
            await command.ExecuteNonQueryAsync();
        }
        store.MigrateLegacyToolData();
        var reloaded = (await CreateStore().GetAsync(session.SessionId)).ShouldNotBeNull();
        reloaded.GetHistorySnapshot().Select(entry => entry.ToolArgs).ShouldBe(["{\"start\":true}", "{\"start\":true}"]);
        reloaded.GetHistorySnapshot().Last().Content.ShouldBe("retained result");
    }

    [Theory]
    [InlineData("hot")]
    [InlineData("warm")]
    [InlineData("cold")]
    [InlineData("expired")]
    public async Task BulkMigration_DedupIgnoresRetentionAndCanonicalNullDoesNotKeepBuggyArguments(string retention)
    {
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("retained-dedup"), AgentId.From("agent"));
        var sink = new DefaultToolAuditSink();
        foreach (var call in new[] { "args", "null-args", "missing-body" })
            session.AddEntries([sink.ProjectStart(call, "probe", "{}"),
                sink.ProjectResult(call, "probe", "retained " + call, false, 0, "{}")]);
        await store.SaveAsync(session);
        await using (var connection = await OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE session_history AS h SET tool_args_from_invocation=0,
                    tool_args=CASE WHEN h.message_kind='tool-start' THEN i.arguments_json ELSE 'buggy arguments' END,
                    content=CASE WHEN h.message_kind='tool-result' THEN i.result_content ELSE h.content END
                FROM tool_invocations i WHERE i.id=h.tool_invocation_id;
                UPDATE tool_invocations SET retention_state=$retention;
                UPDATE tool_invocations SET arguments_json=NULL WHERE tool_call_id='null-args';
                UPDATE session_history SET tool_args=NULL,tool_invocation_id=NULL WHERE tool_call_id='null-args' AND message_kind='tool-start';
                UPDATE tool_invocations SET result_content=NULL,result_bytes=0,result_sha256=NULL WHERE tool_call_id='missing-body';
                """;
            command.Parameters.AddWithValue("$retention", retention);
            await command.ExecuteNonQueryAsync();
        }
        store.MigrateLegacyToolData();
        await using var verify = await OpenAsync();
        await using var query = verify.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM session_history WHERE tool_args IS NOT NULL";
        Convert.ToInt64(await query.ExecuteScalarAsync()).ShouldBe(0);
        query.CommandText = "SELECT COUNT(*) FROM session_history WHERE message_kind='tool-result' AND tool_args_from_invocation=1";
        Convert.ToInt64(await query.ExecuteScalarAsync()).ShouldBe(3);
        query.CommandText = "SELECT content FROM session_history WHERE tool_call_id='missing-body' AND message_kind='tool-result'";
        (await query.ExecuteScalarAsync()).ShouldBe("retained missing-body");
        query.CommandText = "SELECT COUNT(*) FROM session_history WHERE tool_call_id IN ('args','null-args') AND message_kind='tool-result' AND content IS NOT NULL";
        Convert.ToInt64(await query.ExecuteScalarAsync()).ShouldBe(0);
        var loaded = (await CreateStore().GetAsync(session.SessionId)).ShouldNotBeNull();
        loaded.GetHistorySnapshot().Where(entry => entry.ToolCallId == "null-args").ShouldAllBe(entry => entry.ToolArgs == null);
        loaded.GetHistorySnapshot().Where(entry => entry.Kind == MessageKind.ToolResult)
            .Select(entry => entry.Content).ShouldBe(["retained args", "retained null-args", "retained missing-body"]);
    }

    [Theory]
    [InlineData("warm")]
    [InlineData("cold")]
    [InlineData("expired")]
    public async Task BulkMigration_BackfillDoesNotRestoreRemovedRetainedStateResult(string retention)
    {
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("no-resurrection"), AgentId.From("agent"));
        var sink = new DefaultToolAuditSink();
        session.AddEntries([sink.ProjectStart("call", "probe", "{}"), sink.ProjectResult("call", "probe", "historical body", false, 0, "{}")]);
        await store.SaveAsync(session);
        await using (var connection = await OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE session_history SET tool_args='{}',tool_args_from_invocation=0,
                    content=CASE WHEN message_kind='tool-result' THEN 'historical body' ELSE content END,tool_invocation_id=NULL;
                UPDATE tool_invocations SET retention_state=$retention,result_content=NULL,result_bytes=0,result_sha256=NULL;
                """;
            command.Parameters.AddWithValue("$retention", retention);
            await command.ExecuteNonQueryAsync();
        }
        store.MigrateLegacyToolData();
        await using var verify = await OpenAsync();
        await using var query = verify.CreateCommand();
        query.CommandText = "SELECT result_content,result_bytes,result_sha256,retention_state FROM tool_invocations";
        await using (var reader = await query.ExecuteReaderAsync())
        {
            (await reader.ReadAsync()).ShouldBeTrue();
            reader.IsDBNull(0).ShouldBeTrue();
            reader.GetInt64(1).ShouldBe(0);
            reader.IsDBNull(2).ShouldBeTrue();
            reader.GetString(3).ShouldBe(retention);
        }
        query.CommandText = "SELECT content FROM session_history WHERE message_kind='tool-result'";
        (await query.ExecuteScalarAsync()).ShouldBe("historical body");
    }

    [Fact]
    public async Task BulkMigration_UniqueEarlierResultBodyIsNotErased()
    {
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("legacy-unique-result"), AgentId.From("agent"));
        var sink = new DefaultToolAuditSink();
        session.AddEntries([sink.ProjectStart("call","probe","{}"),sink.ProjectResult("call","probe","authoritative result",false,0,"{}")]);
        await store.SaveAsync(session);
        await using (var connection = await OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE session_history SET content='unique earlier result',tool_args='incorrect',tool_args_from_invocation=0
                WHERE message_kind='tool-result';
                """;
            await command.ExecuteNonQueryAsync();
        }
        store.MigrateLegacyToolData();
        var loaded = (await CreateStore().GetAsync(session.SessionId)).ShouldNotBeNull();
        loaded.GetHistorySnapshot().Last().Content.ShouldBe("unique earlier result");
        loaded.GetHistorySnapshot().Last().ToolArgs.ShouldBe("{}");
        var bounded = await CreateStore().ScanHistoryAsync(session.SessionId, new SessionHistoryScan(AgentId.From("agent"), 2, 4096));
        bounded.Entries.Last().Content.ShouldBe("unique earlier result");
        loaded.ReplaceHistory(loaded.GetHistorySnapshot());
        await CreateStore().SaveAsync(loaded);
        var replaced = (await CreateStore().GetAsync(session.SessionId)).ShouldNotBeNull();
        replaced.GetHistorySnapshot().Last().Content.ShouldBe("unique earlier result");
    }

    [Theory]
    [InlineData("get")]
    [InlineData("get-or-create")]
    [InlineData("enumerate")]
    [InlineData("conversation")]
    [InlineData("archive")]
    public async Task BulkMigration_LoadBeforeCommitCannotPublishStaleHistoryAfterInvalidation(string readPath)
    {
        var seedStore = CreateStore();
        var session = await seedStore.GetOrCreateAsync(SessionId.From("cache-race"), AgentId.From("agent"));
        var sink = new DefaultToolAuditSink();
        session.AddEntries([sink.ProjectStart("call", "probe", "{}"), sink.ProjectResult("call", "probe", "result", false, 0, "{}")]);
        await seedStore.SaveAsync(session);
        await using (var connection = await OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE session_history SET tool_args='buggy legacy arguments',tool_args_from_invocation=0 WHERE message_kind='tool-result'";
            await command.ExecuteNonQueryAsync();
        }
        var loadedOldHistory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = CreateStore();
        var publications = 0;
        store.BeforeSessionCachePublicationAsync = async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref publications) == 1)
            {
                loadedOldHistory.SetResult();
                await releasePublication.Task.WaitAsync(cancellationToken);
            }
        };
        Task read = readPath switch
        {
            "get" => store.GetAsync(session.SessionId),
            "get-or-create" => store.GetOrCreateAsync(session.SessionId, AgentId.From("agent")),
            "enumerate" => store.ListAsync(),
            "conversation" => store.ListByConversationAsync(session.ConversationId),
            "archive" => store.ArchiveAsync(session.SessionId),
            _ => throw new ArgumentOutOfRangeException(nameof(readPath))
        };
        try
        {
            await loadedOldHistory.Task.WaitAsync(TimeSpan.FromSeconds(10));
            store.MigrateLegacyToolData().Cleanup.CleanedRows.ShouldBe(1);
        }
        finally
        {
            releasePublication.TrySetResult();
        }
        await read.WaitAsync(TimeSpan.FromSeconds(10));
        store.BeforeSessionCachePublicationAsync = null;
        // The already-returned aggregate is outside this contract. A subsequent read must not
        // obtain its pre-commit projection from the cache, even after the completion fast path.
        store.MigrateLegacyToolData().Cleanup.CleanedRows.ShouldBe(0);
        var current = (await store.GetAsync(session.SessionId)).ShouldNotBeNull();
        current.GetHistorySnapshot().Last().ToolArgs.ShouldBe("{}");
        current.GetHistorySnapshot().Last().Content.ShouldBe("result");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task BulkMigration_LinkedStartAndUnlinkedNullTimestampResultRetainOutcome(bool isError, bool incomplete)
    {
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("linked-start-null-time"), AgentId.From("agent"));
        var sink = new DefaultToolAuditSink();
        session.AddEntries([sink.ProjectStart("call", "probe", "{}"), incomplete
            ? sink.ProjectIncomplete("call", "probe", "{}") : sink.ProjectResult("call", "probe", "result", isError, 0, "{}")]);
        await store.SaveAsync(session);
        await using (var connection = await OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE session_history SET timestamp=NULL,
                    content=CASE WHEN message_kind='tool-result' THEN 'result' ELSE content END,
                    tool_invocation_id=CASE WHEN message_kind='tool-start' THEN tool_invocation_id END;
                UPDATE tool_invocations SET started_at=NULL,completed_at=NULL,status='incomplete',is_error=0,
                    result_content=NULL,result_bytes=0,result_sha256=NULL;
                """;
            await command.ExecuteNonQueryAsync();
        }
        store.MigrateLegacyToolData();
        await using var verify = await OpenAsync();
        await using var query = verify.CreateCommand();
        query.CommandText = "SELECT arguments_json,status,is_error,started_at,completed_at,result_content FROM tool_invocations";
        await using var reader = await query.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        reader.GetString(0).ShouldBe("{}");
        reader.GetString(1).ShouldBe(incomplete ? "unknown" : isError ? "error" : "success");
        reader.GetInt64(2).ShouldBe(isError || incomplete ? 1 : 0);
        reader.IsDBNull(3).ShouldBeTrue();
        reader.IsDBNull(4).ShouldBeTrue();
        reader.GetString(5).ShouldBe("result");
    }

    [Fact]
    public async Task BulkMigration_LinkedNullBodyErrorResultAndUnlinkedStartRetainErrorOutcome()
    {
        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("null-body-error"), AgentId.From("agent"));
        var sink = new DefaultToolAuditSink();
        session.AddEntries([sink.ProjectStart("call", "probe", "{}"), sink.ProjectResult("call", "probe", "result", true, 0, "{}")]);
        await store.SaveAsync(session);
        await using (var connection = await OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE session_history SET timestamp=NULL,
                    tool_args=CASE WHEN message_kind='tool-start' THEN '{}' ELSE tool_args END,
                    tool_invocation_id=CASE WHEN message_kind='tool-result' THEN tool_invocation_id END;
                UPDATE tool_invocations SET started_at=NULL,completed_at=NULL,status='unknown',is_error=1,
                    result_content=NULL,result_bytes=0,result_sha256=NULL;
                """;
            await command.ExecuteNonQueryAsync();
        }
        store.MigrateLegacyToolData();
        await using var verify = await OpenAsync();
        await using var query = verify.CreateCommand();
        query.CommandText = "SELECT status,is_error,result_content,result_bytes,result_sha256 FROM tool_invocations";
        await using var reader = await query.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        reader.GetString(0).ShouldBe("error");
        reader.GetInt64(1).ShouldBe(1);
        reader.IsDBNull(2).ShouldBeTrue();
        reader.GetInt64(3).ShouldBe(0);
        reader.IsDBNull(4).ShouldBeTrue();
    }

    private SqliteSessionStore CreateStore()
        => new(ConnectionString, NullLogger<SqliteSessionStore>.Instance, _conversations);

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private async Task AssertOnlyInvocationOwnsDuplicateArgumentsAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM session_history WHERE tool_args IS NOT NULL";
        Convert.ToInt64(await command.ExecuteScalarAsync()).ShouldBe(0);
        command.CommandText = "SELECT COUNT(*) FROM tool_invocations WHERE arguments_json=$arguments";
        command.Parameters.AddWithValue("$arguments", "{\"marker\":\"result-args-should-live-once\",\"unicode\":\"\u03b1\u2713\"}");
        Convert.ToInt64(await command.ExecuteScalarAsync()).ShouldBe(1);
    }

    private async Task<(string? Arguments, string? ResultContent)> ReadInvocationAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT arguments_json,result_content FROM tool_invocations WHERE session_id='single-copy'";
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolForConnectionString(ConnectionString);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
