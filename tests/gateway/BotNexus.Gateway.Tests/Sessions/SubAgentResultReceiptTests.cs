using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Conversations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Sessions;

public sealed class SubAgentResultReceiptTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"subagent-receipt-{Guid.NewGuid():N}.db");
    private readonly InMemoryConversationStore _conversations = new();
    private static readonly SessionId Parent = SessionId.From("receipt-parent");
    private static readonly ConversationId Conversation = ConversationId.From("receipt-conversation");
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString();
    private SqliteSessionStore Store() => new(ConnectionString, NullLogger<SqliteSessionStore>.Instance, _conversations);

    private async Task<SqliteSessionStore> ArrangeAsync()
    {
        var store = Store();
        await _conversations.CreateAsync(new Conversation { ConversationId = Conversation, AgentId = AgentId.From("parent") });
        var session = await store.GetOrCreateAsync(Parent, AgentId.From("parent"));
        session.ConversationId = Conversation;
        await store.SaveAsync(session);
        await store.SaveSubAgentSessionAsync(new SubAgentInfo
        {
            SubAgentId = "retained-run", ParentSessionId = Parent, ChildSessionId = SessionId.From("child"),
            ParentAgentId = "parent", ChildAgentId = "child",
            ParentConversationId = Conversation, Task = "task", Status = SubAgentStatus.Completed,
            StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow, ResultSummary = "retained-result"
        });
        return store;
    }

    private static SessionEntry Result(string call) => new()
    {
        Role = MessageRole.Tool, Kind = MessageKind.ToolResult, ToolName = "manage_subagent", ToolCallId = call,
        ToolArgs = "{\"action\":\"wait\",\"subAgentId\":\"retained-run\"}", Content = "retained-result"
    };

    [Fact]
    public async Task Consume_ColdSameCallRetry_RetainsPayloadAndOneOriginalToolRow()
    {
        var store = await ArrangeAsync();
        await store.ConsumeSubAgentResultAsync("retained-run", Parent, Conversation, Result("call"));
        var cold = Store();
        (await cold.ConsumeSubAgentResultAsync("retained-run", Parent, Conversation, Result("call"))).ShouldBe("retained-result");
        (await cold.ConsumeSubAgentResultAsync("retained-run", Parent, Conversation, Result("other"))).ShouldBeNull();
        var parent = await cold.GetAsync(Parent);
        parent.ShouldNotBeNull();
        parent.GetHistorySnapshot().Count(e => e.Kind == MessageKind.ToolResult && e.ToolCallId == "call").ShouldBe(1);
        // The later ToolEnd/final aggregate save must not duplicate the tool-owned durable row.
        parent.AddEntry(Result("call"));
        await cold.SaveAsync(parent);
        var reloaded = await Store().GetAsync(Parent);
        reloaded.ShouldNotBeNull();
        reloaded.GetHistorySnapshot().Count(e => e.Kind == MessageKind.ToolResult && e.ToolCallId == "call").ShouldBe(1);
    }

    [Fact]
    public async Task Consume_TwoWorkers_OneWinnerAndOneDurableResult()
    {
        var store = await ArrangeAsync();
        var results = await Task.WhenAll(
            store.ConsumeSubAgentResultAsync("retained-run", Parent, Conversation, Result("one")),
            Store().ConsumeSubAgentResultAsync("retained-run", Parent, Conversation, Result("two")));
        results.Count(r => r == "retained-result").ShouldBe(1);
        var parent = await Store().GetAsync(Parent);
        parent.ShouldNotBeNull();
        parent.GetHistorySnapshot().Count(e => e.Kind == MessageKind.ToolResult).ShouldBe(1);
    }

    [Fact]
    public async Task Consume_CancelBeforeCommit_LeavesNoReceiptOrToolResult()
    {
        var store = await ArrangeAsync();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() =>
            store.ConsumeSubAgentResultAsync("retained-run", Parent, Conversation, Result("cancelled"), cancelled.Token));
        (await Store().ConsumeSubAgentResultAsync("retained-run", Parent, Conversation, Result("retry"))).ShouldBe("retained-result");
    }

    [Fact]
    public async Task Consume_ReceiptWriteFault_RollsBackOriginalToolResultAndCanRetry()
    {
        var store = await ArrangeAsync();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_receipt BEFORE UPDATE OF consumed_tool_call_id ON sub_agent_sessions BEGIN SELECT RAISE(ABORT, 'receipt-write-fault'); END;";
        await command.ExecuteNonQueryAsync();
        await Should.ThrowAsync<SqliteException>(() => store.ConsumeSubAgentResultAsync("retained-run", Parent, Conversation, Result("fault")));
        var parent = await Store().GetAsync(Parent);
        parent.ShouldNotBeNull();
        parent.GetHistorySnapshot().ShouldNotContain(e => e.ToolCallId == "fault");
        command.CommandText = "DROP TRIGGER reject_receipt";
        await command.ExecuteNonQueryAsync();
        (await Store().ConsumeSubAgentResultAsync("retained-run", Parent, Conversation, Result("retry"))).ShouldBe("retained-result");
    }

    [Fact]
    public async Task Consume_CommitThenCancellation_RetainsColdEvidence()
    {
        var store = await ArrangeAsync();
        using var cancellation = new CancellationTokenSource();
        await store.ConsumeSubAgentResultAsync("retained-run", Parent, Conversation, Result("committed"), cancellation.Token);
        cancellation.Cancel();
        (await Store().ConsumeSubAgentResultAsync("retained-run", Parent, Conversation, Result("committed"))).ShouldBe("retained-result");
        (await Store().ConsumeSubAgentResultAsync("retained-run", Parent, Conversation, Result("new"))).ShouldBeNull();
    }

    [Fact]
    public async Task Consume_ReboundParent_RefusesWithoutConsumption()
    {
        var store = await ArrangeAsync();
        await Should.ThrowAsync<InvalidOperationException>(() =>
            store.ConsumeSubAgentResultAsync("retained-run", Parent, ConversationId.From("wrong"), Result("wrong")));
        (await store.ConsumeSubAgentResultAsync("retained-run", Parent, Conversation, Result("right"))).ShouldBe("retained-result");
    }

    [Fact]
    public async Task Consume_CancelInsideTransaction_RollsBackBothReceiptAndOriginalResult()
    {
        var store = await ArrangeAsync();
        using var cancellation = new CancellationTokenSource();
        store.BeforeSubAgentReceiptCommitAsync = _ =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        };
        await Should.ThrowAsync<OperationCanceledException>(() =>
            store.ConsumeSubAgentResultAsync("retained-run", Parent, Conversation, Result("in-transaction"), cancellation.Token));
        var parent = await Store().GetAsync(Parent);
        parent.ShouldNotBeNull();
        parent.GetHistorySnapshot().ShouldNotContain(e => e.ToolCallId == "in-transaction");
        (await Store().ConsumeSubAgentResultAsync("retained-run", Parent, Conversation, Result("after-cancel"))).ShouldBe("retained-result");
    }

    [Fact]
    public async Task Consume_CommitThenCancelledReturn_LeavesDurableIdempotentEvidence()
    {
        var store = await ArrangeAsync();
        using var cancellation = new CancellationTokenSource();
        store.AfterSubAgentReceiptCommitAsync = () =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        await Should.ThrowAsync<OperationCanceledException>(() =>
            store.ConsumeSubAgentResultAsync("retained-run", Parent, Conversation, Result("commit-cancel"), cancellation.Token));
        (await Store().ConsumeSubAgentResultAsync("retained-run", Parent, Conversation, Result("commit-cancel"))).ShouldBe("retained-result");
        (await Store().ConsumeSubAgentResultAsync("retained-run", Parent, Conversation, Result("new-consumer"))).ShouldBeNull();
        var parent = await Store().GetAsync(Parent);
        parent.ShouldNotBeNull();
        parent.GetHistorySnapshot().ShouldContain(e => e.ToolCallId == "commit-cancel" && e.Kind == MessageKind.ToolResult);
    }

    [Theory]
    [InlineData(false, "old admission error", true)]
    [InlineData(true, "old admission error", true)]
    [InlineData(true, "different successful result", false)]
    [InlineData(true, "retained-result", true)]
    public async Task Consume_ExistingCollision_RefusesReceiptAndPreservesOriginalResult(
        bool legacyKey, string originalContent, bool isError)
    {
        var store = await ArrangeAsync();
        var parent = (await store.GetAsync(Parent)).ShouldNotBeNull();
        parent.AddEntry(Result("collision") with { Content = originalContent, ToolIsError = isError });
        await store.SaveAsync(parent);

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$parent", Parent.Value);
        command.Parameters.AddWithValue("$key", legacyKey ? Guid.NewGuid().ToString("N") : "tool-result:collision");
        // Simulate pre-dedup history, including normalized payload-only storage.
        command.CommandText = "UPDATE session_history SET persistence_key=$key, content=NULL WHERE session_id=$parent AND tool_call_id='collision'";
        (await command.ExecuteNonQueryAsync()).ShouldBe(1);
        command.CommandText = "SELECT id FROM session_history WHERE session_id=$parent AND tool_call_id='collision'";
        var originalId = await command.ExecuteScalarAsync();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            Store().ConsumeSubAgentResultAsync("retained-run", Parent, Conversation, Result("collision")));

        command.CommandText = "SELECT id, persistence_key, content FROM session_history WHERE session_id=$parent AND tool_call_id='collision'";
        await using (var reader = await command.ExecuteReaderAsync())
        {
            (await reader.ReadAsync()).ShouldBeTrue();
            reader.GetInt64(0).ShouldBe(originalId);
            reader.GetString(1).ShouldBe(command.Parameters["$key"].Value);
            reader.IsDBNull(2).ShouldBeTrue();
            (await reader.ReadAsync()).ShouldBeFalse();
        }
        command.CommandText = "SELECT result_content, is_error FROM tool_invocations WHERE session_id=$parent AND tool_call_id='collision'";
        await using (var reader = await command.ExecuteReaderAsync())
        {
            (await reader.ReadAsync()).ShouldBeTrue();
            reader.GetString(0).ShouldBe(originalContent);
            reader.GetInt64(1).ShouldBe(isError ? 1L : 0L);
        }
        command.CommandText = "SELECT consumed_tool_call_id FROM sub_agent_sessions WHERE id='retained-run'";
        (await command.ExecuteScalarAsync()).ShouldBe(DBNull.Value);
        var coldParent = (await Store().GetAsync(Parent)).ShouldNotBeNull();
        var original = coldParent.GetHistorySnapshot().Single(e => e.Kind == MessageKind.ToolResult
            && e.ToolCallId == "collision");
        original.Content.ShouldBe(originalContent);
        original.ToolIsError.ShouldBe(isError);
        (await Store().ConsumeSubAgentResultAsync("retained-run", Parent, Conversation, Result("fresh")))
            .ShouldBe("retained-result");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Consume_LegacyMatchingResult_ConvergesOnlyWithOneOriginalRow(int originalRows)
    {
        var store = await ArrangeAsync();
        var parent = (await store.GetAsync(Parent)).ShouldNotBeNull();
        parent.AddEntry(Result("matching"));
        await store.SaveAsync(parent);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$parent", Parent.Value);
        var originalKey = Guid.NewGuid().ToString("N");
        command.Parameters.AddWithValue("$key", originalKey);
        command.CommandText = "UPDATE session_history SET persistence_key=$key, content=NULL WHERE session_id=$parent AND tool_call_id='matching'";
        (await command.ExecuteNonQueryAsync()).ShouldBe(1);
        if (originalRows == 2)
        {
            command.CommandText = """
                INSERT INTO session_history (session_id, role, timestamp, message_kind, tool_call_id, tool_invocation_id, persistence_key, tool_is_error)
                SELECT session_id, role, timestamp, message_kind, tool_call_id, tool_invocation_id, 'second-legacy-key', tool_is_error
                FROM session_history WHERE session_id=$parent AND tool_call_id='matching'
                """;
            (await command.ExecuteNonQueryAsync()).ShouldBe(1);
        }
        if (originalRows == 1)
        {
            (await Store().ConsumeSubAgentResultAsync("retained-run", Parent, Conversation, Result("matching")))
                .ShouldBe("retained-result");
            (await Store().ConsumeSubAgentResultAsync("retained-run", Parent, Conversation, Result("matching")))
                .ShouldBe("retained-result");
        }
        else
        {
            await Should.ThrowAsync<InvalidOperationException>(() =>
                Store().ConsumeSubAgentResultAsync("retained-run", Parent, Conversation, Result("matching")));
        }
        command.CommandText = "SELECT COUNT(*) FROM session_history WHERE session_id=$parent AND tool_call_id='matching'";
        (await command.ExecuteScalarAsync()).ShouldBe((long)originalRows);
        command.CommandText = "SELECT COUNT(*) FROM session_history WHERE session_id=$parent AND persistence_key=$key AND content IS NULL";
        (await command.ExecuteScalarAsync()).ShouldBe(1L);
        command.CommandText = "SELECT consumed_tool_call_id FROM sub_agent_sessions WHERE id='retained-run'";
        (await command.ExecuteScalarAsync()).ShouldBe(originalRows == 1 ? "matching" : DBNull.Value);
        if (originalRows == 2)
            (await Store().ConsumeSubAgentResultAsync("retained-run", Parent, Conversation, Result("fresh")))
                .ShouldBe("retained-result");
    }

    [Fact]
    public async Task Consume_RedactsBeforeCommit_ColdReceiptAndParentContainOnlySafePayload()
    {
        await ArrangeAsync();
        var redactor = new Moq.Mock<BotNexus.Gateway.Abstractions.Security.ISecretRedactor>();
        redactor.Setup(r => r.Redact(Moq.It.IsAny<string>()))
            .Returns<string>(text => text.Replace("private-marker", "[REDACTED]", StringComparison.Ordinal));
        var store = new SqliteSessionStore(ConnectionString, NullLogger<SqliteSessionStore>.Instance,
            _conversations, redactor.Object);
        var proposed = Result("safe") with { Content = "bounded private-marker result" };

        (await store.ConsumeSubAgentResultAsync("retained-run", Parent, Conversation, proposed))
            .ShouldBe("bounded [REDACTED] result");
        (await Store().ConsumeSubAgentResultAsync("retained-run", Parent, Conversation, proposed))
            .ShouldBe("bounded [REDACTED] result");
        var parent = (await Store().GetAsync(Parent)).ShouldNotBeNull();
        parent.GetHistorySnapshot().ShouldNotContain(e => e.Content.Contains("private-marker", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        BotNexus.Testing.SqlitePoolCleanup.ClearPoolFor(_path);
        foreach (var path in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(path)) File.Delete(path);
    }
}
