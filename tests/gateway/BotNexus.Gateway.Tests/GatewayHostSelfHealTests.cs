using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Tests for the write-time self-heal of orphaned crash sentinels (#2030). When an inbound
/// message arrives for a session that carries a crash sentinel but has no live in-memory turn,
/// the sentinel is an orphan (a turn that died mid-flight) and is cleared at ingress so the
/// session unblocks the instant the user retries - no gateway restart. When a turn IS live the
/// sentinel is legitimate and must be left in place so the message keeps queuing behind it.
/// </summary>
public sealed partial class GatewayHostTests
{
    /// <summary>
    /// A tracker whose live-turn answer is fixed, so tests can model "a turn is already
    /// executing for this session" vs. "no live turn" deterministically.
    /// </summary>
    private sealed class StubTurnTracker(bool hasLiveTurn) : ISessionTurnTracker
    {
        public IDisposable BeginTurn(string sessionId) => new NoopScope();

        public bool HasLiveTurn(string sessionId) => hasLiveTurn;

        private sealed class NoopScope : IDisposable
        {
            public void Dispose() { }
        }
    }

    [Fact]
    public async Task DispatchAsync_SentinelWithNoLiveTurn_SelfHealsAtIngressAndProceeds()
    {
        // Arrange: a session that already carries an orphaned crash sentinel and NO live turn.
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-heal"]);
        var handle = CreatePromptHandle("agent-heal", "session-heal", "healed-response");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
            AgentId.From("agent-heal"), SessionId.From("session-heal"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var session = new GatewaySession
        {
            SessionId = SessionId.From("session-heal"),
            AgentId = AgentId.From("agent-heal")
        };
        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.System,
            Content = "[agent turn in progress - gateway restarted if visible]",
            IsCrashSentinel = true
        });

        // Record whether a sentinel was still present at the FIRST SaveAsync - the write-ahead
        // user-message save that runs AFTER ingress self-heal but BEFORE PrepareTurnAsync writes
        // its own fresh sentinel. If self-heal fired, this observes zero sentinels.
        bool? sentinelPresentAtFirstSave = null;
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(
            SessionId.From("session-heal"), AgentId.From("agent-heal"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => sentinelPresentAtFirstSave ??= session.History.Any(e => e.IsCrashSentinel));

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions.Object, new RecordingActivityBroadcaster(),
            CreateChannelManager(channel.Object), turnTracker: new StubTurnTracker(hasLiveTurn: false));

        // Act
        await host.DispatchAsync(CreateMessage("retry please", sessionId: "session-heal"));

        // Assert: the orphaned sentinel was cleared at ingress (before the write-ahead save)...
        sentinelPresentAtFirstSave.ShouldBe(false,
            "orphaned sentinel must be self-healed at message ingress when no turn is live (#2030)");
        // ...and the message proceeded to completion with a delivered reply.
        channel.Verify(c => c.SendAsync(
            It.Is<OutboundMessage>(m => m.Content == "healed-response"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_SentinelWithLiveTurn_DoesNotSelfHeal()
    {
        // Arrange: a session with a sentinel AND a live turn already executing. The sentinel is
        // legitimate (it belongs to the in-flight turn) and must NOT be cleared at ingress.
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-live"]);
        var handle = CreatePromptHandle("agent-live", "session-live", "response");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
            AgentId.From("agent-live"), SessionId.From("session-live"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var session = new GatewaySession
        {
            SessionId = SessionId.From("session-live"),
            AgentId = AgentId.From("agent-live")
        };
        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.System,
            Content = "[agent turn in progress - gateway restarted if visible]",
            IsCrashSentinel = true
        });

        bool? sentinelPresentAtFirstSave = null;
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(
            SessionId.From("session-live"), AgentId.From("agent-live"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => sentinelPresentAtFirstSave ??= session.History.Any(e => e.IsCrashSentinel));

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions.Object, new RecordingActivityBroadcaster(),
            CreateChannelManager(channel.Object), turnTracker: new StubTurnTracker(hasLiveTurn: true));

        // Act
        await host.DispatchAsync(CreateMessage("concurrent message", sessionId: "session-live"));

        // Assert: the sentinel was NOT cleared at ingress because a turn is live - it survives
        // to the write-ahead save, so the message legitimately queues behind the in-flight turn.
        sentinelPresentAtFirstSave.ShouldBe(true,
            "sentinel must be preserved at ingress when a turn is live so the message keeps queuing (#2030)");
    }
}
