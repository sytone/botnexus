using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Security;
using BotNexus.Gateway.Sessions;
using BotNexus.Domain.World;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class SubAgentToolWriteAheadTests
{
    [Theory]
    [InlineData("exec")]
    [InlineData("shell")]
    [InlineData("process")]
    public async Task PersistAsync_MissingStore_BlocksProcessCapableTool(string toolName)
    {
        var writeAhead = Create(null, "child");

        Func<Task> act = () => writeAhead.PersistAsync(
            "call-1", toolName, Args("command", "danger"), default);

        var error = await Should.ThrowAsync<InvalidOperationException>(act);
        error.Message.ShouldContain("blocked");
    }

    [Fact]
    public async Task PersistAsync_DoesNotReleaseCallerUntilArgumentsAreDurable()
    {
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = ChildSession("child");
        var store = StoreFor(session);
        store.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                saveStarted.SetResult();
                await releaseSave.Task;
            });
        var writeAhead = Create(store.Object, "child");

        var persistence = writeAhead.PersistAsync("call-1", "exec", Args("command", "git status"), default);
        await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        persistence.IsCompleted.ShouldBeFalse();
        session.History.ShouldHaveSingleItem().ToolArgs.ShouldNotBeNull().ShouldContain("git status");

        releaseSave.SetResult();
        await persistence;
    }


    [Fact]
    public async Task PersistAsync_EmptyArgumentsRemainKnownEmptyJson()
    {
        var session = ChildSession("child");
        var store = StoreFor(session);
        var writeAhead = Create(store.Object, "child");

        await writeAhead.PersistAsync("call-1", "process", new Dictionary<string, object?>(), default);

        session.History.ShouldHaveSingleItem().ToolArgs.ShouldBe("{}");
    }

    [Fact]
    public async Task PersistAsync_ExecStartRoundTripsThroughSqliteBeforeReturning()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "SubAgentToolWriteAheadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var connectionString = $"Data Source={Path.Combine(directory, "sessions.db")};Pooling=False";
            var conversations = new InMemoryConversationStore();
            await conversations.CreateAsync(new Conversation
            {
                ConversationId = ConversationId.From("conv"),
                AgentId = AgentId.From("child-agent")
            });
            var store = new SqliteSessionStore(connectionString, NullLogger<SqliteSessionStore>.Instance, conversations);
            var session = await store.GetOrCreateAsync(SessionId.From("child"), AgentId.From("child-agent"));
            session.ConversationId = ConversationId.From("conv");
            session.SessionType = SessionType.AgentSubAgent;
            await store.SaveAsync(session);

            var writeAhead = Create(store, "child");
            await writeAhead.PersistAsync("call-sqlite", "exec", Args("command", "git status"), default);

            var reloadedStore = new SqliteSessionStore(connectionString, NullLogger<SqliteSessionStore>.Instance, conversations);
            var reloaded = await reloadedStore.GetAsync(SessionId.From("child"));
            var start = reloaded.ShouldNotBeNull().History.ShouldHaveSingleItem();
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
    public async Task PersistAsync_InterruptedAfterReturn_PreservesStartWithoutEnd()
    {
        var session = ChildSession("child");
        var store = StoreFor(session);
        var writeAhead = Create(store.Object, "child");

        await writeAhead.PersistAsync("call-1", "exec", Args("command", "deploy"), default);

        var start = session.History.ShouldHaveSingleItem();
        start.ToolCallId.ShouldBe("call-1");
        start.ToolArgs.ShouldNotBeNull().ShouldContain("deploy");
        start.ToolIsError.ShouldBeFalse();
    }

    [Theory]
    [InlineData("exec")]
    [InlineData("shell")]
    [InlineData("process")]
    public async Task PersistAsync_WriteFailure_BlocksProcessCapableTool(string toolName)
    {
        var session = ChildSession("child");
        var store = StoreFor(session);
        store.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));
        var writeAhead = Create(store.Object, "child");

        Func<Task> act = () => writeAhead.PersistAsync("call-1", toolName, Args("command", "danger"), default);

        var error = await Should.ThrowAsync<InvalidOperationException>(act);
        error.Message.ShouldContain("blocked");
    }

    [Fact]
    public async Task PersistAsync_ParentAndChildHistoriesRemainIsolated()
    {
        var parent = ChildSession("parent");
        var child = ChildSession("child");
        var store = new Mock<ISessionStore>();
        store.Setup(s => s.GetAsync(SessionId.From("child"), It.IsAny<CancellationToken>())).ReturnsAsync(child);
        store.Setup(s => s.GetAsync(SessionId.From("parent"), It.IsAny<CancellationToken>())).ReturnsAsync(parent);
        store.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await Create(store.Object, "child").PersistAsync("call-child", "exec", Args("command", "child-only"), default);

        parent.History.ShouldBeEmpty();
        child.History.ShouldHaveSingleItem().ToolCallId.ShouldBe("call-child");
    }

    [Fact]
    public async Task PersistAsync_ParallelCallsRetainTheirOwnRedactedArguments()
    {
        var session = new GatewaySession(new Session
        {
            SessionId = SessionId.From("child"),
            ConversationId = ConversationId.From("conv")
        }, new SecretRedactor()) { AgentId = AgentId.From("child-agent") };
        var store = StoreFor(session);
        var writeAhead = Create(store.Object, "child");
        var token = "ghp_abcdefghijklmnopqrstuvwxyzABCDEFGHIJ";

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            writeAhead.PersistAsync($"call-{i}", "exec", Args("command", $"echo {i} {token}"), default)));

        var starts = session.GetHistorySnapshot().OrderBy(e => e.ToolCallId).ToArray();
        starts.Length.ShouldBe(8);
        starts.Select(e => e.ToolCallId).ShouldBeUnique();
        starts.ShouldAllBe(e => e.ToolArgs!.Contains("[REDACTED]", StringComparison.Ordinal));
        starts.ShouldAllBe(e => !e.ToolArgs!.Contains(token, StringComparison.Ordinal));
        foreach (var i in Enumerable.Range(0, 8))
            starts.ShouldContain(e => e.ToolCallId == $"call-{i}" && e.ToolArgs!.Contains($"echo {i}", StringComparison.Ordinal));
    }

    private static SubAgentToolWriteAhead Create(ISessionStore? store, string sessionId) =>
        new(store, new SecretRedactor(), SessionId.From(sessionId), NullLogger.Instance);

    private static Mock<ISessionStore> StoreFor(GatewaySession session)
    {
        var store = new Mock<ISessionStore>();
        store.Setup(s => s.GetAsync(session.SessionId, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        store.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return store;
    }

    private static GatewaySession ChildSession(string id) => new()
    {
        SessionId = SessionId.From(id),
        AgentId = AgentId.From($"{id}-agent"),
        ConversationId = ConversationId.From("conv")
    };

    private static IReadOnlyDictionary<string, object?> Args(string name, string value) =>
        new Dictionary<string, object?> { [name] = value };
}
