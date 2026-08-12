using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BotNexus.Gateway.Tests.Controllers;

/// <summary>
/// Covers <c>POST /api/agents/{agentId}/conversations/{conversationId}/messages</c> (issue #2840) —
/// the first-class HTTP door onto the conversation-addressed inbound path that previously only
/// <c>WebhookInboundController</c> held.
/// </summary>
/// <remarks>
/// The conversation router / dispatcher / session store are REAL here, so the conversation-to-session
/// binding is exercised end to end rather than asserted against a mock's recorded arguments. Only the
/// orchestrator is substituted, because executing a real agent turn is out of scope for a controller
/// test — but the substitute captures the <see cref="InboundMessage"/> so the routing-hint contract
/// (acceptance clause 1) is pinned on the real object the controller builds.
/// </remarks>
public sealed class ConversationMessagesControllerTests
{
    private const string AgentSlug = "pr-doctor";

    private readonly InMemoryConversationStore _conversations = new();
    private readonly InMemorySessionStore _sessions = new();
    private readonly IConversationDispatcher _dispatcher;
    private readonly IInboundMessageOrchestrator _orchestrator = Substitute.For<IInboundMessageOrchestrator>();
    private readonly StubAgentRegistry _agents = new();

    /// <summary>Agent ids the stub registry reports as registered.</summary>
    private HashSet<string> _knownAgents => _agents.Registered;

