using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Phase 9 / P9-C contract pins on <see cref="AgentExchangeService"/>:
/// local A↔A conversations auto-archive when the exchange terminates (driven by
/// W-3 directive — "A-A conversations should have an end and then the conversation
/// is done as that topic of conversation is over"). Cross-world sender behaviour
/// is exercised end-to-end in
/// <c>CrossWorldFederationControllerTests</c> via the receiver pins; this file
/// focuses on the local single-process <c>ConverseAsync</c> path.
/// </summary>
public sealed class AgentExchangeArchiveTests
{
    [Fact]
    public async Task ConverseAsync_OnSuccessfulExchangeFinish_ArchivesConversation()
    {
        // The target invokes finish_agent_exchange on turn 2 (simulated by writing the
        // matching finishedAgentExchangeId payload + surfacing the tool-call entry).
        var (service, conversationStore, _) = BuildExchangeWithFinishOnSecondTurn();

        var result = await service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = AgentId.From("initiator-agent"),
            TargetId = AgentId.From("target-agent"),
            Message = "Solve this",
            Objective = "ship it",
            MaxTurns = 3
        });

        result.CompletionReason.ShouldBe("exchangeFinished");

        var conv = await conversationStore.GetAsync(result.ConversationId);
        conv.ShouldNotBeNull();
        conv!.Status.ShouldBe(ConversationStatus.Archived,
            customMessage: "P9-C: ConverseAsync MUST archive the A↔A conversation when the " +
                "exchange terminates normally. A↔A conversations are bounded by their exchange — " +
                "if left Active they accumulate forever in portal/list APIs.");
        conv.ActiveSessionId.ShouldBeNull(
            customMessage: "ArchiveAsync atomically clears ActiveSessionId.");
    }

    [Fact]
    public async Task ConverseAsync_SingleShotNoObjective_ArchivesConversation()
    {
        // Single-shot: no Objective + MaxTurns=1 → one prompt, no finish-tool needed,
        // CompletionReason becomes "singleShot" / "objectiveMet" via ResolveCompletionReason.
        var (service, conversationStore, _, _) = BuildService();

        var result = await service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = AgentId.From("initiator-agent"),
            TargetId = AgentId.From("target-agent"),
            Message = "Do a thing",
            MaxTurns = 1
        });

        var conv = await conversationStore.GetAsync(result.ConversationId);
        conv.ShouldNotBeNull();
        conv!.Status.ShouldBe(ConversationStatus.Archived,
            customMessage: "P9-C: single-shot exchanges (no Objective, one prompt) MUST also " +
                "archive — they're as terminal as exchange-finished. Without this, every " +
                "AgentConverseTool invocation leaves an Active stub conversation.");
    }

    [Fact]
    public async Task ConverseAsync_MaxTurnsReachedWithoutFinish_ArchivesConversation()
    {
        // Max turns reached without the target ever invoking finish_agent_exchange.
        // CompletionReason becomes "maxTurnsReached" — exchange still terminal, conversation
        // still must archive (W-3 — the exchange ended, the topic is done).
        var (service, conversationStore, _, _) = BuildService(initialReply: "still working");

        var result = await service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = AgentId.From("initiator-agent"),
            TargetId = AgentId.From("target-agent"),
            Message = "tell me a long story",
            Objective = "narrate something",
            MaxTurns = 3
        });

        result.CompletionReason.ShouldBe("maxTurnsReached");

        var conv = await conversationStore.GetAsync(result.ConversationId);
        conv.ShouldNotBeNull();
        conv!.Status.ShouldBe(ConversationStatus.Archived,
            customMessage: "P9-C: MaxTurns-reached is a terminal exchange end — conversation " +
                "must archive. Without this, abandoned exchanges accumulate as Active forever.");
    }

    [Fact]
    public async Task ConverseAsync_OnPromptFailure_ArchivesConversation()
    {
        // Failure path: target prompt throws; error catch seals session AND must archive.
        var sessionStore = new InMemorySessionStore();
        var conversationStore = new InMemoryConversationStore();
        var initiator = AgentId.From("initiator-agent");
        var target = AgentId.From("target-agent");
        var registry = CreateRegistry(initiator, target);

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("LLM upstream went bang"));
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            sessionStore,
            conversationStore,
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance);

        var action = () => service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "trigger failure",
            MaxTurns = 3
        });
        await action.ShouldThrowAsync<InvalidOperationException>();

        var conv = (await conversationStore.ListAsync()).ShouldHaveSingleItem();
        conv.Status.ShouldBe(ConversationStatus.Archived,
            customMessage: "P9-C: error catch path MUST archive — terminal failure is still " +
                "a terminal exchange end. Leaving the conversation Active after a failure " +
                "would show it as 'in flight' indefinitely in portal/list APIs.");
    }

    [Fact]
    public async Task ConverseAsync_FailureDuringArchive_DoesNotPropagateException()
    {
        // The archive write is a derived-state side-effect; if it fails (e.g. transient DB issue)
        // the caller must still observe ConverseAsync as successful — the session is already
        // sealed and the conversation is still queryable through ListByConversationAsync.
        // Failure-isolation: ArchiveOnExchangeEndAsync swallows exceptions and logs a warning.
        var sessionStore = new InMemorySessionStore();
        var conversationStore = new ThrowingArchiveStore();
        var initiator = AgentId.From("initiator-agent");
        var target = AgentId.From("target-agent");
        var registry = CreateRegistry(initiator, target);

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "ok" });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            sessionStore,
            conversationStore,
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance);

        var result = await service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "Do a thing",
            MaxTurns = 1
        });

        // The exchange itself succeeded — caller does not see the archive failure as a top-level
        // ConverseAsync failure.
        result.ShouldNotBeNull();
        conversationStore.ArchiveCallCount.ShouldBe(1,
            customMessage: "ArchiveOnExchangeEndAsync was called exactly once and threw; the " +
                "exception was swallowed so ConverseAsync still returned its successful result.");

        // The session is still sealed regardless of the archive failure.
        var session = await sessionStore.GetAsync(result.SessionId);
        session.ShouldNotBeNull();
        session!.Status.ShouldBe(GatewaySessionStatus.Sealed);
    }

    [Fact]
    public async Task ConverseAsync_AfterArchive_DoesNotClobber_NewerOwnerConversation()
    {
        // Concurrent-actor pointer guard: if some other actor reassigns ActiveSessionId on this
        // conversation between our GetAsync and our ArchiveAsync, we must NOT clobber it. The
        // archive's strict pointer guard requires latest.ActiveSessionId == expectedSessionId,
        // and the failed exchange operates on its OWN conversation (CreateExchangeConversationAsync
        // mints one per call) so it can never archive a conversation it didn't create.
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var initiator = AgentId.From("initiator-agent");
        var target = AgentId.From("target-agent");
        var registry = CreateRegistry(initiator, target);

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "ok" });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            sessionStore,
            conversationStore,
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance);

        var firstResult = await service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "first",
            MaxTurns = 1
        });

        // First conversation should already be Archived (P9-C).
        var firstAfter = await conversationStore.GetAsync(firstResult.ConversationId);
        firstAfter!.Status.ShouldBe(ConversationStatus.Archived);

        // Simulate a hypothetical external mutator that resurrects the conversation by hand:
        // unarchive it AND reassign ActiveSessionId to a new owner. (This is the worst-case
        // shape — production code never does this, but it pins the contract.)
        var newerOwner = SessionId.Create();
        firstAfter.Status = ConversationStatus.Active;
        firstAfter.ActiveSessionId = newerOwner;
        await conversationStore.SaveAsync(firstAfter);

        // A second ConverseAsync mints its own NEW conversation. After it archives ITS OWN
        // conversation, the first conversation's manually-reassigned ActiveSessionId must remain
        // intact — the archive helper's strict pointer guard skips when ActiveSessionId no
        // longer equals the expected SessionId, AND the second call operates on a different
        // ConversationId entirely.
        var secondResult = await service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "second",
            MaxTurns = 1
        });

        secondResult.ConversationId.ShouldNotBe(firstResult.ConversationId,
            customMessage: "ConverseAsync must mint a fresh conversation per call.");

        var firstFinal = await conversationStore.GetAsync(firstResult.ConversationId);
        firstFinal!.ActiveSessionId.ShouldBe(newerOwner,
            customMessage: "P9-C strict pointer guard: a second ConverseAsync archives only " +
                "ITS OWN conversation; it must never touch a different conversation's pointer. " +
                "The manually-reassigned newerOwner on the first conversation must survive.");

        var secondConv = await conversationStore.GetAsync(secondResult.ConversationId);
        secondConv!.Status.ShouldBe(ConversationStatus.Archived,
            customMessage: "Second exchange archived its OWN conversation as expected.");
    }

    // ---- helpers ----

    private static (AgentExchangeService Service, InMemoryConversationStore ConversationStore,
        InMemorySessionStore SessionStore, Mock<IAgentSupervisor> Supervisor)
        BuildService(string initialReply = "ok")
    {
        var initiator = AgentId.From("initiator-agent");
        var target = AgentId.From("target-agent");
        var registry = CreateRegistry(initiator, target);
        var sessionStore = new InMemorySessionStore();
        var conversationStore = new InMemoryConversationStore();

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = initialReply });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            sessionStore,
            conversationStore,
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance);

        return (service, conversationStore, sessionStore, supervisor);
    }

    private static (AgentExchangeService Service, InMemoryConversationStore ConversationStore,
        InMemorySessionStore SessionStore)
        BuildExchangeWithFinishOnSecondTurn()
    {
        var initiator = AgentId.From("initiator-agent");
        var target = AgentId.From("target-agent");
        var registry = CreateRegistry(initiator, target);
        var sessionStore = new InMemorySessionStore();
        var conversationStore = new InMemoryConversationStore();

        SessionId? capturedSessionId = null;
        var turnCounter = 0;
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                turnCounter++;
                if (turnCounter == 1)
                    return new AgentResponse { Content = "still working" };

                // Simulate the finish_agent_exchange tool by writing the matching payload
                // BEFORE returning the tool-call entry — mirrors the real tool's behaviour.
                if (capturedSessionId is { } sid)
                {
                    var s = sessionStore.GetAsync(sid, CancellationToken.None).GetAwaiter().GetResult();
                    if (s is not null
                        && s.Metadata.TryGetValue(FinishAgentExchangeTool.ActiveExchangeIdKey, out var v)
                        && v is string activeId)
                    {
                        s.Metadata[FinishAgentExchangeTool.FinishedExchangeIdKey] = activeId;
                        s.Metadata[FinishAgentExchangeTool.FinishedReasonKey] = "shipped";
                        sessionStore.SaveAsync(s, CancellationToken.None).GetAwaiter().GetResult();
                    }
                }
                return new AgentResponse
                {
                    Content = "Done.",
                    ToolCalls = [new AgentToolCallInfo("call-1", "finish_agent_exchange", IsError: false)]
                };
            });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Callback<AgentId, SessionId, CancellationToken>((_, sid, _) => capturedSessionId = sid)
            .ReturnsAsync(handle.Object);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            sessionStore,
            conversationStore,
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance);

        return (service, conversationStore, sessionStore);
    }

    private static Mock<IAgentRegistry> CreateRegistry(AgentId initiator, AgentId target)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(initiator)).Returns(new AgentDescriptor
        {
            AgentId = initiator,
            DisplayName = initiator.Value,
            ApiProvider = "openai",
            ModelId = "gpt-test",
            SubAgentIds = [target.Value]
        });
        registry.Setup(r => r.Get(target)).Returns(new AgentDescriptor
        {
            AgentId = target,
            DisplayName = target.Value,
            ApiProvider = "openai",
            ModelId = "gpt-test"
        });
        registry.Setup(r => r.Contains(initiator)).Returns(true);
        registry.Setup(r => r.Contains(target)).Returns(true);
        return registry;
    }

    /// <summary>
    /// A conversation store that succeeds at everything except <see cref="ArchiveAsync"/>,
    /// which throws. Used to prove <c>ArchiveOnExchangeEndAsync</c> swallows archive
    /// failures (failure isolation: archive is derived state, must not propagate).
    /// </summary>
    private sealed class ThrowingArchiveStore : IConversationStore
    {
        private readonly InMemoryConversationStore _inner = new();
        public int ArchiveCallCount { get; private set; }

        public Task<Conversation?> GetAsync(ConversationId conversationId, CancellationToken ct = default)
            => _inner.GetAsync(conversationId, ct);
        public Task<IReadOnlyList<Conversation>> ListAsync(AgentId? agentId = null, CancellationToken ct = default)
            => _inner.ListAsync(agentId, ct);
        public Task<IReadOnlyList<Conversation>> ListForCitizenAsync(CitizenId citizen, CancellationToken ct = default)
            => _inner.ListForCitizenAsync(citizen, ct);
        public Task<Conversation> CreateAsync(Conversation conversation, CancellationToken ct = default)
            => _inner.CreateAsync(conversation, ct);
        public Task SaveAsync(Conversation conversation, CancellationToken ct = default)
            => _inner.SaveAsync(conversation, ct);
        public Task ArchiveAsync(ConversationId conversationId, CancellationToken ct = default)
        {
            ArchiveCallCount++;
            throw new InvalidOperationException("simulated archive failure");
        }
        public Task<Conversation?> ResolveByBindingAsync(AgentId agentId, ChannelKey channelType, ChannelAddress channelAddress, CancellationToken ct = default)
            => _inner.ResolveByBindingAsync(agentId, channelType, channelAddress, ct);
        public Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(AgentId? agentId = null, CancellationToken ct = default)
            => _inner.GetSummariesAsync(agentId, ct);
    }
}
