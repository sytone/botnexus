using System.Text.RegularExpressions;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Gateway.Api.Extensions;
using BotNexus.Gateway.Api.Triggers;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.DependencyInjection;
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

    // ─── #3543: the flush must not masquerade as a cron run ────────────────────

    /// <summary>
    /// #3543 AC4/AC5 — the non-vacuity anchor for this issue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pre-fix suite mocked a trigger reporting <see cref="TriggerType.Memory"/> and therefore
    /// only ever exercised a branch that could not occur in production: no memory trigger existed,
    /// so <c>ResolveTrigger</c> always fell through to <c>CronTrigger</c>, and every <c>/reset</c>
    /// flush was stamped with a malformed jobless <c>cron:&lt;timestamp&gt;:&lt;guid&gt;</c> session
    /// id — 305 such rows accumulated in the live store.
    /// </para>
    /// <para>
    /// This test therefore refuses to invent its trigger set. It resolves the triggers from the
    /// PRODUCTION registration seam <c>AddBotNexusGatewayApi</c>, so removing the
    /// <c>MemoryTrigger</c> registration, or reinstating the cron fallback in
    /// <c>SessionEndMemoryFlusher.ResolveTrigger</c>, reddens this test by name rather than being
    /// absorbed by a hand-written mock.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task FlushAsync_WithProductionRegisteredTriggers_DoesNotProduceACronSessionId()
    {
        SessionId? createdSessionId = null;
        var triggers = ResolveProductionInternalTriggers(sid => createdSessionId = sid);

        // Guard: the production seam really does register the cron trigger this test exists to
        // discriminate against. An empty or cron-less set would make the assertion vacuous.
        triggers.ShouldContain(t => t.Type.Equals(TriggerType.Cron),
            "the production registration must still contain CronTrigger, or this test proves nothing");

        triggers.ShouldContain(t => t.Type.Equals(TriggerType.Memory),
            "#3543 AC2: a TriggerType.Memory trigger must be registered in " +
            "GatewayApiServiceCollectionExtensions. Without it SessionEndMemoryFlusher resolves " +
            "nothing and the flush is silently skipped (or, pre-fix, executed as a cron run).");

        var session = BuildInteractiveSessionWithUserTurn();
        session.ConversationId = ConversationId.From("c_human");

        var flusher = CreateFlusher(triggers.ToArray());
        await flusher.FlushAsync(TestAgent, session, EnabledOptions());

        createdSessionId.ShouldNotBeNull(
            "the flush must actually create a session through the resolved trigger; a null here " +
            "means ResolveTrigger matched nothing and the flush was silently skipped.");

        var value = createdSessionId!.Value.Value;

        // AC1/AC3: never the malformed jobless cron shape…
        Regex.IsMatch(value, @"^cron:\d+:").ShouldBeFalse(
            $"#3543 AC1: a session-end memory flush must not be stamped with a jobless cron session " +
            $"id. Got '{value}'. That three-segment cron:<timestamp>:<guid> shape rendered as cron " +
            "poisoning inside human conversations and cron's job-scoped scans can never claim it.");

        // …and not the cron namespace at all.
        value.StartsWith("cron:", StringComparison.Ordinal).ShouldBeFalse(
            "#3543 AC1: the memory flush must carry a distinct, non-cron session-id shape.");
        value.StartsWith("memory:", StringComparison.Ordinal).ShouldBeTrue(
            "#3543 AC1: the flush session id carries the memory namespace so it is attributable to " +
            "the subsystem that actually produced it.");
    }

    /// <summary>
    /// #3543 AC2, sad path: with no memory trigger present the flush is skipped rather than
    /// borrowing <c>CronTrigger</c>. Pre-fix this exact configuration was the only one that ever
    /// occurred in production, and it silently produced a cron session.
    /// </summary>
    [Fact]
    public async Task FlushAsync_WithOnlyCronTriggerRegistered_SkipsRatherThanFallingBackToCron()
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
            Times.Never,
            "#3543 AC2: the cron fallback is deleted. A memory flush is not a cron run, and " +
            "borrowing CronTrigger to perform one is what stamped 305 malformed cron: session ids " +
            "onto human conversations. With no memory trigger the flush must be an honest no-op.");
    }

    /// <summary>
    /// #3543 AC3: <c>CronTrigger</c> itself now refuses to mint a jobless session id, so a future
    /// caller cannot quietly re-accumulate sessions that belong to no job.
    /// </summary>
    [Fact]
    public async Task CronTrigger_WithNoJobId_RefusesToMintAJoblessSessionId()
    {
        var sessionStore = new Mock<ISessionStore>();
        var conversationStore = new Mock<IConversationStore>();
        var supervisor = new Mock<IAgentSupervisor>();
        var trigger = new CronTrigger(
            supervisor.Object, conversationStore.Object, sessionStore.Object, NullLogger<CronTrigger>.Instance);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => trigger.CreateSessionAsync(
            TestAgent, "flush your memory", request: new InternalTriggerRequest()));

        ex.Message.ShouldContain("CronJobId");

        // The rejection happens before any conversation is minted, so a refused call cannot leave
        // an orphan conversation behind.
        conversationStore.Verify(
            s => s.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the <see cref="IInternalTrigger"/> set exactly as production does, by running the
    /// real <c>AddBotNexusGatewayApi</c> registration over mocked infrastructure. The trigger set is
    /// therefore whatever the product registers — not a list this test maintains, which is the
    /// property that makes <see cref="FlushAsync_WithProductionRegisteredTriggers_DoesNotProduceACronSessionId"/>
    /// non-vacuous.
    /// </summary>
    private static IReadOnlyList<IInternalTrigger> ResolveProductionInternalTriggers(
        Action<SessionId> onSessionCreated)
    {
        var sessionStore = new Mock<ISessionStore>();
        sessionStore
            .Setup(s => s.GetOrCreateAsync(It.IsAny<SessionId>(), It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
            .Returns<SessionId, AgentId, CancellationToken>((sid, aid, _) =>
            {
                onSessionCreated(sid);
                return Task.FromResult(new GatewaySession { SessionId = sid, AgentId = aid });
            });
        sessionStore
            .Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        sessionStore
            .Setup(s => s.ListAsync(It.IsAny<AgentId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<GatewaySession>());

        var conversationStore = new Mock<IConversationStore>();
        conversationStore
            .Setup(s => s.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .Returns<Conversation, CancellationToken>((c, _) => Task.FromResult(c));
        conversationStore
            .Setup(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handle = new Mock<IAgentHandle>();
        handle
            .Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "memory written" });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var registry = new Mock<IAgentRegistry>();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(sessionStore.Object);
        services.AddSingleton(conversationStore.Object);
        services.AddSingleton(supervisor.Object);
        services.AddSingleton(registry.Object);
        services.AddBotNexusGatewayApi();

        return services.BuildServiceProvider().GetServices<IInternalTrigger>().ToList();
    }

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
