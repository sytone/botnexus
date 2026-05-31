using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

public sealed class SessionEndMemoryFlusherTests
{
    private static readonly AgentId TestAgent = AgentId.From("agent-a");

    // ─── ShouldFlush ───────────────────────────────────────────────────────────

    [Fact]
    public void ShouldFlush_WhenEnabled_InteractiveSession_WithUserTurns_ReturnsTrue()
    {
        var session = BuildInteractiveSessionWithUserTurn();
        var flusher = CreateFlusher();
        var options = EnabledOptions();

        flusher.ShouldFlush(session, options).ShouldBeTrue();
    }

    [Fact]
    public void ShouldFlush_WhenDisabled_ReturnsFalse()
    {
        var session = BuildInteractiveSessionWithUserTurn();
        var flusher = CreateFlusher();
        var options = new CompactionOptions { MemoryFlush = new MemoryFlushOptions { Enabled = false } };

        flusher.ShouldFlush(session, options).ShouldBeFalse();
    }

    [Fact]
    public void ShouldFlush_WhenNonInteractiveSession_ReturnsFalse()
    {
        // P9-E (#645): SessionType.Heartbeat is gone; heartbeat sessions now carry
        // SessionType.AgentSelf which is still classified as non-interactive by
        // Session.IsInteractive. Migrated to AgentSelf to verify the same gate.
        var session = BuildSession(SessionType.AgentSelf);
        session.History.Add(new SessionEntry { Role = MessageRole.User, Content = "hello" });
        var flusher = CreateFlusher();
        var options = EnabledOptions();

        flusher.ShouldFlush(session, options).ShouldBeFalse();
    }

    [Fact]
    public void ShouldFlush_WhenUserAgentSessionDeliveredViaCronChannel_ReturnsFalse()
    {
        // P9-E (#645): cron sessions are now SessionType.UserAgent (proxy for the
        // citizen who scheduled the job). The "cron" channel keeps them out of the
        // interactive memory-flush path via Session.IsInteractive's channel exclusion.
        var session = BuildSession(SessionType.UserAgent);
        session.ChannelType = ChannelKey.From("cron");
        session.History.Add(new SessionEntry { Role = MessageRole.User, Content = "hello" });
        var flusher = CreateFlusher();
        var options = EnabledOptions();

        flusher.ShouldFlush(session, options).ShouldBeFalse();
    }

    [Fact]
    public void ShouldFlush_WhenNoUserTurns_ReturnsFalse()
    {
        var session = BuildInteractiveSession();
        // No user turns added
        var flusher = CreateFlusher();
        var options = EnabledOptions();

        flusher.ShouldFlush(session, options).ShouldBeFalse();
    }

    // ─── FlushAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task FlushAsync_WithMemoryTrigger_CallsCreateSessionWithSessionEndPrompt()
    {
        var session = BuildInteractiveSessionWithUserTurn();
        var options = EnabledOptions();
        var triggerMock = new Mock<IInternalTrigger>();
        triggerMock.Setup(t => t.Type).Returns(TriggerType.Memory);
        triggerMock
            .Setup(t => t.CreateSessionAsync(
                TestAgent,
                options.MemoryFlush.SessionEndPromptText,
                It.IsAny<CancellationToken>(),
                It.IsAny<InternalTriggerRequest?>()))
            .ReturnsAsync(SessionId.Create());

        var flusher = CreateFlusher(triggerMock.Object);
        await flusher.FlushAsync(TestAgent, session, options);

        triggerMock.Verify(t => t.CreateSessionAsync(
            TestAgent,
            options.MemoryFlush.SessionEndPromptText,
            It.IsAny<CancellationToken>(),
            It.IsAny<InternalTriggerRequest?>()),
            Times.Once);
    }

    [Fact]
    public async Task FlushAsync_WithNoTrigger_DoesNotThrow()
    {
        var session = BuildInteractiveSessionWithUserTurn();
        var options = EnabledOptions();
        var flusher = CreateFlusher(); // no triggers

        // Should not throw — just logs a warning
        await flusher.FlushAsync(TestAgent, session, options);
    }

    [Fact]
    public async Task FlushAsync_WhenTriggerThrows_DoesNotRethrow()
    {
        var session = BuildInteractiveSessionWithUserTurn();
        var options = EnabledOptions();
        var triggerMock = new Mock<IInternalTrigger>();
        triggerMock.Setup(t => t.Type).Returns(TriggerType.Memory);
        triggerMock
            .Setup(t => t.CreateSessionAsync(
                It.IsAny<AgentId>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<InternalTriggerRequest?>()))
            .ThrowsAsync(new InvalidOperationException("trigger exploded"));

        var flusher = CreateFlusher(triggerMock.Object);

        // Should not throw — flush is non-fatal
        await flusher.FlushAsync(TestAgent, session, options);
    }

    [Fact]
    public async Task FlushAsync_UsesCronTrigger_WhenNoMemoryTriggerRegistered()
    {
        var session = BuildInteractiveSessionWithUserTurn();
        var options = EnabledOptions();
        var cronTriggerMock = new Mock<IInternalTrigger>();
        cronTriggerMock.Setup(t => t.Type).Returns(TriggerType.Cron);
        cronTriggerMock
            .Setup(t => t.CreateSessionAsync(
                It.IsAny<AgentId>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<InternalTriggerRequest?>()))
            .ReturnsAsync(SessionId.Create());

        var flusher = CreateFlusher(cronTriggerMock.Object);
        await flusher.FlushAsync(TestAgent, session, options);

        cronTriggerMock.Verify(t => t.CreateSessionAsync(
            It.IsAny<AgentId>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<InternalTriggerRequest?>()),
            Times.Once);
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    private static Session BuildInteractiveSessionWithUserTurn()
    {
        var session = BuildInteractiveSession();
        session.History.Add(new SessionEntry { Role = MessageRole.User, Content = "hello" });
        return session;
    }

    private static Session BuildInteractiveSession() => BuildSession(SessionType.UserAgent);

    private static Session BuildSession(SessionType type)
    {
        return new Session
        {
            SessionId = SessionId.Create(),
            SessionType = type
        };
    }

    private static SessionEndMemoryFlusher CreateFlusher(params IInternalTrigger[] triggers)
        => new(triggers, NullLogger<SessionEndMemoryFlusher>.Instance);

    private static CompactionOptions EnabledOptions()
        => new() { MemoryFlush = new MemoryFlushOptions { Enabled = true } };
}
