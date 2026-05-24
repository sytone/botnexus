using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Services;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Conversations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

namespace BotNexus.Gateway.Tests.Conversations;

/// <summary>
/// Behavioural tests for <see cref="DefaultConversationResetService"/>. Covers every outcome
/// branch (Reset / NoActiveSession / NotFound / StaleSessionId), the canonical step ordering
/// (Stop → Flush → CancelAskUser → Seal → ClearActiveSessionId), and best-effort error
/// behaviour around the supervisor, flusher, and ask-user cancellation.
/// </summary>
public sealed class DefaultConversationResetServiceTests
{
    private static readonly AgentId TestAgent = AgentId.From("agent-a");
    private static readonly ConversationId TestConversation = ConversationId.From("conv-1");
    private static readonly SessionId TestSession = SessionId.From("session-1");

    // ─── Outcome branches ────────────────────────────────────────────────────

    [Fact]
    public async Task Reset_UnknownConversation_ReturnsNotFound()
    {
        var fixture = new Fixture();
        // No conversation set up.

        var result = await fixture.Service.ResetActiveSessionAsync(TestConversation);

        result.Outcome.ShouldBe(ConversationResetOutcome.NotFound);
        result.SealedSessionId.ShouldBeNull();
        result.AgentId.ShouldBeNull();
        fixture.Supervisor.Verify(s => s.StopAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Reset_ConversationWithNoActiveSession_ReturnsNoActiveSession_AndIsIdempotent()
    {
        var fixture = new Fixture();
        var conversation = new Conversation { ConversationId = TestConversation, AgentId = TestAgent, ActiveSessionId = null };
        fixture.SetupConversation(conversation);

        var result = await fixture.Service.ResetActiveSessionAsync(TestConversation);

        result.Outcome.ShouldBe(ConversationResetOutcome.NoActiveSession);
        result.SealedSessionId.ShouldBeNull();
        result.AgentId.ShouldBe(TestAgent);
        fixture.Supervisor.Verify(s => s.StopAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Conversations.Verify(c => c.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Reset_StaleExpectedActiveSessionId_ReturnsStaleSessionId_AndDoesNotTouchAnything()
    {
        var fixture = new Fixture();
        var newerSession = SessionId.From("session-2");
        var conversation = new Conversation { ConversationId = TestConversation, AgentId = TestAgent, ActiveSessionId = newerSession };
        fixture.SetupConversation(conversation);

        var result = await fixture.Service.ResetActiveSessionAsync(TestConversation, expectedActiveSessionId: TestSession);

        result.Outcome.ShouldBe(ConversationResetOutcome.StaleSessionId);
        result.SealedSessionId.ShouldBeNull();
        result.AgentId.ShouldBe(TestAgent);
        fixture.Supervisor.Verify(s => s.StopAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Conversations.Verify(c => c.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Sessions.Verify(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Reset_MatchingExpectedActiveSessionId_ProceedsAsNormal()
    {
        var fixture = new Fixture();
        var (conversation, session) = fixture.SetupInteractiveConversationWithSession();

        var result = await fixture.Service.ResetActiveSessionAsync(TestConversation, expectedActiveSessionId: TestSession);

        result.Outcome.ShouldBe(ConversationResetOutcome.Reset);
        result.SealedSessionId.ShouldBe(TestSession);
        session.Session.Status.ShouldBe(SessionStatus.Sealed);
        conversation.ActiveSessionId.ShouldBeNull();
    }

    [Fact]
    public async Task Reset_MissingSessionInStore_DefensivelyClearsActiveSessionId_AndReturnsNoActiveSession()
    {
        var fixture = new Fixture();
        var conversation = new Conversation { ConversationId = TestConversation, AgentId = TestAgent, ActiveSessionId = TestSession };
        fixture.SetupConversation(conversation);
        // SessionStore returns null for TestSession (not set up).

        var result = await fixture.Service.ResetActiveSessionAsync(TestConversation);

        result.Outcome.ShouldBe(ConversationResetOutcome.NoActiveSession);
        result.AgentId.ShouldBe(TestAgent);
        conversation.ActiveSessionId.ShouldBeNull();
        fixture.Conversations.Verify(c => c.SaveAsync(conversation, It.IsAny<CancellationToken>()), Times.Once);
        fixture.Supervisor.Verify(s => s.StopAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── Canonical sequence ──────────────────────────────────────────────────

    [Fact]
    public async Task Reset_InteractiveSession_ExecutesCanonicalSequence_InOrder()
    {
        var fixture = new Fixture();
        var (conversation, session) = fixture.SetupInteractiveConversationWithSession();
        var sequence = new MockSequence();

        // We can't put SaveAsync on the sequence reliably because the same mock is reused
        // for both conversation+session saves. Instead, assert ordering via a manual list.
        var callOrder = new List<string>();
        fixture.Supervisor.Setup(s => s.StopAsync(TestAgent, TestSession, It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("stop"))
            .Returns(Task.CompletedTask);
        fixture.Flusher.Setup(f => f.FlushAsync(TestAgent, session.Session, It.IsAny<CompactionOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("flush"))
            .Returns(Task.CompletedTask);
        fixture.AskUserRegistry.Setup(a => a.CancelAllForConversation(TestConversation))
            .Callback(() => callOrder.Add("cancel-ask-user"));
        fixture.Sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("save-session"))
            .Returns(Task.CompletedTask);
        fixture.Conversations.Setup(c => c.SaveAsync(conversation, It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("save-conversation"))
            .Returns(Task.CompletedTask);

        var result = await fixture.Service.ResetActiveSessionAsync(TestConversation);

        result.Outcome.ShouldBe(ConversationResetOutcome.Reset);
        callOrder.ShouldBe(new[] { "stop", "flush", "cancel-ask-user", "save-session", "save-conversation" });
    }

    [Fact]
    public async Task Reset_SealsSession_WithStatusSealedAndUpdatedTimestamp_NotArchiveAsync()
    {
        var fixture = new Fixture();
        var fixedNow = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        fixture.TimeProvider = new FixedTimeProvider(fixedNow);
        var (conversation, session) = fixture.SetupInteractiveConversationWithSession();

        await fixture.Service.ResetActiveSessionAsync(TestConversation);

        session.Session.Status.ShouldBe(SessionStatus.Sealed);
        session.Session.UpdatedAt.ShouldBe(fixedNow);
        // Critical: ArchiveAsync is intentionally NOT used (it deletes in InMemorySessionStore,
        // renames in FileSessionStore — both destroy transcript readability).
        fixture.Sessions.Verify(s => s.ArchiveAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Sessions.Verify(s => s.SaveAsync(session, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Reset_ClearsActiveSessionId_AndUpdatesConversationTimestamp()
    {
        var fixture = new Fixture();
        var fixedNow = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        fixture.TimeProvider = new FixedTimeProvider(fixedNow);
        var (conversation, _) = fixture.SetupInteractiveConversationWithSession();

        await fixture.Service.ResetActiveSessionAsync(TestConversation);

        conversation.ActiveSessionId.ShouldBeNull();
        conversation.UpdatedAt.ShouldBe(fixedNow);
        fixture.Conversations.Verify(c => c.SaveAsync(conversation, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── Best-effort error handling ──────────────────────────────────────────

    [Fact]
    public async Task Reset_FlushFailure_IsSwallowed_AndResetCompletes()
    {
        var fixture = new Fixture();
        var (conversation, session) = fixture.SetupInteractiveConversationWithSession();
        fixture.Flusher.Setup(f => f.FlushAsync(It.IsAny<AgentId>(), It.IsAny<Session>(), It.IsAny<CompactionOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("memory backend down"));

        var result = await fixture.Service.ResetActiveSessionAsync(TestConversation);

        result.Outcome.ShouldBe(ConversationResetOutcome.Reset);
        session.Session.Status.ShouldBe(SessionStatus.Sealed);
        conversation.ActiveSessionId.ShouldBeNull();
    }

    [Fact]
    public async Task Reset_SupervisorStopFailure_IsSwallowed_AndResetCompletes()
    {
        var fixture = new Fixture();
        var (conversation, session) = fixture.SetupInteractiveConversationWithSession();
        fixture.Supervisor.Setup(s => s.StopAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("handle already disposed"));

        var result = await fixture.Service.ResetActiveSessionAsync(TestConversation);

        result.Outcome.ShouldBe(ConversationResetOutcome.Reset);
        session.Session.Status.ShouldBe(SessionStatus.Sealed);
        conversation.ActiveSessionId.ShouldBeNull();
    }

    [Fact]
    public async Task Reset_AskUserCancelFailure_IsSwallowed_AndResetCompletes()
    {
        var fixture = new Fixture();
        var (conversation, session) = fixture.SetupInteractiveConversationWithSession();
        fixture.AskUserRegistry.Setup(a => a.CancelAllForConversation(It.IsAny<ConversationId>()))
            .Throws(new InvalidOperationException("registry corrupt"));

        var result = await fixture.Service.ResetActiveSessionAsync(TestConversation);

        result.Outcome.ShouldBe(ConversationResetOutcome.Reset);
        session.Session.Status.ShouldBe(SessionStatus.Sealed);
        conversation.ActiveSessionId.ShouldBeNull();
    }

    // ─── Conditional invocation ─────────────────────────────────────────────

    [Fact]
    public async Task Reset_NonInteractiveSession_SkipsFlush_StillSealsAndClears()
    {
        var fixture = new Fixture();
        var conversation = new Conversation { ConversationId = TestConversation, AgentId = TestAgent, ActiveSessionId = TestSession };
        var session = BuildSession(SessionType.Heartbeat); // ShouldFlush returns false.
        fixture.SetupConversation(conversation);
        fixture.SetupSession(session);
        // Default Flusher.ShouldFlush mock returns based on real semantics; for Heartbeat the
        // real flusher would say false. We mirror that here.
        fixture.Flusher.Setup(f => f.ShouldFlush(session.Session, It.IsAny<CompactionOptions>())).Returns(false);

        var result = await fixture.Service.ResetActiveSessionAsync(TestConversation);

        result.Outcome.ShouldBe(ConversationResetOutcome.Reset);
        fixture.Flusher.Verify(f => f.FlushAsync(It.IsAny<AgentId>(), It.IsAny<Session>(), It.IsAny<CompactionOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        session.Session.Status.ShouldBe(SessionStatus.Sealed);
        conversation.ActiveSessionId.ShouldBeNull();
    }

    [Fact]
    public async Task Reset_NullFlusher_StillCompletes()
    {
        var fixture = new Fixture { IncludeFlusher = false };
        var (conversation, session) = fixture.SetupInteractiveConversationWithSession();

        var result = await fixture.Service.ResetActiveSessionAsync(TestConversation);

        result.Outcome.ShouldBe(ConversationResetOutcome.Reset);
        session.Session.Status.ShouldBe(SessionStatus.Sealed);
        conversation.ActiveSessionId.ShouldBeNull();
    }

    [Fact]
    public async Task Reset_NullAskUserRegistry_StillCompletes()
    {
        var fixture = new Fixture { IncludeAskUserRegistry = false };
        var (conversation, session) = fixture.SetupInteractiveConversationWithSession();

        var result = await fixture.Service.ResetActiveSessionAsync(TestConversation);

        result.Outcome.ShouldBe(ConversationResetOutcome.Reset);
        session.Session.Status.ShouldBe(SessionStatus.Sealed);
        conversation.ActiveSessionId.ShouldBeNull();
    }

    [Fact]
    public async Task Reset_CancelsAskUserOncePerConversation()
    {
        var fixture = new Fixture();
        fixture.SetupInteractiveConversationWithSession();

        await fixture.Service.ResetActiveSessionAsync(TestConversation);

        fixture.AskUserRegistry.Verify(a => a.CancelAllForConversation(TestConversation), Times.Once);
    }

    // ─── Fixture & helpers ──────────────────────────────────────────────────

    private static GatewaySession BuildSession(SessionType type)
    {
        var session = new GatewaySession
        {
            SessionId = TestSession,
            AgentId = TestAgent,
        };
        session.Session.SessionType = type;
        session.Session.ConversationId = TestConversation;
        session.Session.History.Add(new SessionEntry { Role = MessageRole.User, Content = "hello" });
        return session;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class Fixture
    {
        public Mock<IConversationStore> Conversations { get; } = new(MockBehavior.Strict);
        public Mock<ISessionStore> Sessions { get; } = new(MockBehavior.Strict);
        public Mock<IAgentSupervisor> Supervisor { get; } = new(MockBehavior.Strict);
        public Mock<ISessionEndMemoryFlusher> Flusher { get; } = new(MockBehavior.Strict);
        public Mock<IAskUserResponseRegistry> AskUserRegistry { get; } = new(MockBehavior.Strict);
        public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

        public bool IncludeFlusher { get; init; } = true;
        public bool IncludeAskUserRegistry { get; init; } = true;

        public Fixture()
        {
            // Default lenient setups for "looks up missing things returns null" so NotFound paths work.
            Conversations.Setup(c => c.GetAsync(It.IsAny<ConversationId>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Conversation?)null);
            Conversations.Setup(c => c.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            Sessions.Setup(s => s.GetAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((GatewaySession?)null);
            Sessions.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            Supervisor.Setup(s => s.StopAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            // Default Flusher: interactive sessions are flushable, non-interactive aren't —
            // individual tests can override. Calls to FlushAsync return completed by default.
            Flusher.Setup(f => f.ShouldFlush(It.IsAny<Session>(), It.IsAny<CompactionOptions>()))
                .Returns<Session, CompactionOptions>((s, _) => s.IsInteractive && s.History.Any(e => e.Role == MessageRole.User));
            Flusher.Setup(f => f.FlushAsync(It.IsAny<AgentId>(), It.IsAny<Session>(), It.IsAny<CompactionOptions>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            AskUserRegistry.Setup(a => a.CancelAllForConversation(It.IsAny<ConversationId>()));
        }

        public IConversationResetService Service => new DefaultConversationResetService(
            Conversations.Object,
            Sessions.Object,
            Supervisor.Object,
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions { MemoryFlush = new MemoryFlushOptions { Enabled = true } }),
            NullLogger<DefaultConversationResetService>.Instance,
            IncludeFlusher ? Flusher.Object : null,
            IncludeAskUserRegistry ? AskUserRegistry.Object : null,
            TimeProvider);

        public void SetupConversation(Conversation conversation)
        {
            Conversations.Setup(c => c.GetAsync(conversation.ConversationId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(conversation);
        }

        public void SetupSession(GatewaySession session)
        {
            Sessions.Setup(s => s.GetAsync(session.SessionId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(session);
        }

        public (Conversation, GatewaySession) SetupInteractiveConversationWithSession()
        {
            var conversation = new Conversation { ConversationId = TestConversation, AgentId = TestAgent, ActiveSessionId = TestSession };
            var session = BuildSession(SessionType.UserAgent);
            SetupConversation(conversation);
            SetupSession(session);
            return (conversation, session);
        }
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T> where T : class, new()
    {
        public TestOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