    /// <summary>Completes when the controller hands a message to the orchestrator.</summary>
    private readonly TaskCompletionSource<InboundMessage> _accepted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConversationMessagesControllerTests()
    {
        var router = new DefaultConversationRouter(
            _conversations, _sessions, NullLogger<DefaultConversationRouter>.Instance);
        _dispatcher = new DefaultConversationDispatcher(router, _conversations);

        // A hand-written stub rather than a substitute: IAgentRegistry.Contains takes a Vogen value
        // object, and an Arg.Any<AgentId>() spec against it is left unbound (NSubstitute reports
        // "Remaining (non-bound) argument specifications: any AgentId") and then explodes as a
        // RedundantArgumentMatcherException on an unrelated later interaction - failing the whole
        // fixture in the constructor with a message that names none of the real cause.
        _knownAgents.Add(AgentSlug);

        _orchestrator.AcceptAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _accepted.TrySetResult(call.Arg<InboundMessage>());
                return Task.FromResult(InboundDispatchResult.NoRoute());
            });
    }

    // ── clause 1: delivers through the orchestrator with the route conversation id ─────────────

    /// <summary>
    /// Acceptance clause 1: with <c>wake:true</c> the message must reach
    /// <see cref="IInboundMessageOrchestrator.AcceptAsync"/> carrying
    /// <c>RoutingHints.RequestedConversationId</c> = the route conversation and
    /// <c>RoutingHints.RequestedAgentId</c> = the route agent.
    /// </summary>
    [Fact]
    public async Task Post_WithWake_DeliversThroughOrchestratorWithConversationRoutingHint()
    {
        var conversation = await CreateConversationAsync();
        var controller = CreateController();

        var result = await controller.PostMessage(
            AgentSlug, conversation.ConversationId.Value,
            new PostConversationMessageRequest("PR #123 has a failing check."),
            CancellationToken.None);

        result.ShouldBeOfType<AcceptedResult>();

        var inbound = await AwaitAcceptedAsync();
        inbound.RoutingHints.ShouldNotBeNull();
        inbound.RoutingHints!.RequestedConversationId.ShouldBe(conversation.ConversationId);
        inbound.RoutingHints.RequestedAgentId.ShouldBe(BotNexus.Domain.Primitives.AgentId.From(AgentSlug));
        inbound.Content.ShouldBe("PR #123 has a failing check.");
    }

    // ── clause 2: lands in the EXISTING conversation's session, not a fresh one ────────────────

    /// <summary>
    /// Acceptance clause 2: the returned session id must be the session already bound to the
    /// conversation, not a newly minted one — and that same conversation must be what the
    /// orchestrator was asked to deliver into. Asserting both halves is what makes clause 10
    /// hold: deleting the orchestrator call reddens this test as well as the clause-1 test.
    /// </summary>
    [Fact]
    public async Task Post_ReturnsTheConversationsBoundSession_AndDeliversIntoThatConversation()
    {
        var conversation = await CreateConversationAsync();
        var controller = CreateController();

        var body = await PostAcceptedAsync(controller, conversation.ConversationId.Value, "hello");

        var reloaded = await _conversations.GetAsync(conversation.ConversationId);
        reloaded.ShouldNotBeNull();
        reloaded!.ActiveSessionId.ShouldNotBeNull();

        // The receipt names the conversation's OWN session — no orphan.
        body.SessionId.ShouldBe(reloaded.ActiveSessionId!.Value.Value);
        body.ConversationId.ShouldBe(conversation.ConversationId.Value);

        var boundSession = await _sessions.GetAsync(reloaded.ActiveSessionId!.Value);
        boundSession.ShouldNotBeNull();
        boundSession!.ConversationId.ShouldBe(conversation.ConversationId);

        // ...and the orchestrator was asked to deliver into that same conversation.
        var inbound = await AwaitAcceptedAsync();
        inbound.RoutingHints!.RequestedConversationId.ShouldBe(conversation.ConversationId);

        // Exactly one session exists overall: nothing was minted on the side.
        (await _sessions.ListAsync()).Count.ShouldBe(1);
    }

    /// <summary>
    /// Acceptance clause 2 (repeat caller): successive posts must continue the same thread rather
    /// than accumulating a session per call — the orphan-accumulation failure mode that
    /// <c>POST /api/chat</c> has and that this endpoint exists to avoid.
    /// </summary>
    [Fact]
    public async Task Post_SuccessiveCalls_ReuseTheSameSession()
    {
        var conversation = await CreateConversationAsync();
        var controller = CreateController();

        var first = await PostAcceptedAsync(controller, conversation.ConversationId.Value, "one", wake: false);
        var second = await PostAcceptedAsync(controller, conversation.ConversationId.Value, "two", wake: false);

        second.SessionId.ShouldBe(first.SessionId);
        (await _sessions.ListAsync()).Count.ShouldBe(1);
    }

    // ── clause 3: wake:false appends without executing a turn ──────────────────────────────────

    /// <summary>
    /// Acceptance clause 3, first half: with <c>wake:false</c> the message is durably persisted on
    /// the conversation's bound session and is therefore visible to
    /// <c>GET /api/conversations/{id}/history</c>, which reads that same session.
    /// </summary>
    [Fact]
    public async Task Post_WithoutWake_PersistsMessageOnConversationSession()
    {
        var conversation = await CreateConversationAsync();
        var controller = CreateController();

        var body = await PostAcceptedAsync(controller, conversation.ConversationId.Value, "audit note", wake: false);

        var session = await _sessions.GetAsync(SessionId.From(body.SessionId));
        session.ShouldNotBeNull();
        session!.ConversationId.ShouldBe(conversation.ConversationId);

        var userEntries = session.GetHistorySnapshot().Where(e => e.Role == MessageRole.User).ToList();
        userEntries.Count.ShouldBe(1);
        userEntries[0].Content.ShouldBe("audit note");
    }

    /// <summary>
    /// Acceptance clause 3, second half: <c>wake:false</c> must NOT execute an agent turn. The
    /// orchestrator is the only path to a turn, so never touching it is the assertion.
    /// </summary>
    [Fact]
    public async Task Post_WithoutWake_DoesNotExecuteAnAgentTurn()
    {
        var conversation = await CreateConversationAsync();
        var controller = CreateController();

        await PostAcceptedAsync(controller, conversation.ConversationId.Value, "audit note", wake: false);

        // Give a detached wake task every chance to have run before asserting it never started.
        await Task.Delay(50);

        await _orchestrator.DidNotReceive()
            .AcceptAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>());
        _orchestrator.DidNotReceive().Post(Arg.Any<InboundMessage>());
        _accepted.Task.IsCompleted.ShouldBeFalse();
    }

    // ── clause 4: 404 vs 400 are distinguishable ───────────────────────────────────────────────

    /// <summary>Acceptance clause 4: an unknown agent is 404, not 400 and not a silent 202.</summary>
    [Fact]
    public async Task Post_UnknownAgent_Returns404()
    {
        var conversation = await CreateConversationAsync();
        var controller = CreateController();

        var result = await controller.PostMessage(
            "no-such-agent", conversation.ConversationId.Value,
            new PostConversationMessageRequest("hi"), CancellationToken.None);

        result.ShouldBeOfType<NotFoundObjectResult>();
    }

    /// <summary>Acceptance clause 4: an unknown conversation is 404.</summary>
    [Fact]
    public async Task Post_UnknownConversation_Returns404()
    {
        var controller = CreateController();

        var result = await controller.PostMessage(
            AgentSlug, "c_does-not-exist",
            new PostConversationMessageRequest("hi"), CancellationToken.None);

        result.ShouldBeOfType<NotFoundObjectResult>();
    }

    /// <summary>
    /// Acceptance clause 4: a conversation that exists but belongs to a DIFFERENT agent is 404 —
    /// the route pair must be coherent, and leaking "exists, but not yours" as a 200/400 would let
    /// a caller enumerate other agents' conversation ids.
    /// </summary>
    [Fact]
    public async Task Post_ConversationOwnedByAnotherAgent_Returns404()
    {
        var otherAgent = BotNexus.Domain.Primitives.AgentId.From("someone-else");
        _knownAgents.Add("someone-else");
        var conversation = await CreateConversationAsync(otherAgent);
        var controller = CreateController();

        var result = await controller.PostMessage(
            AgentSlug, conversation.ConversationId.Value,
            new PostConversationMessageRequest("hi"), CancellationToken.None);

        result.ShouldBeOfType<NotFoundObjectResult>();
    }

    /// <summary>
    /// Acceptance clause 4: a malformed body (missing/blank message) is 400, which the caller can
    /// distinguish from the 404s above. A blank message must not be delivered as an empty turn.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Post_MissingOrBlankMessage_Returns400(string? message)
    {
        var conversation = await CreateConversationAsync();
        var controller = CreateController();

        var result = await controller.PostMessage(
            AgentSlug, conversation.ConversationId.Value,
            new PostConversationMessageRequest(message), CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    /// <summary>Acceptance clause 4: a completely absent body is 400, not a NullReferenceException.</summary>
    [Fact]
    public async Task Post_NullBody_Returns400()
    {
        var conversation = await CreateConversationAsync();
        var controller = CreateController();

        var result = await controller.PostMessage(
            AgentSlug, conversation.ConversationId.Value, request: null, CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    // ── clause 7: 202 carrying the resolved identifiers ────────────────────────────────────────

    /// <summary>
    /// Acceptance clause 7: the success shape is <c>202 Accepted</c> carrying the resolved
    /// conversation and session ids, and echoing which mode ran.
    /// </summary>
    [Fact]
    public async Task Post_Returns202WithResolvedIdentifiers()
    {
        var conversation = await CreateConversationAsync();
        var controller = CreateController();

        var result = await controller.PostMessage(
            AgentSlug, conversation.ConversationId.Value,
            new PostConversationMessageRequest("go"), CancellationToken.None);

        var accepted = result.ShouldBeOfType<AcceptedResult>();
        var body = accepted.Value.ShouldBeOfType<PostConversationMessageResponse>();
        body.ConversationId.ShouldBe(conversation.ConversationId.Value);
        body.SessionId.ShouldNotBeNullOrWhiteSpace();
        body.Wake.ShouldBeTrue();
    }

    // ── clause 8: provenance ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Acceptance clause 8: an explicit <c>sender</c> is stamped onto the persisted transcript entry
    /// so the message's origin is visible in conversation history rather than appearing from nowhere.
    /// </summary>
    [Fact]
    public async Task Post_WithoutWake_RecordsSenderProvenanceOnTheEntry()
    {
        var conversation = await CreateConversationAsync();
        var controller = CreateController();

        var body = await PostAcceptedAsync(
            controller, conversation.ConversationId.Value, "PR #123 failed", wake: false, sender: "cron:pr-doctor");

        var session = await _sessions.GetAsync(SessionId.From(body.SessionId));
        var entry = session!.GetHistorySnapshot().Single(e => e.Role == MessageRole.User);
        entry.SenderId.ShouldBe("api:cron:pr-doctor");
    }

    /// <summary>
    /// Acceptance clause 8: the same provenance travels on the inbound message for the wake path, so
    /// the turn the agent takes is attributable to the calling script too.
    /// </summary>
    [Fact]
    public async Task Post_WithWake_CarriesSenderProvenanceOnTheInboundMessage()
    {
        var conversation = await CreateConversationAsync();
        var controller = CreateController();

        await PostAcceptedAsync(controller, conversation.ConversationId.Value, "go", sender: "cron:pr-doctor");

        var inbound = await AwaitAcceptedAsync();
        inbound.SenderId.ShouldBe("api:cron:pr-doctor");
        inbound.ChannelType.ShouldBe(ChannelKey.From("api"));
    }

    /// <summary>
    /// Acceptance clause 8: omitting <c>sender</c> still records a non-empty, unambiguous origin.
    /// "Came in over the API" is weaker provenance than a named caller, but it is not nothing.
    /// </summary>
    [Fact]
    public async Task Post_WithoutSender_StillRecordsAnApiOrigin()
    {
        var conversation = await CreateConversationAsync();
        var controller = CreateController();

        var body = await PostAcceptedAsync(controller, conversation.ConversationId.Value, "go", wake: false);

        var session = await _sessions.GetAsync(SessionId.From(body.SessionId));
        var entry = session!.GetHistorySnapshot().Single(e => e.Role == MessageRole.User);
        entry.SenderId.ShouldBe("api");
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────

    private async Task<InboundMessage> AwaitAcceptedAsync()
    {
        var completed = await Task.WhenAny(_accepted.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.ShouldBe(_accepted.Task, "the controller never handed the message to the orchestrator");
        return await _accepted.Task;
    }

    private async Task<PostConversationMessageResponse> PostAcceptedAsync(
        ConversationMessagesController controller,
        string conversationId,
        string message,
        bool wake = true,
        string? sender = null)
    {
        var result = await controller.PostMessage(
            AgentSlug, conversationId,
            new PostConversationMessageRequest(message, wake, sender),
            CancellationToken.None);

        var accepted = result.ShouldBeOfType<AcceptedResult>();
        return accepted.Value.ShouldBeOfType<PostConversationMessageResponse>();
    }

    private async Task<Conversation> CreateConversationAsync(AgentId? owner = null)
    {
        var agentId = owner ?? BotNexus.Domain.Primitives.AgentId.From(AgentSlug);
        var conversation = ConversationFactory.CreateForAgent(
            ConversationKind.HumanAgent,
            ConversationId.Create(),
            agentId,
            title: "Existing thread",
            initiator: CitizenId.Of(agentId));
        return await _conversations.CreateAsync(conversation);
    }

    private ConversationMessagesController CreateController() =>
        new(_conversations,
            _sessions,
            _dispatcher,
            _orchestrator,
            _agents,
            NullLogger<ConversationMessagesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    /// <summary>
    /// Minimal <see cref="IAgentRegistry"/> whose only meaningful member is <c>Contains</c> - the one
    /// the controller uses to tell an unknown agent (404) from a malformed body (400).
    /// </summary>
    private sealed class StubAgentRegistry : IAgentRegistry
    {
        public HashSet<string> Registered { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Contains(AgentId agentId) => Registered.Contains(agentId.Value);

        public AgentDescriptor? Get(AgentId agentId) => null;

        public IReadOnlyList<AgentDescriptor> GetAll() => [];

        public void Register(AgentDescriptor descriptor) => Registered.Add(descriptor.AgentId.Value);

        public void Unregister(AgentId agentId) => Registered.Remove(agentId.Value);
    }
}
