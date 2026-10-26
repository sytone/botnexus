using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Events;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

public sealed class BoundedRecoveryTests
{
    private static GatewaySession Session(string id, SessionStatus status = SessionStatus.Active)
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From(id),
            AgentId = AgentId.From("agent"),
            ConversationId = ConversationId.From($"conv-{id}"),
            Status = status,
            SessionType = SessionType.UserAgent
        };
        session.Metadata["keep"] = "unchanged";
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "request" });
        session.AddEntry(new SessionEntry { Role = MessageRole.System, Content = "sentinel", IsCrashSentinel = true });
        return session;
    }

    [Fact]
    public async Task SqliteCandidates_ExcludeTerminalBeforePagingAndOwnerResolution_PreserveDataAcrossReopens()
    {
        var directory = Directory.CreateTempSubdirectory("bounded-recovery-").FullName;
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "sessions.db"),
            Pooling = false
        }.ToString();
        var conversations = new Mock<IConversationStore>();
        conversations.Setup(s => s.GetAsync(It.IsAny<ConversationId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConversationId id, CancellationToken _) => new Conversation
            {
                ConversationId = id,
                AgentId = AgentId.From("agent")
            });
        SqliteSessionStore Open() => new(connectionString, NullLogger<SqliteSessionStore>.Instance, conversations.Object);
        try
        {
            var sessions = new[]
            {
                Session("a", SessionStatus.Sealed),
                Session("b"),
                Session("c", SessionStatus.Expired),
                Session("d", SessionStatus.Suspended),
                Session("e", SessionStatus.Sealed),
                Session("f", SessionStatus.Expired),
                Session("g", SessionStatus.Sealed)
            };
            foreach (var session in sessions)
                await Open().SaveAsync(session);
            for (var restart = 0; restart < 2; restart++)
            {
                conversations.Invocations.Clear();
                var store = Open();
                // Initialize this store first, then inject raw statuses so migration cannot
                // hide whether the candidate SQL itself handles the legacy spelling.
                await store.ListUnresolvedCrashSentinelsAsync(1);
                await using (var connection = new SqliteConnection(connectionString))
                {
                    await connection.OpenAsync();
                    await using var command = connection.CreateCommand();
                    command.CommandText = """
                        UPDATE sessions SET status = CASE id
                            WHEN 'a' THEN ' SeAlEd '
                            WHEN 'c' THEN ' ExPiReD '
                            WHEN 'e' THEN ' ClOsEd '
                            WHEN 'f' THEN '2'
                            WHEN 'g' THEN '3'
                            ELSE status END
                        """;
                    await command.ExecuteNonQueryAsync();
                }
                conversations.Invocations.Clear();
                var first = await store.ListUnresolvedCrashSentinelsAsync(1);
                var second = await store.ListUnresolvedCrashSentinelsAsync(1, first.NextCursor);
                first.Rows.Select(r => r.SessionId.Value).ShouldBe(["b"]);
                first.NextCursor.ShouldBe("b");
                second.Rows.Select(r => r.SessionId.Value).ShouldBe(["d"]);
                second.NextCursor.ShouldBeNull();
                var terminals = sessions.Where(s => SessionMutationPolicy.IsTerminal(s.Status)).ToArray();
                // Verify every exclusion before hydration itself performs owner lookups.
                foreach (var terminal in terminals)
                {
                    conversations.Verify(
                        s => s.GetAsync(terminal.ConversationId, It.IsAny<CancellationToken>()),
                        Times.Never);
                }
                foreach (var terminal in terminals)
                {
                    var loaded = await Open().GetAsync(terminal.SessionId);
                    loaded.ShouldNotBeNull();
                    loaded.Status.ShouldBe(terminal.Status);
                    loaded.UpdatedAt.ShouldBe(terminal.UpdatedAt);
                    loaded.Metadata["keep"]?.ToString().ShouldBe("unchanged");
                    loaded.History.Select(e => (e.PersistenceId, e.Content, e.IsCrashSentinel))
                        .ShouldBe(terminal.History.Select(e => (e.PersistenceId, e.Content, e.IsCrashSentinel)));
                    loaded.History.ShouldNotContain(e => e.Role == MessageRole.Notification || e.IsReplayBanner);
                }
            }
        }
        finally
        {
            SqlitePoolCleanup.ClearPoolsUnder(directory);
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PortableCandidates_ExcludeTerminalBeforePaging(bool interfaceDefault)
    {
        var memory = new InMemorySessionStore();
        var sessions = new[]
        {
            Session("a", SessionStatus.Sealed),
            Session("b"),
            Session("c", SessionStatus.Expired),
            Session("d", SessionStatus.Suspended),
            Session("e", SessionStatus.Sealed)
        };
        foreach (var session in sessions)
            await memory.SaveAsync(session);
        ISessionStore store = interfaceDefault ? new DefaultStore(memory) : memory;
        for (var restart = 0; restart < 2; restart++)
        {
            var first = await store.ListUnresolvedCrashSentinelsAsync(1);
            var second = await store.ListUnresolvedCrashSentinelsAsync(1, first.NextCursor);
            first.Rows.Select(r => r.SessionId.Value).ShouldBe(["b"]);
            first.NextCursor.ShouldBe("b");
            second.Rows.Select(r => r.SessionId.Value).ShouldBe(["d"]);
            second.NextCursor.ShouldBeNull();
            sessions.Where(s => SessionMutationPolicy.IsTerminal(s.Status)).ShouldAllBe(s =>
                s.History.Count == 2
                && s.History.Any(e => e.IsCrashSentinel)
                && Equals(s.Metadata["keep"], "unchanged"));
        }
    }

    [Theory]
    [InlineData(SessionStatus.Sealed)]
    [InlineData(SessionStatus.Expired)]
    public async Task StartedAsync_TerminalAfterCandidateRead_DoesNotMutateResolveSaveReplayOrPublish(SessionStatus status)
    {
        var session = Session("terminal");
        var store = Store(session);
        store.Setup(s => s.GetAsync(session.SessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                session.Status = status;
                return session;
            });
        var conversations = new Mock<IConversationStore>();
        var orchestrator = new Mock<IInboundMessageOrchestrator>();
        var publisher = new Mock<IConversationEventPublisher>();
        var broadcaster = new Mock<IActivityBroadcaster>();
        var logger = new CaptureLogger();
        await Service(store.Object, logger, orchestrator.Object, publisher.Object,
            broadcaster.Object, conversations.Object).StartedAsync(default);
        session.History.Count.ShouldBe(2);
        session.History.ShouldContain(e => e.IsCrashSentinel);
        session.Metadata.Count.ShouldBe(1);
        store.Verify(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<SessionWriteFence>(),
            It.IsAny<CancellationToken>()), Times.Never);
        conversations.Invocations.ShouldBeEmpty();
        orchestrator.Invocations.ShouldBeEmpty();
        publisher.Invocations.ShouldBeEmpty();
        broadcaster.Invocations.ShouldBeEmpty();
        logger.Summary["TerminalSkippedCount"].ShouldBe(1);
        logger.Summary["NotifiedCount"].ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartedAsync_SummarySeparatesPersistedReplayFenceAndFailure(bool replay)
    {
        var sessions = new[] { Session("a"), Session("b"), Session("c"), Session("d") };
        var store = Store(sessions);
        store.Setup(s => s.SaveAsync(sessions[1], It.IsAny<SessionWriteFence>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SessionSaveOutcome.Rebound);
        store.Setup(s => s.SaveAsync(sessions[2], It.IsAny<SessionWriteFence>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("isolated save failure"));
        var orchestrator = new Mock<IInboundMessageOrchestrator>();
        orchestrator.Setup(o => o.Post(It.IsAny<InboundMessage>())).Returns(true);
        var publisher = new Mock<IConversationEventPublisher>();
        publisher.Setup(p => p.PublishAsync(It.IsAny<ConversationEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var logger = new CaptureLogger();
        await Service(store.Object, logger, replay ? orchestrator.Object : null, publisher.Object).StartedAsync(default);
        logger.Summary["ScannedRows"].ShouldBe(4);
        logger.Summary["NotifiedCount"].ShouldBe(2);
        logger.Summary["ReplayedCount"].ShouldBe(replay ? 4 : 0);
        logger.Summary["FenceRejectedCount"].ShouldBe(1);
        logger.Summary["PersistenceFailedCount"].ShouldBe(1);
        logger.Summary["TerminalSkippedCount"].ShouldBe(0);
        publisher.Verify(p => p.PublishAsync(It.IsAny<ConversationEvent>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        store.Verify(s => s.SaveAsync(sessions[3], It.IsAny<SessionWriteFence>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartedAsync_PostCommitActivityFailure_CountsPersistedNotificationAndContinues()
    {
        var sessions = new[] { Session("a"), Session("b") };
        var store = Store(sessions);
        var broadcaster = new Mock<IActivityBroadcaster>();
        broadcaster.Setup(b => b.PublishAsync(It.IsAny<GatewayActivity>(), It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromException(new IOException("isolated activity failure")));
        var publisher = new Mock<IConversationEventPublisher>();
        publisher.Setup(p => p.PublishAsync(It.IsAny<ConversationEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var logger = new CaptureLogger();

        await Service(store.Object, logger, publisher: publisher.Object, broadcaster: broadcaster.Object)
            .StartedAsync(default);

        logger.Summary["ScannedRows"].ShouldBe(2);
        logger.Summary["NotifiedCount"].ShouldBe(2);
        logger.Summary["ReplayedCount"].ShouldBe(0);
        logger.Summary["FenceRejectedCount"].ShouldBe(0);
        logger.Summary["PersistenceFailedCount"].ShouldBe(0);
        logger.Summary["TerminalSkippedCount"].ShouldBe(0);
        publisher.Verify(p => p.PublishAsync(It.IsAny<ConversationEvent>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        foreach (var session in sessions)
        {
            store.Verify(s => s.SaveAsync(session, It.IsAny<SessionWriteFence>(), It.IsAny<CancellationToken>()),
                Times.Once);
            session.History.Count(e => e.Role == MessageRole.Notification).ShouldBe(1);
            session.History.ShouldNotContain(e => e.IsCrashSentinel);
        }
    }

    private static Mock<ISessionStore> Store(params GatewaySession[] sessions)
    {
        var store = new Mock<ISessionStore>();
        store.Setup(s => s.ListUnresolvedCrashSentinelsAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnresolvedCrashSentinelPage(
                sessions.Select(s => new UnresolvedCrashSentinelRow(s.SessionId, s.AgentId)).ToList(), null));
        store.Setup(s => s.GetAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SessionId id, CancellationToken _) => sessions.Single(s => s.SessionId == id));
        store.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<SessionWriteFence>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SessionSaveOutcome.Persisted);
        return store;
    }

    private static InterruptedTurnNotificationService Service(
        ISessionStore store,
        CaptureLogger logger,
        IInboundMessageOrchestrator? orchestrator = null,
        IConversationEventPublisher? publisher = null,
        IActivityBroadcaster? broadcaster = null,
        IConversationStore? conversations = null)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetAll()).Returns([new AgentDescriptor
        {
            AgentId = AgentId.From("agent"),
            DisplayName = "agent",
            ModelId = "model",
            ApiProvider = "provider"
        }]);
        return new(store, registry.Object, broadcaster ?? Mock.Of<IActivityBroadcaster>(),
            publisher ?? Mock.Of<IConversationEventPublisher>(), logger, orchestrator,
            Options.Create(new GatewayOptions { AutoReplayInterruptedTurns = true }), conversations);
    }

    private sealed class CaptureLogger : ILogger<InterruptedTurnNotificationService>
    {
        public Dictionary<string, object?> Summary { get; private set; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).StartsWith("Interrupted-turn scan complete:", StringComparison.Ordinal)
                && state is IEnumerable<KeyValuePair<string, object?>> values)
                Summary = values.ToDictionary(p => p.Key, p => p.Value);
        }
    }

    // Deliberately does not implement the candidate API: exercises the real interface default.
    private sealed class DefaultStore(InMemorySessionStore inner) : ISessionStore
    {
        public Task<GatewaySession?> GetAsync(SessionId id, CancellationToken ct = default) => inner.GetAsync(id, ct);
        public Task<GatewaySession> GetOrCreateAsync(SessionId id, AgentId agent, CancellationToken ct = default) => inner.GetOrCreateAsync(id, agent, ct);
        public Task SaveAsync(GatewaySession session, CancellationToken ct = default) => inner.SaveAsync(session, ct);
        public Task DeleteAsync(SessionId id, CancellationToken ct = default) => inner.DeleteAsync(id, ct);
        public Task ArchiveAsync(SessionId id, CancellationToken ct = default) => inner.ArchiveAsync(id, ct);
        public Task<IReadOnlyList<GatewaySession>> ListAsync(AgentId? agent = null, CancellationToken ct = default) => inner.ListAsync(agent, ct);
        public Task<IReadOnlyList<GatewaySession>> ListByChannelAsync(AgentId agent, ChannelKey channel, CancellationToken ct = default) => inner.ListByChannelAsync(agent, channel, ct);
        public Task<IReadOnlyList<GatewaySession>> ListByConversationAsync(ConversationId conversation, AgentId? agent = null, CancellationToken ct = default) => inner.ListByConversationAsync(conversation, agent, ct);
        public Task<IReadOnlyList<GatewaySession>> GetExistenceAsync(AgentId agent, ExistenceQuery query, CancellationToken ct = default) => inner.GetExistenceAsync(agent, query, ct);
    }
}
