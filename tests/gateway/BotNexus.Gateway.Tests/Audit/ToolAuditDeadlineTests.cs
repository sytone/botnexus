using System.Collections.Concurrent;
using System.Diagnostics;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Audit;
using BotNexus.Gateway.Diagnostics;
using BotNexus.Gateway.Security;
using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Audit;

public sealed class ToolAuditDeadlineTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task PersistStartAsync_StoreIgnoresCancellation_ReadReturnsWithinHardDeadline()
    {
        var store = HangingStore();
        var audit = Create(store.Object);
        var stopwatch = Stopwatch.StartNew();

        await audit.PersistStartAsync("call-read", "read", Args("path", "secret-value"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
        store.Verify(s => s.AppendEntriesAsync(
            SessionId.From("session-a"),
            It.Is<IReadOnlyList<SessionEntry>>(entries => entries.Count == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PersistStartAsync_StoreBlocksBeforeReturningTask_ReadStillReturnsWithinHardDeadline()
    {
        var release = new ManualResetEventSlim(false);
        var store = new Mock<ISessionStore>();
        store.Setup(s => s.AppendEntriesAsync(
                It.IsAny<SessionId>(),
                It.IsAny<IReadOnlyList<SessionEntry>>(),
                It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                release.Wait();
                return Task.FromResult(new SessionAppendMutationResult(SessionMutationOutcome.Applied, 1));
            });

        try
        {
            await Create(store.Object).PersistStartAsync(
                    "call-sync-block", "read", Args("path", "file.txt"), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            release.Set();
            release.Dispose();
        }
    }

    [Theory]
    [InlineData("exec")]
    [InlineData("shell")]
    [InlineData("process")]
    public async Task PersistStartAsync_StoreIgnoresCancellation_SideEffectingToolAwaitsActualCompletion(string toolName)
    {
        var completion = new TaskCompletionSource<SessionAppendMutationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Mock<ISessionStore>();
        store.Setup(s => s.AppendEntriesAsync(
                It.IsAny<SessionId>(),
                It.IsAny<IReadOnlyList<SessionEntry>>(),
                It.IsAny<CancellationToken>()))
            .Returns(completion.Task);
        var persistence = Create(store.Object).PersistStartAsync(
            "call-side-effect", toolName, Args("command", "secret-value"), CancellationToken.None);

        using (var probe = new CancellationTokenSource(Deadline * 2))
        {
            await Should.ThrowAsync<OperationCanceledException>(
                () => persistence.WaitAsync(probe.Token));
        }
        persistence.IsCompleted.ShouldBeFalse("a side-effecting call cannot abandon a write that may still become durable");

        completion.SetException(new IOException("database is locked"));
        var error = await Should.ThrowAsync<InvalidOperationException>(() => persistence);
        error.Message.ShouldContain("audit unavailable", Case.Insensitive);
        error.Message.ShouldContain("lock", Case.Insensitive);
        error.Message.ShouldContain(toolName);
    }

    [Theory]
    [InlineData("exec")]
    [InlineData("shell")]
    [InlineData("process")]
    public async Task PersistStartAsync_LockedSqlite_SideEffectingToolFailsWithinDeadlineWithoutLateAuditRow(
        string toolName)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, nameof(ToolAuditDeadlineTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var databasePath = Path.Combine(directory, "sessions.db");
            var connectionString = $"Data Source={databasePath};Pooling=False";
            var conversations = new InMemoryConversationStore();
            await conversations.CreateAsync(new Conversation
            {
                ConversationId = ConversationId.From("conv"),
                AgentId = AgentId.From("agent-a")
            });
            var store = new SqliteSessionStore(connectionString, NullLogger<SqliteSessionStore>.Instance, conversations);
            var session = await store.GetOrCreateAsync(SessionId.From("session-a"), AgentId.From("agent-a"));
            session.ConversationId = ConversationId.From("conv");
            await store.SaveAsync(session);

            await using var lockConnection = new SqliteConnection(connectionString);
            await lockConnection.OpenAsync();
            await using var lockCommand = lockConnection.CreateCommand();
            lockCommand.CommandText = "BEGIN EXCLUSIVE;";
            await lockCommand.ExecuteNonQueryAsync();

            var stopwatch = Stopwatch.StartNew();
            var error = await Should.ThrowAsync<InvalidOperationException>(() =>
                Create(store).PersistStartAsync(
                    $"call-{toolName}", toolName, Args("command", "danger"), CancellationToken.None));
            stopwatch.Stop();

            stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
            error.Message.ShouldContain("audit unavailable", Case.Insensitive);
            error.Message.ShouldContain("lock", Case.Insensitive);

            lockCommand.CommandText = "ROLLBACK;";
            await lockCommand.ExecuteNonQueryAsync();

            var reloaded = await new SqliteSessionStore(
                    connectionString, NullLogger<SqliteSessionStore>.Instance, conversations)
                .GetAsync(SessionId.From("session-a"));
            reloaded.ShouldNotBeNull().GetHistorySnapshot()
                .ShouldNotContain(entry => entry.ToolCallId == $"call-{toolName}");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PersistStartAsync_BoundedStore_ReadOnlyToolUsesDefinitiveDeadlineAndRemainsBestEffort()
    {
        var store = new Mock<ISessionStore>();
        var bounded = store.As<IBoundedSessionAppendStore>();
        bounded.Setup(s => s.AppendEntriesWithinAsync(
                It.IsAny<SessionId>(), It.IsAny<IReadOnlyList<SessionEntry>>(), It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BoundedSessionAppendException(
                BoundedSessionAppendFailureReason.Deadline,
                "bounded append expired"));

        await Should.NotThrowAsync(() => Create(store.Object).PersistStartAsync(
            "call-read-bounded", "read", Args("path", "file.txt"), CancellationToken.None));

        bounded.Verify(s => s.AppendEntriesWithinAsync(
            SessionId.From("session-a"),
            It.Is<IReadOnlyList<SessionEntry>>(entries => entries.Count == 1),
            Deadline,
            It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.AppendEntriesAsync(
            It.IsAny<SessionId>(), It.IsAny<IReadOnlyList<SessionEntry>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PersistStartAsync_EmitsCorrelatedStageTelemetryWithoutArguments()
    {
        var stopped = new ConcurrentQueue<System.Diagnostics.Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == GatewayDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName.StartsWith("audit.", StringComparison.Ordinal)
                    && Equals(activity.GetTagItem("botnexus.tool.call_id"), "call-telemetry"))
                    stopped.Enqueue(activity);
            }
        };
        ActivitySource.AddActivityListener(listener);

        var store = new Mock<ISessionStore>();
        store.Setup(s => s.AppendEntriesAsync(
                SessionId.From("session-a"),
                It.IsAny<IReadOnlyList<SessionEntry>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionAppendMutationResult(SessionMutationOutcome.Applied, 1));

        await Create(store.Object).PersistStartAsync(
            "call-telemetry", "read", Args("path", "do-not-emit-this-argument"), CancellationToken.None);

        var stages = stopped.ToArray();
        stages.Select(activity => activity.OperationName).ShouldBe(["audit.serialize", "audit.persist"], ignoreOrder: true);
        stages.ShouldAllBe(activity => Equals(activity.GetTagItem("botnexus.agent.id"), "agent-a"));
        stages.ShouldAllBe(activity => Equals(activity.GetTagItem("botnexus.session.id"), "session-a"));
        stages.ShouldAllBe(activity => Equals(activity.GetTagItem("botnexus.tool.name"), "read"));
        stages.ShouldAllBe(activity => Equals(activity.GetTagItem("botnexus.tool.call_id"), "call-telemetry"));
        stages.ShouldAllBe(activity => Equals(activity.GetTagItem("botnexus.audit.outcome"), "success"));
        string.Join(" ", stages.SelectMany(activity => activity.TagObjects).Select(tag => $"{tag.Key}={tag.Value}"))
            .ShouldNotContain("do-not-emit-this-argument");
    }

    private static ToolAuditWriteAhead Create(ISessionStore store) => new(
        store,
        DefaultToolAuditSink.Instance,
        new SecretRedactor(),
        AgentId.From("agent-a"),
        SessionId.From("session-a"),
        NullLogger.Instance,
        Deadline);

    private static Mock<ISessionStore> HangingStore()
    {
        var never = new TaskCompletionSource<SessionAppendMutationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Mock<ISessionStore>();
        store.Setup(s => s.AppendEntriesAsync(
                It.IsAny<SessionId>(),
                It.IsAny<IReadOnlyList<SessionEntry>>(),
                It.IsAny<CancellationToken>()))
            .Returns(never.Task);
        return store;
    }

    private static IReadOnlyDictionary<string, object?> Args(string name, string value) =>
        new Dictionary<string, object?> { [name] = value };
}
