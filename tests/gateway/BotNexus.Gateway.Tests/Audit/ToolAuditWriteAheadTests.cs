using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Audit;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Security;
using BotNexus.Gateway.Sessions;
using BotNexus.Domain.World;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Audit;

/// <summary>
/// #2615: the tool-audit write-ahead must FAIL CLOSED. The prior slices (#2113, #2613, #2614) made
/// the audit data available and gave it one shape; this suite pins the actual security guarantee:
/// a side-effecting tool cannot execute unless its invocation was durably recorded first, and a run
/// that dies after tool-start leaves an explicitly incomplete record rather than silence.
/// </summary>
public sealed class ToolAuditWriteAheadTests
{
    // ---------- AC1: a start record is persisted before invocation, for every executed call ----------

    [Fact]
    public async Task PersistStartAsync_WritesAStartRowThroughTheSharedSink()
    {
        var session = Session("s1");
        var store = StoreFor(session);

        await Create(store.Object, "s1").PersistStartAsync("call-1", "read", Args("path", "a.txt"), default);

        var row = session.GetHistorySnapshot().ShouldHaveSingleItem();
        row.Role.ShouldBe(MessageRole.Tool);
        // The row must be rendered by the ONE #2614 sink, not hand-rolled here: the typed
        // ToolStart kind is what the sink stamps and what a hand-rolled row historically lacked.
        row.Kind.ShouldBe(MessageKind.ToolStart);
        row.IsToolStartRow().ShouldBeTrue();
        row.ToolCallId.ShouldBe("call-1");
        row.ToolName.ShouldBe("read");
        row.ToolArgs.ShouldNotBeNull().ShouldContain("a.txt");
    }

    [Fact]
    public async Task PersistStartAsync_AppliesTheWriteAheadToNonSubAgentToolsToo()
    {
        // The #2113 slice only wrote ahead for sub-agents, so a top-level agent's tool calls left
        // no trace at all until the tool returned. AC1 says "every executed tool call".
        var session = Session("top-level");
        var store = StoreFor(session);

        await Create(store.Object, "top-level").PersistStartAsync("call-9", "web_fetch", Args("url", "https://x"), default);

        session.GetHistorySnapshot().ShouldHaveSingleItem().ToolCallId.ShouldBe("call-9");
    }

