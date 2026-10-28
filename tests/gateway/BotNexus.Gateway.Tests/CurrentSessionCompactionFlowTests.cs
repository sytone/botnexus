using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Events;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace BotNexus.Gateway.Tests;

public sealed class CurrentSessionCompactionFlowTests
{
    [Theory]
    [InlineData("success", 200)]
    [InlineData("empty", 200)]
    [InlineData("error", 502)]
    public async Task Compact_RealPipeline_MapsOutcomesAndReleasesReservation(string mode, int status)
    {
        var f = await Fixture.CreateAsync();
        f.Compactor.CompactAsync(Arg.Any<GatewaySession>(), Arg.Any<CompactionOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<CompactionOptions>().PreservedTurns.ShouldBe(0);
                return Task.FromResult(mode switch
                {
                    "success" => Success(f.Session),
                    "empty" => CompactionResult.Skipped(skipReason: CompactionSkipReason.EmptyHistory),
                    _ => throw new IOException("secret provider detail")
                });
            });
        var result = await f.InvokeAsync();
        result.ShouldBeAssignableTo<ObjectResult>().StatusCode.ShouldBe(status);
        System.Text.Json.JsonSerializer.Serialize(((ObjectResult)result).Value).ShouldNotContain("secret");
        if (mode == "success")
        {
            f.Supervisor.GetHandle(f.Handle.AgentId, f.Handle.SessionId).ShouldBeNull();
            f.Handle.TryReserveMaintenance().ShouldBeNull();
        }
        else
        {
            using var lease = f.Supervisor.TryReserveMaintenance(f.Handle);
            lease.ShouldNotBeNull();
        }
        await f.Supervisor.StopAllAsync();
    }

    [Theory]
    [InlineData("seal")]
    [InlineData("reset")]
    [InlineData("rebind")]
    public async Task Compact_BindingChangesDuringSummary_ReturnsConflictWithoutResurrecting(string change)
    {
        var f = await Fixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Compactor.CompactAsync(Arg.Any<GatewaySession>(), Arg.Any<CompactionOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ => { entered.SetResult(); await release.Task; return Success(f.Session); });
        var pending = f.InvokeAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        f.Supervisor.TryReserveMaintenance(f.Handle).ShouldBeNull();
        await Should.ThrowAsync<InvalidOperationException>(() => f.Supervisor.GetOrCreateAsync(f.Handle.AgentId, f.Handle.SessionId));
        if (change == "seal") f.Session.Status = SessionStatus.Sealed;
        else if (change == "rebind") f.Session.ConversationId = ConversationId.Create();
        else
        {
            f.Session.Status = SessionStatus.Sealed;
            await f.Conversations.SaveAsync(f.Conversation with { ActiveSessionId = null });
        }
        release.SetResult();
        (await pending).ShouldBeOfType<ConflictObjectResult>();
        if (change != "rebind") f.Session.Status.ShouldBe(SessionStatus.Sealed);
        await f.Supervisor.StopAllAsync();
    }

    [Fact]
    public async Task Compact_CancellationDuringSummary_EvictsOnlyCapturedHandleAndReleasesKey()
    {
        var f = await Fixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Compactor.CompactAsync(Arg.Any<GatewaySession>(), Arg.Any<CompactionOptions>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
                return Success(f.Session);
            });
        using var cancel = new CancellationTokenSource();
        var pending = f.InvokeAsync(cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancel.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () => await pending);
        f.Supervisor.GetHandle(f.Handle.AgentId, f.Handle.SessionId).ShouldBeNull();
        var replacement = CompactionAdmissionTests.Create().Handle;
        f.Strategy.CreateAsync(Arg.Any<AgentDescriptor>(), Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>()).Returns(replacement);
        (await f.Supervisor.GetOrCreateAsync(f.Handle.AgentId, f.Handle.SessionId)).ShouldBeSameAs(replacement);
        (await f.Supervisor.StopIfCurrentAsync(f.Handle)).ShouldBeFalse();
        f.Supervisor.GetHandle(f.Handle.AgentId, f.Handle.SessionId).ShouldBeSameAs(replacement);
        await f.Supervisor.StopAllAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compact_CoordinatorThrowsAndCapturedDisposalThrows_PreservesRedactedFailureOrOriginalCancellation(bool cancelled)
    {
        var handle = Substitute.For<IAgentHandle>();
        handle.AgentId.Returns(AgentId.From("owner"));
        handle.SessionId.Returns(SessionId.Create());
        var admission = Substitute.For<IDisposable>();
        handle.TryReserveMaintenance().Returns(admission);
        handle.DisposeAsync().Returns(_ => ValueTask.FromException(new IOException("secret disposal detail")));
        var f = await Fixture.CreateAsync(residentHandle: handle);
        using var cancel = new CancellationTokenSource();
        var originalCancellation = new OperationCanceledException("original cancellation", cancel.Token);
        var coordinator = new ThrowingCoordinator(() =>
        {
            if (cancelled) cancel.Cancel();
            return cancelled ? originalCancellation : new IOException("secret coordinator detail");
        });
        // Keep admission and session ownership checks real; only the failing coordinator is replaced.
        var sessions = new InMemorySessionStore();
        await sessions.SaveAsync(f.Session);
        var controller = new ConversationsController(f.Conversations, sessions,
            supervisor: f.Supervisor, compactionCoordinator: coordinator)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

        var pending = controller.CompactAsync(f.Conversation.ConversationId.Value,
            new CompactConversationRequest(f.Session.SessionId.Value), cancel.Token);
        if (cancelled)
        {
            // Await directly: assertion helpers may wrap a cancelled task and replace its
            // captured exception with TaskCanceledException before returning it to the test.
            OperationCanceledException? error = null;
            try { await pending; }
            catch (OperationCanceledException caught) { error = caught; }
            error.ShouldNotBeNull();
            error.ShouldBeSameAs(originalCancellation);
            error.CancellationToken.ShouldBe(cancel.Token);
        }
        else
        {
            var response = (await pending).ShouldBeOfType<ObjectResult>();
            response.StatusCode.ShouldBe(502);
            System.Text.Json.JsonSerializer.Serialize(response.Value).ShouldBe("{\"status\":\"compaction-failed\"}");
        }
        f.Supervisor.GetHandle(handle.AgentId, handle.SessionId).ShouldBeNull();
        handle.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IAgentHandle.DisposeAsync)).ShouldBe(1);
        admission.Received(1).Dispose();
        var replacement = CompactionAdmissionTests.Create(handle.SessionId).Handle;
        f.Strategy.CreateAsync(Arg.Any<AgentDescriptor>(), Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>()).Returns(replacement);
        (await f.Supervisor.GetOrCreateAsync(handle.AgentId, handle.SessionId)).ShouldBeSameAs(replacement);
        (await f.Supervisor.StopIfCurrentAsync(handle)).ShouldBeFalse();
        f.Supervisor.GetHandle(handle.AgentId, handle.SessionId).ShouldBeSameAs(replacement);
        await f.Supervisor.StopAllAsync();
    }

    [Fact]
    public async Task Compact_BusyRun_DoesNotCallCompactor()
    {
        var f = await Fixture.CreateAsync();
        using var lease = f.Handle.TryReserveMaintenance();
        lease.ShouldNotBeNull();
        (await f.InvokeAsync()).ShouldBeOfType<ConflictObjectResult>();
        f.Compactor.ReceivedCalls().ShouldBeEmpty();
        await f.Supervisor.StopAllAsync();
    }

    [Fact]
    public async Task Compact_StopDuringAwait_BlocksReplacementUntilReleaseAndNeverStopsLaterReplacement()
    {
        var f = await Fixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Compactor.CompactAsync(Arg.Any<GatewaySession>(), Arg.Any<CompactionOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ => { entered.SetResult(); await release.Task; return Success(f.Session); });
        var pending = f.InvokeAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await f.Supervisor.StopAsync(f.Handle.AgentId, f.Handle.SessionId);
        await Should.ThrowAsync<InvalidOperationException>(() => f.Supervisor.GetOrCreateAsync(f.Handle.AgentId, f.Handle.SessionId));
        release.SetResult();
        (await pending).ShouldBeOfType<ConflictObjectResult>();
        var replacement = CompactionAdmissionTests.Create().Handle;
        f.Strategy.CreateAsync(Arg.Any<AgentDescriptor>(), Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>()).Returns(replacement);
        (await f.Supervisor.GetOrCreateAsync(f.Handle.AgentId, f.Handle.SessionId)).ShouldBeSameAs(replacement);
        (await f.Supervisor.StopIfCurrentAsync(f.Handle)).ShouldBeFalse();
        f.Supervisor.GetHandle(f.Handle.AgentId, f.Handle.SessionId).ShouldBeSameAs(replacement);
        await f.Supervisor.StopAllAsync();
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("sqlite")]
    [InlineData("file")]
    public async Task Compact_ResetAtFinalSave_DoesNotRewriteSealedTranscript(string backend)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"botnexus-compaction-reset-{Guid.NewGuid():N}.db");
        var conversations = new InMemoryConversationStore();
        var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();
        var fileSystem = new System.IO.Abstractions.TestingHelpers.MockFileSystem();
        var storePath = Path.Combine(Path.GetTempPath(), "compaction-file");
        ISessionStore CreateReader() => backend switch
        {
            "sqlite" => new SqliteSessionStore(connectionString, NullLogger<SqliteSessionStore>.Instance, conversations),
            "file" => new FileSessionStore(storePath, NullLogger<FileSessionStore>.Instance, fileSystem, conversations),
            _ => new InMemorySessionStore()
        };
        ISessionStore durable = CreateReader();
        var boundary = new FinalSaveBoundary(durable);
        var f = await Fixture.CreateAsync(conversations, boundary);
        try
        {
            var original = f.Session.GetHistorySnapshot().ToArray();
            f.Compactor.CompactAsync(Arg.Any<GatewaySession>(), Arg.Any<CompactionOptions>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(Success(call.Arg<GatewaySession>())));
            var pending = f.InvokeAsync();
            await boundary.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            boundary.Candidate.ShouldNotBeNull().GetHistorySnapshot().ShouldContain(e => e.IsCompactionSummary);
            // Compaction has passed its binding precheck and reached its final fenced save.
            // Reset must capture the same cached live aggregate, seal and persist it while
            // that save is paused. This is the production reset service, not a fake seal.
            var options = Substitute.For<IOptionsMonitor<CompactionOptions>>();
            options.CurrentValue.Returns(new CompactionOptions());
            var reset = new DefaultConversationResetService(conversations, boundary, f.Supervisor,
                options, NullLogger<DefaultConversationResetService>.Instance,
                Substitute.For<IConversationEventPublisher>());
            var resetResult = await reset.ResetActiveSessionAsync(f.Conversation.ConversationId, f.Session.SessionId);
            resetResult.Outcome.ShouldBe(ConversationResetOutcome.Reset);
            boundary.Release.TrySetResult();
            (await pending).ShouldBeOfType<ConflictObjectResult>();
            var reader = backend == "memory" ? durable : CreateReader();
            var sealedSession = await reader.GetAsync(f.Session.SessionId);
            sealedSession.ShouldNotBeNull();
            sealedSession.Status.ShouldBe(SessionStatus.Sealed);
            sealedSession.GetHistorySnapshot().ShouldBe(backend == "file"
                ? original.Select(e => e with { PersistenceId = null, PersistenceKey = null }) : original);
            sealedSession.GetHistorySnapshot().ShouldNotContain(e => e.IsHistory || e.IsCompactionSummary);
            f.Session.GetHistorySnapshot().ShouldBe(original);
            (await conversations.GetAsync(f.Conversation.ConversationId)).ShouldNotBeNull().ActiveSessionId.ShouldBeNull();
        }
        finally
        {
            boundary.Release.TrySetResult();
            await f.Supervisor.StopAllAsync();
            foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("sqlite")]
    [InlineData("file")]
    public async Task Compact_Success_PreservesFullHistoryAndOtherConversationForSameAgent(string backend)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"botnexus-compaction-success-{Guid.NewGuid():N}.db");
        var conversations = new InMemoryConversationStore();
        var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();
        var fs = new System.IO.Abstractions.TestingHelpers.MockFileSystem();
        var storePath = Path.Combine(Path.GetTempPath(), $"compaction-success-{Guid.NewGuid():N}");
        ISessionStore CreateStore() => backend switch
        {
            "sqlite" => new SqliteSessionStore(connectionString, NullLogger<SqliteSessionStore>.Instance, conversations),
            "file" => new FileSessionStore(storePath, NullLogger<FileSessionStore>.Instance, fs, conversations),
            _ => new InMemorySessionStore()
        };
        var sessions = CreateStore();
        var f = await Fixture.CreateAsync(conversations, sessions);
        var otherConversation = await conversations.CreateAsync(new Conversation
        {
            ConversationId = ConversationId.Create(), AgentId = f.Handle.AgentId,
            Visibility = ConversationVisibility.UserFacing
        });
        var other = await sessions.GetOrCreateAsync(SessionId.From("other-session"), f.Handle.AgentId);
        other.ConversationId = otherConversation.ConversationId;
        other.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "other conversation untouched" });
        otherConversation.ActiveSessionId = other.SessionId;
        await conversations.SaveAsync(otherConversation);
        await sessions.SaveAsync(other);
        var (_, otherHandle) = CompactionAdmissionTests.Create(other.SessionId);
        f.Strategy.CreateAsync(Arg.Any<AgentDescriptor>(), Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>()).Returns(otherHandle);
        await f.Supervisor.GetOrCreateAsync(other.AgentId, other.SessionId);
        var original = f.Session.GetHistorySnapshot().ToArray();
        var otherOriginal = other.GetHistorySnapshot().ToArray();
        f.Compactor.CompactAsync(Arg.Any<GatewaySession>(), Arg.Any<CompactionOptions>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(Success(call.Arg<GatewaySession>())));
        (await f.InvokeAsync()).ShouldBeOfType<OkObjectResult>();
        var reader = backend == "memory" ? sessions : CreateStore();
        var compacted = (await reader.GetAsync(f.Session.SessionId)).ShouldNotBeNull();
        var history = compacted.GetHistorySnapshot();
        history.Count.ShouldBe(original.Length + 1);
        history.Take(original.Length).ShouldBe(original.Select(e => backend == "file"
            ? e with { IsHistory = true, PersistenceId = null, PersistenceKey = null }
            : e with { IsHistory = true }));
        history.Last().IsCompactionSummary.ShouldBeTrue();
        compacted.Metadata["retained"]?.ToString().ShouldBe("metadata");
        compacted.StreamReplay.NextSequenceId.ShouldBe(f.Session.StreamReplay.NextSequenceId);
        compacted.StreamReplay.GetEventSnapshot().ShouldBe(f.Session.StreamReplay.GetEventSnapshot());
        if (backend != "sqlite") compacted.CallerId.ShouldBe("caller");
        f.Session.GetHistorySnapshot().ShouldBe(original, "the detached candidate must not mutate the captured live aggregate");
        (await sessions.GetAsync(other.SessionId)).ShouldNotBeNull().GetHistorySnapshot().ShouldBe(otherOriginal);
        (await conversations.GetAsync(otherConversation.ConversationId)).ShouldNotBeNull().ActiveSessionId.ShouldBe(other.SessionId);
        f.Supervisor.GetHandle(other.AgentId, other.SessionId).ShouldBeSameAs(otherHandle);
        using var maintenance = f.Supervisor.TryReserveMaintenance(otherHandle);
        maintenance.ShouldNotBeNull();
        await f.Supervisor.StopAllAsync();
        foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            if (File.Exists(path)) File.Delete(path);
    }

    [Theory]
    [InlineData("memory", false, false)]
    [InlineData("memory", true, false)]
    [InlineData("file", false, false)]
    [InlineData("file", true, false)]
    [InlineData("sqlite", false, false)]
    [InlineData("sqlite", true, false)]
    [InlineData("memory", false, true)]
    [InlineData("file", false, true)]
    public async Task Compact_ConcurrentNoWakeAppend_PreservesMessageAndReportsConflict(string backend, bool atFinalSave, bool noOp)
    {
        await ConcurrentMutationAsync(backend, atFinalSave, noOp, metadata: false);
    }

    [Theory]
    [InlineData("memory", false)]
    [InlineData("memory", true)]
    [InlineData("file", false)]
    [InlineData("file", true)]
    [InlineData("sqlite", false)]
    [InlineData("sqlite", true)]
    public async Task Compact_ConcurrentMetadataPatch_PreservesPatchAndReportsConflict(string backend, bool atFinalSave)
    {
        await ConcurrentMutationAsync(backend, atFinalSave, noOp: false, metadata: true);
    }

    private static async Task ConcurrentMutationAsync(string backend, bool atFinalSave, bool noOp, bool metadata)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"botnexus-compaction-append-{Guid.NewGuid():N}.db");
        var conversations = new InMemoryConversationStore();
        var fs = new System.IO.Abstractions.TestingHelpers.MockFileSystem();
        var storePath = Path.Combine(Path.GetTempPath(), $"compaction-append-{Guid.NewGuid():N}");
        var connection = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();
        ISessionStore CreateStore() => backend switch
        {
            "file" => new FileSessionStore(storePath, NullLogger<FileSessionStore>.Instance, fs, conversations),
            "sqlite" => new SqliteSessionStore(connection, NullLogger<SqliteSessionStore>.Instance, conversations),
            _ => new InMemorySessionStore()
        };
        var durable = CreateStore();
        var boundary = new FinalSaveBoundary(durable);
        var f = await Fixture.CreateAsync(conversations, boundary);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Compactor.CompactAsync(Arg.Any<GatewaySession>(), Arg.Any<CompactionOptions>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var result = noOp ? CompactionResult.Skipped(skipReason: CompactionSkipReason.EmptyHistory)
                    : Success(call.Arg<GatewaySession>());
                entered.TrySetResult();
                if (!atFinalSave) await release.Task;
                return result;
            });
        try
        {
            var original = f.Session.GetHistorySnapshot().ToArray();
            var pending = f.InvokeAsync();
            await (atFinalSave ? boundary.Entered.Task : entered.Task).WaitAsync(TimeSpan.FromSeconds(10));
            if (metadata)
            {
                var patch = await durable.PatchMetadataAsync(f.Session.SessionId,
                    new Dictionary<string, object?> { ["concurrent"] = "patch" });
                patch.Outcome.ShouldBe(SessionMutationOutcome.Applied);
            }
            else
            {
                var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
                registry.Register(new AgentDescriptor { AgentId = f.Handle.AgentId, DisplayName = "owner",
                    ModelId = "test", ApiProvider = "test", IsolationStrategy = "test" });
                var router = new DefaultConversationRouter(conversations, durable, NullLogger<DefaultConversationRouter>.Instance);
                var orchestrator = Substitute.For<IInboundMessageOrchestrator>();
                var messages = new ConversationMessagesController(conversations, durable,
                    new DefaultConversationDispatcher(router, conversations), orchestrator, registry,
                    NullLogger<ConversationMessagesController>.Instance)
                { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
                (await messages.PostMessage(f.Handle.AgentId.Value, f.Conversation.ConversationId.Value,
                    new PostConversationMessageRequest("concurrent append", Wake: false), CancellationToken.None))
                    .ShouldBeOfType<AcceptedResult>();
                orchestrator.ReceivedCalls().ShouldBeEmpty();
            }
            release.TrySetResult();
            boundary.Release.TrySetResult();
            var response = await pending;
            if (noOp) response.ShouldBeOfType<OkObjectResult>();
            else System.Text.Json.JsonSerializer.Serialize(response.ShouldBeOfType<ConflictObjectResult>().Value)
                .ShouldBe("{\"status\":\"concurrent-session-change\"}");
            var reader = backend == "memory" ? durable : CreateStore();
            var current = (await reader.GetAsync(f.Session.SessionId)).ShouldNotBeNull();
            var history = current.GetHistorySnapshot();
            history.ShouldNotContain(e => e.IsHistory || e.IsCompactionSummary);
            history.Take(original.Length).Select(e => e with { PersistenceId = null, PersistenceKey = null })
                .ShouldBe(original.Select(e => e with { PersistenceId = null, PersistenceKey = null }));
            if (metadata) current.Metadata["concurrent"]?.ToString().ShouldBe("patch");
            else history.Last().Content.ShouldBe("concurrent append");
            history.Count.ShouldBe(original.Length + (metadata ? 0 : 1));
            current.Metadata["retained"]?.ToString().ShouldBe("metadata");
            f.Session.GetHistorySnapshot().ShouldNotContain(e => e.IsHistory || e.IsCompactionSummary);
            f.Supervisor.GetHandle(f.Handle.AgentId, f.Handle.SessionId).ShouldBeSameAs(f.Handle);
            using var reservation = f.Supervisor.TryReserveMaintenance(f.Handle);
            reservation.ShouldNotBeNull();
            if (noOp) boundary.Candidate.ShouldBeNull("a no-op must never publish a stale detached copy");
        }
        finally
        {
            release.TrySetResult();
            boundary.Release.TrySetResult();
            await f.Supervisor.StopAllAsync();
            foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class ThrowingCoordinator(Func<Exception> exception) : ISessionCompactionCoordinator
    {
        public Task<SessionCompactionOutcome> CompactAsync(AgentId agentId, GatewaySession session,
            CancellationToken cancellationToken, bool force = false,
            CompactionHandlePolicy handlePolicy = CompactionHandlePolicy.Evict, CompactionOptions? resolvedOptions = null)
        {
            force.ShouldBeTrue();
            handlePolicy.ShouldBe(CompactionHandlePolicy.KeepCurrent);
            resolvedOptions.ShouldBeNull();
            return Task.FromException<SessionCompactionOutcome>(exception());
        }
        public string BuildNotificationText(SessionCompactionOutcome outcome) => throw new NotSupportedException();
        public Task<bool> TryPublishNotificationAsync(SessionCompactionOutcome outcome, AgentId agentId,
            GatewaySession session, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static CompactionResult Success(GatewaySession session)
    {
        var snapshot = session.SnapshotHistoryForCompaction();
        var compacted = snapshot.Entries.Select(e => e with { IsHistory = true }).ToList();
        compacted.Add(new SessionEntry { Role = MessageRole.System, Content = "summary", IsCompactionSummary = true });
        return CompactionResult.ForSuccess("summary", compacted, snapshot.Count, 0, 100, 10,
            snapshot.DestructiveVersion, snapshot.Count);
    }

    private sealed class FinalSaveBoundary(ISessionStore inner) : SessionStoreBase
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public GatewaySession? Candidate { get; private set; }

        public override Task<GatewaySession?> GetAsync(SessionId sessionId, CancellationToken cancellationToken = default) =>
            inner.GetAsync(sessionId, cancellationToken);
        public override Task<GatewaySession> GetOrCreateAsync(SessionId sessionId, AgentId agentId, CancellationToken cancellationToken = default) =>
            inner.GetOrCreateAsync(sessionId, agentId, cancellationToken);
        public override Task SaveAsync(GatewaySession session, CancellationToken cancellationToken = default) =>
            inner.SaveAsync(session, cancellationToken);
        public override async Task<SessionSaveOutcome> SaveAsync(GatewaySession session, SessionWriteFence fence, CancellationToken cancellationToken = default)
        {
            Candidate = session;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await inner.SaveAsync(session, fence, cancellationToken);
        }
        public override Task DeleteAsync(SessionId sessionId, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(sessionId, cancellationToken);
        public override Task ArchiveAsync(SessionId sessionId, CancellationToken cancellationToken = default) =>
            inner.ArchiveAsync(sessionId, cancellationToken);
        protected override Task<IReadOnlyList<GatewaySession>> EnumerateSessionsAsync(CancellationToken cancellationToken) =>
            inner.ListAsync(cancellationToken: cancellationToken);
    }

    private sealed record Fixture(InMemoryConversationStore Conversations, Conversation Conversation,
        GatewaySession Session, IAgentHandle Handle, DefaultAgentSupervisor Supervisor,
        IIsolationStrategy Strategy, ISessionCompactor Compactor, ConversationsController Controller)
    {
        public Task<ActionResult> InvokeAsync(CancellationToken token = default) => Controller.CompactAsync(
            Conversation.ConversationId.Value, new CompactConversationRequest(Session.SessionId.Value), token);

        public static async Task<Fixture> CreateAsync(InMemoryConversationStore? conversationStore = null, ISessionStore? sessionStore = null,
            IAgentHandle? residentHandle = null)
        {
            var conversations = conversationStore ?? new InMemoryConversationStore();
            var sessions = sessionStore ?? new InMemorySessionStore();
            var handle = residentHandle ?? CompactionAdmissionTests.Create().Handle;
            var conversation = await conversations.CreateAsync(new Conversation
            {
                ConversationId = ConversationId.Create(), AgentId = handle.AgentId,
                Visibility = ConversationVisibility.UserFacing, Title = "compact",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            });
            var session = await sessions.GetOrCreateAsync(handle.SessionId, handle.AgentId);
            session.ConversationId = conversation.ConversationId;
            session.CallerId = "caller";
            session.Metadata["retained"] = "metadata";
            var sequence = session.StreamReplay.AllocateSequenceId();
            session.StreamReplay.AddEvent(sequence, "retained replay", 10);
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "original question" });
            session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "original answer", ThinkingContent = "retained thinking" });
            await sessions.SaveAsync(session);
            // SQLite evicts on save. Use the cached instance that reset will also read.
            session = (await sessions.GetAsync(session.SessionId)).ShouldNotBeNull();
            conversation = conversation with { ActiveSessionId = session.SessionId };
            await conversations.SaveAsync(conversation);
            var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
            registry.Register(new AgentDescriptor { AgentId = handle.AgentId, DisplayName = "owner",
                ModelId = "test", ApiProvider = "test", IsolationStrategy = "test" });
            var strategy = Substitute.For<IIsolationStrategy>();
            strategy.Name.Returns("test");
            strategy.CreateAsync(Arg.Any<AgentDescriptor>(), Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>()).Returns(handle);
            var supervisor = new DefaultAgentSupervisor(registry, [strategy], sessions, NullLogger<DefaultAgentSupervisor>.Instance);
            await supervisor.GetOrCreateAsync(handle.AgentId, handle.SessionId);
            var compactor = Substitute.For<ISessionCompactor>();
            var options = Substitute.For<IOptionsMonitor<CompactionOptions>>();
            options.CurrentValue.Returns(new CompactionOptions());
            var coordinator = new SessionCompactionCoordinator(compactor, sessions, supervisor, null, conversations,
                options, NullLogger<SessionCompactionCoordinator>.Instance);
            var controller = new ConversationsController(conversations, sessions, supervisor: supervisor,
                compactionCoordinator: coordinator)
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
            return new(conversations, conversation, session, handle, supervisor, strategy, compactor, controller);
        }
    }
}
