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
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<SessionAppendMutationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Mock<ISessionStore>();
        store.Setup(s => s.AppendEntriesAsync(
                It.IsAny<SessionId>(),
                It.IsAny<IReadOnlyList<SessionEntry>>(),
                It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                entered.TrySetResult(true);
                return completion.Task;
            });
        // This asynchronous store returns its incomplete task immediately, so inline dispatch
        // establishes entry without involving thread-pool scheduling in the deadline assertion.
        var audit = Create(store.Object, static append => append());
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var persistence = audit.PersistStartAsync("call-read", "read", Args("path", "secret-value"), CancellationToken.None);
            entered.Task.IsCompletedSuccessfully.ShouldBeTrue("inline dispatch must establish store entry before returning its task");
            completion.Task.IsCompleted.ShouldBeFalse();
            await persistence.WaitAsync(TimeSpan.FromSeconds(2));

            stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));

            entered.Task.IsCompletedSuccessfully.ShouldBeTrue();
            completion.Task.IsCompleted.ShouldBeFalse("the read deadline must not await store completion");
            store.Verify(s => s.AppendEntriesAsync(
                SessionId.From("session-a"),
                It.Is<IReadOnlyList<SessionEntry>>(entries => entries.Count == 1),
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            completion.TrySetResult(new SessionAppendMutationResult(SessionMutationOutcome.Applied, 1));
        }
    }

    [Fact]
    public async Task PersistStartAsync_ReadAppendRemainsQueued_ReturnsWithinHardDeadlineBeforeEntryOrCompletion()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<SessionAppendMutationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatched = new TaskCompletionSource<SessionAppendMutationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<Task<SessionAppendMutationResult>>? queuedAppend = null;
        var dispatchCount = 0;
        var store = new Mock<ISessionStore>();
        store.Setup(s => s.AppendEntriesAsync(
                It.IsAny<SessionId>(),
                It.IsAny<IReadOnlyList<SessionEntry>>(),
                It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                entered.TrySetResult(true);
                return completion.Task;
            });
        var audit = Create(store.Object, append =>
        {
            dispatchCount++;
            queuedAppend = append;
            return dispatched.Task;
        });
        var stopwatch = Stopwatch.StartNew();
        Task<SessionAppendMutationResult>? appendTask = null;

        try
        {
            await audit.PersistStartAsync("call-queued", "read", Args("path", "file.txt"), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));

            stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
            dispatchCount.ShouldBe(1);
            entered.Task.IsCompleted.ShouldBeFalse("the append is still in the controlled dispatch queue");
            dispatched.Task.IsCompleted.ShouldBeFalse();
            completion.Task.IsCompleted.ShouldBeFalse();
            store.Verify(s => s.AppendEntriesAsync(
                It.IsAny<SessionId>(), It.IsAny<IReadOnlyList<SessionEntry>>(), It.IsAny<CancellationToken>()), Times.Never);

            // Release dispatch only after the deadline returned; the store still ignores cancellation
            // and remains incomplete. Entry and completion are distinct deterministic boundaries.
            appendTask = queuedAppend.ShouldNotBeNull()();
            entered.Task.IsCompletedSuccessfully.ShouldBeTrue();
            appendTask.IsCompleted.ShouldBeFalse();
            store.Verify(s => s.AppendEntriesAsync(
                SessionId.From("session-a"),
                It.Is<IReadOnlyList<SessionEntry>>(entries => entries.Count == 1 && entries[0].ToolCallId == "call-queued"),
                It.Is<CancellationToken>(token => token.IsCancellationRequested)), Times.Once);
        }
        finally
        {
            completion.TrySetResult(new SessionAppendMutationResult(SessionMutationOutcome.Applied, 1));
            if (appendTask is not null)
                dispatched.TrySetResult(await appendTask);
            else
                dispatched.TrySetResult(new SessionAppendMutationResult(SessionMutationOutcome.Applied, 1));
        }
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
    public async Task PersistStartAsync_LockedSqlite_SideEffectingToolFailsClosedWithoutLateAuditRow(
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

            var error = await Should.ThrowAsync<InvalidOperationException>(() =>
                Create(store).PersistStartAsync(
                    $"call-{toolName}", toolName, Args("command", "danger"), CancellationToken.None));

            // The bounded store returns only after its write attempt has definitively failed.
            // Runner scheduling is not part of the persistence deadline contract.
            var boundedFailure = error.InnerException.ShouldBeOfType<BoundedSessionAppendException>();
            boundedFailure.Reason.ShouldBe(BoundedSessionAppendFailureReason.Locked);
            boundedFailure.InnerException.ShouldBeOfType<SqliteException>().SqliteErrorCode.ShouldBeOneOf(5, 6);
            error.Message.ShouldContain(toolName);
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

    [Theory]
    [InlineData("exec")]
    [InlineData("shell")]
    [InlineData("process")]
    public async Task PersistStartAsync_BoundedStore_SideEffectingToolPassesDeadlineAndAwaitsDefinitiveFailure(
        string toolName)
    {
        var completion = new TaskCompletionSource<SessionAppendMutationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Mock<ISessionStore>();
        var bounded = store.As<IBoundedSessionAppendStore>();
        bounded.Setup(s => s.AppendEntriesWithinAsync(
                SessionId.From("session-a"),
                It.Is<IReadOnlyList<SessionEntry>>(entries => entries.Count == 1 && entries[0].ToolCallId == "call-bounded"),
                Deadline,
                CancellationToken.None))
            .Returns(completion.Task);
        var persistence = Create(store.Object).PersistStartAsync(
            "call-bounded", toolName, Args("command", "danger"), CancellationToken.None);

        bounded.Verify(s => s.AppendEntriesWithinAsync(
            SessionId.From("session-a"),
            It.Is<IReadOnlyList<SessionEntry>>(entries => entries.Count == 1 && entries[0].ToolCallId == "call-bounded"),
            Deadline,
            CancellationToken.None), Times.Once);
        using (var probe = new CancellationTokenSource(Deadline * 2))
        {
            await Should.ThrowAsync<OperationCanceledException>(() => persistence.WaitAsync(probe.Token));
        }
        persistence.IsCompleted.ShouldBeFalse("the audit must await the bounded store's definitive outcome even after its deadline");

        var failure = new BoundedSessionAppendException(BoundedSessionAppendFailureReason.Locked, "database is locked");
        completion.SetException(failure);
        var error = await Should.ThrowAsync<InvalidOperationException>(() => persistence);
        error.InnerException.ShouldBeSameAs(failure);
        error.Message.ShouldContain("audit unavailable", Case.Insensitive);
        error.Message.ShouldContain(toolName);
        store.Verify(s => s.AppendEntriesAsync(
            It.IsAny<SessionId>(), It.IsAny<IReadOnlyList<SessionEntry>>(), It.IsAny<CancellationToken>()), Times.Never);
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
        // Activity listeners are process-wide; isolate this invocation without serializing tests.
        var toolCallId = $"call-telemetry-{Guid.NewGuid():N}";
        var stopped = new ConcurrentQueue<System.Diagnostics.Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == GatewayDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName.StartsWith("audit.", StringComparison.Ordinal)
                    && Equals(activity.GetTagItem("botnexus.tool.call_id"), toolCallId))
                    stopped.Enqueue(activity);
            }
        };
        ActivitySource.AddActivityListener(listener);

        using var callerCts = new CancellationTokenSource();
        // A successful bounded append avoids making telemetry success depend on the queued fallback.
        // Strict behavior and explicit verification prevent that fallback from silently passing.
        var store = new Mock<ISessionStore>(MockBehavior.Strict);
        var bounded = store.As<IBoundedSessionAppendStore>();
        bounded.Setup(s => s.AppendEntriesWithinAsync(
                SessionId.From("session-a"),
                It.Is<IReadOnlyList<SessionEntry>>(entries => entries.Count == 1),
                Deadline,
                callerCts.Token))
            .ReturnsAsync(new SessionAppendMutationResult(SessionMutationOutcome.Applied, 1));

        await Create(store.Object).PersistStartAsync(
            toolCallId, "read", Args("path", "do-not-emit-this-argument"), callerCts.Token);

        bounded.Verify(s => s.AppendEntriesWithinAsync(
            SessionId.From("session-a"),
            It.Is<IReadOnlyList<SessionEntry>>(entries => entries.Count == 1),
            Deadline,
            callerCts.Token), Times.Once);
        store.Verify(s => s.AppendEntriesAsync(
            It.IsAny<SessionId>(), It.IsAny<IReadOnlyList<SessionEntry>>(), It.IsAny<CancellationToken>()),
            Times.Never);

        var stages = stopped.ToArray();
        stages.Select(activity => activity.OperationName).ShouldBe(["audit.serialize", "audit.persist"], ignoreOrder: true);
        stages.ShouldAllBe(activity => Equals(activity.GetTagItem("botnexus.agent.id"), "agent-a"));
        stages.ShouldAllBe(activity => Equals(activity.GetTagItem("botnexus.session.id"), "session-a"));
        stages.ShouldAllBe(activity => Equals(activity.GetTagItem("botnexus.tool.name"), "read"));
        stages.ShouldAllBe(activity => Equals(activity.GetTagItem("botnexus.tool.call_id"), toolCallId));
        stages.ShouldAllBe(activity => Equals(activity.GetTagItem("botnexus.audit.outcome"), "success"));
        string.Join(" ", stages.SelectMany(activity => activity.TagObjects).Select(tag => $"{tag.Key}={tag.Value}"))
            .ShouldNotContain("do-not-emit-this-argument");
    }

    private static ToolAuditWriteAhead Create(
        ISessionStore store,
        Func<Func<Task<SessionAppendMutationResult>>, Task<SessionAppendMutationResult>>? dispatchReadAppend = null) => new(
        store,
        DefaultToolAuditSink.Instance,
        new SecretRedactor(),
        AgentId.From("agent-a"),
        SessionId.From("session-a"),
        NullLogger.Instance,
        Deadline,
        dispatchReadAppend);

    private static IReadOnlyDictionary<string, object?> Args(string name, string value) =>
        new Dictionary<string, object?> { [name] = value };
}