    [Fact]
    public async Task PersistStartAsync_DoesNotReleaseTheCallerUntilTheRecordIsDurable()
    {
        // "Write-ahead" is a happens-before claim, not a fire-and-forget one: if the caller is
        // released while SaveAsync is still in flight, the tool can execute against a record that
        // is not yet durable, which is precisely the guarantee #2615 exists to establish.
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = Session("s1");
        var store = StoreFor(session);
        store.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                saveStarted.SetResult();
                await releaseSave.Task;
            });

        var persistence = Create(store.Object, "s1").PersistStartAsync("call-1", "exec", Args("command", "git status"), default);
        await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        persistence.IsCompleted.ShouldBeFalse();

        releaseSave.SetResult();
        await persistence;
    }

    [Fact]
    public async Task PersistStartAsync_RedactsSecretsBeforeTheyReachTheTranscript()
    {
        var session = Session("s1");
        var store = StoreFor(session);
        var token = "ghp_abcdefghijklmnopqrstuvwxyzABCDEFGHIJ";

        await Create(store.Object, "s1").PersistStartAsync("call-1", "exec", Args("command", $"echo {token}"), default);

        var row = session.GetHistorySnapshot().ShouldHaveSingleItem();
        row.ToolArgs.ShouldNotBeNull().ShouldContain("[REDACTED]");
        row.ToolArgs!.ShouldNotContain(token);
    }

    [Fact]
    public async Task PersistStartAsync_EmptyArgumentsRemainKnownEmptyJson()
    {
        var session = Session("s1");
        var store = StoreFor(session);

        await Create(store.Object, "s1").PersistStartAsync("call-1", "process", new Dictionary<string, object?>(), default);

        session.GetHistorySnapshot().ShouldHaveSingleItem().ToolArgs.ShouldBe("{}");
    }

    // ---------- AC2: a persistence failure BLOCKS a side-effecting tool before invocation ----------

    [Theory]
    [InlineData("exec")]
    [InlineData("shell")]
    [InlineData("process")]
    public async Task PersistStartAsync_WriteFailure_BlocksSideEffectingToolBeforeInvocation(string toolName)
    {
        var session = Session("s1");
        var store = StoreFor(session);
        store.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));

        var error = await Should.ThrowAsync<InvalidOperationException>(
            () => Create(store.Object, "s1").PersistStartAsync("call-1", toolName, Args("command", "rm -rf /"), default));

        error.Message.ShouldContain("blocked");
        error.Message.ShouldContain(toolName);
    }

    [Theory]
    [InlineData("exec")]
    [InlineData("shell")]
    [InlineData("process")]
    public async Task PersistStartAsync_MissingStore_BlocksSideEffectingToolBeforeInvocation(string toolName)
    {
        var error = await Should.ThrowAsync<InvalidOperationException>(
            () => Create(null, "s1").PersistStartAsync("call-1", toolName, Args("command", "danger"), default));

        error.Message.ShouldContain("blocked");
    }

    [Fact]
    public async Task PersistStartAsync_WriteFailure_DoesNotBlockAReadOnlyTool()
    {
        // Fail-closed is deliberately scoped to side-effecting tools. Blocking a read on an audit
        // outage converts a durability incident into a total agent outage; the issue asks for the
        // side-effecting case, and widening it silently would be a different, unreviewed decision.
        var session = Session("s1");
        var store = StoreFor(session);
        store.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));

        await Should.NotThrowAsync(
            () => Create(store.Object, "s1").PersistStartAsync("call-1", "read", Args("path", "a.txt"), default));
    }

    // ---------- AC3/AC4: interrupted invocations leave an EXPLICIT missing-completion record ----------

    [Fact]
    public async Task RecordInterruptedAsync_Cancellation_LeavesAnExplicitIncompleteRow()
    {
        var session = Session("s1");
        var store = StoreFor(session);
        var writeAhead = Create(store.Object, "s1");

        await writeAhead.PersistStartAsync("call-1", "exec", Args("command", "deploy prod"), default);
        // The turn is cancelled after the tool started and before it returned.
        await writeAhead.RecordInterruptedAsync(CancellationToken.None);

        var rows = session.GetHistorySnapshot();
        rows.Count.ShouldBe(2);
        var incomplete = rows[1];
        incomplete.ToolCallId.ShouldBe("call-1");
        incomplete.ToolIsError.ShouldBeTrue();
        incomplete.Kind.ShouldBe(MessageKind.ToolResult);
        incomplete.Content.ShouldContain("did not complete");
        // AC3: the record stays forensically readable - the arguments survive the interruption.
        incomplete.ToolArgs.ShouldNotBeNull().ShouldContain("deploy prod");
    }

    [Fact]
    public async Task RecordInterruptedAsync_Timeout_LeavesTheSameExplicitIncompleteRow()
    {
        // AC4 asserts the timeout case SEPARATELY from the cancellation case: a timeout reaches the
        // write-ahead through a different token, and a fix that only handled turn cancellation
        // would leave a timed-out tool silently unrecorded.
        var session = Session("s1");
        var store = StoreFor(session);
        var writeAhead = Create(store.Object, "s1");
        using var timedOut = new CancellationTokenSource();
        await timedOut.CancelAsync();

        await writeAhead.PersistStartAsync("call-t", "shell", Args("command", "sleep 999"), default);
        await writeAhead.RecordInterruptedAsync(timedOut.Token);

        var incomplete = session.GetHistorySnapshot().Last();
        incomplete.ToolCallId.ShouldBe("call-t");
        incomplete.ToolIsError.ShouldBeTrue();
        incomplete.Content.ShouldContain("did not complete");
        incomplete.ToolArgs.ShouldNotBeNull().ShouldContain("sleep 999");
    }

    [Fact]
    public async Task RecordInterruptedAsync_AfterTheToolCompleted_WritesNothing()
    {
        // Non-vacuity anchor for the two tests above: without it, a write-ahead that emitted an
        // incomplete row for EVERY call would satisfy both of them for the wrong reason.
        var session = Session("s1");
        var store = StoreFor(session);
        var writeAhead = Create(store.Object, "s1");

        await writeAhead.PersistStartAsync("call-1", "exec", Args("command", "ls"), default);
        writeAhead.RecordCompleted("call-1");
        await writeAhead.RecordInterruptedAsync(CancellationToken.None);

        session.GetHistorySnapshot().Count.ShouldBe(1);
        session.GetHistorySnapshot()[0].Kind.ShouldBe(MessageKind.ToolStart);
    }

    [Fact]
    public async Task RecordInterruptedAsync_IsIdempotent()
    {
        var session = Session("s1");
        var store = StoreFor(session);
        var writeAhead = Create(store.Object, "s1");

        await writeAhead.PersistStartAsync("call-1", "exec", Args("command", "ls"), default);
        await writeAhead.RecordInterruptedAsync(CancellationToken.None);
        await writeAhead.RecordInterruptedAsync(CancellationToken.None);

        session.GetHistorySnapshot().Count(e => e.ToolIsError).ShouldBe(1);
    }

    [Fact]
    public async Task RecordInterruptedAsync_PersistenceFailure_DoesNotThrow()
    {
        // The interruption path runs while the turn is already failing. Throwing here would mask
        // the original cancellation with an audit error - fail-closed governs execution, not
        // post-mortem bookkeeping.
        var session = Session("s1");
        var store = StoreFor(session);
        var writeAhead = Create(store.Object, "s1");
        await writeAhead.PersistStartAsync("call-1", "exec", Args("command", "ls"), default);
        store.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));

        await Should.NotThrowAsync(() => writeAhead.RecordInterruptedAsync(CancellationToken.None));
    }

    // ---------- durability end to end ----------

    [Fact]
    public async Task PersistStartAsync_RoundTripsThroughSqliteBeforeReturning()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "ToolAuditWriteAheadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var connectionString = $"Data Source={Path.Combine(directory, "sessions.db")};Pooling=False";
            var conversations = new InMemoryConversationStore();
            await conversations.CreateAsync(new Conversation
            {
                ConversationId = ConversationId.From("conv"),
                AgentId = AgentId.From("agent-a")
            });
            var store = new SqliteSessionStore(connectionString, NullLogger<SqliteSessionStore>.Instance, conversations);
            var session = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));
            session.ConversationId = ConversationId.From("conv");
            await store.SaveAsync(session);

            await Create(store, "s1").PersistStartAsync("call-sqlite", "exec", Args("command", "git status"), default);

            var reloaded = await new SqliteSessionStore(connectionString, NullLogger<SqliteSessionStore>.Instance, conversations)
                .GetAsync(SessionId.From("s1"));
            var start = reloaded.ShouldNotBeNull().GetHistorySnapshot().ShouldHaveSingleItem();
            start.ToolCallId.ShouldBe("call-sqlite");
            start.ToolName.ShouldBe("exec");
            start.ToolArgs.ShouldNotBeNull().ShouldContain("git status");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PersistStartAsync_ParallelCallsRetainTheirOwnRedactedArguments()
    {
        var session = Session("s1");
        var store = StoreFor(session);
        var writeAhead = Create(store.Object, "s1");
        var token = "ghp_abcdefghijklmnopqrstuvwxyzABCDEFGHIJ";

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            writeAhead.PersistStartAsync($"call-{i}", "exec", Args("command", $"echo {i} {token}"), default)));

        var starts = session.GetHistorySnapshot().OrderBy(e => e.ToolCallId).ToArray();
        starts.Length.ShouldBe(8);
        starts.Select(e => e.ToolCallId).ShouldBeUnique();
        starts.ShouldAllBe(e => e.ToolArgs!.Contains("[REDACTED]", StringComparison.Ordinal));
        starts.ShouldAllBe(e => !e.ToolArgs!.Contains(token, StringComparison.Ordinal));
        foreach (var i in Enumerable.Range(0, 8))
            starts.ShouldContain(e => e.ToolCallId == $"call-{i}" && e.ToolArgs!.Contains($"echo {i}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PersistStartAsync_KeepsSessionsIsolated()
    {
        var other = Session("other");
        var mine = Session("s1");
        var store = new Mock<ISessionStore>();
        store.Setup(s => s.GetAsync(SessionId.From("s1"), It.IsAny<CancellationToken>())).ReturnsAsync(mine);
        store.Setup(s => s.GetAsync(SessionId.From("other"), It.IsAny<CancellationToken>())).ReturnsAsync(other);
        store.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await Create(store.Object, "s1").PersistStartAsync("call-mine", "exec", Args("command", "mine"), default);

        other.GetHistorySnapshot().ShouldBeEmpty();
        mine.GetHistorySnapshot().ShouldHaveSingleItem().ToolCallId.ShouldBe("call-mine");
    }

    private static ToolAuditWriteAhead Create(ISessionStore? store, string sessionId) =>
        new(store, DefaultToolAuditSink.Instance, new SecretRedactor(), SessionId.From(sessionId), NullLogger.Instance);

    private static Mock<ISessionStore> StoreFor(GatewaySession session)
    {
        var store = new Mock<ISessionStore>();
        store.Setup(s => s.GetAsync(session.SessionId, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        store.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return store;
    }

    private static GatewaySession Session(string id) => new()
    {
        SessionId = SessionId.From(id),
        AgentId = AgentId.From($"{id}-agent"),
        ConversationId = ConversationId.From("conv")
    };

    private static IReadOnlyDictionary<string, object?> Args(string name, string value) =>
        new Dictionary<string, object?> { [name] = value };
}
