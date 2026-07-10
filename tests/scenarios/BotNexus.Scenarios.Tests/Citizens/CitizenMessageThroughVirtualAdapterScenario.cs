using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using BotNexus.Scenarios.Harness;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Scenarios.Tests.Citizens;

/// <summary>
/// Spec for the minimal citizen → router → conversation/session loop driven through the
/// <see cref="VirtualChannelAdapter"/>. This is the smallest possible "scenario" that proves
/// the suite delivers behavioural value on top of the conformance tests: a citizen message
/// arriving on a virtual channel ends up creating a real <see cref="Conversation"/> and
/// <see cref="Session"/> via the production <see cref="DefaultConversationRouter"/>.
/// </summary>
/// <remarks>
/// This scenario deliberately bypasses <c>GatewayHost</c> / <c>IAgentSupervisor</c> /
/// <c>LlmClient</c> — those land with the <c>VirtualWorld</c> harness in a follow-up PR.
/// What it locks down today is the contract between the inbound channel surface and the
/// router/store layer, so future PRs that change session shape, binding shape, or routing
/// semantics have to acknowledge this behaviour and update the scenario explicitly.
/// </remarks>
public sealed class CitizenMessageThroughVirtualAdapterScenario
{
    [Fact]
    public async Task CitizenMessage_ThroughVirtualAdapter_CreatesConversationAndSession_BoundToTheVirtualChannel()
    {
        // Arrange — wire real router + real in-memory stores against a virtual channel.
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var router = new DefaultConversationRouter(
            conversationStore,
            sessionStore,
            NullLogger<DefaultConversationRouter>.Instance);

        var agentId = AgentId.From("scenario-agent");
        var adapter = new VirtualChannelAdapter();
        var dispatcher = new RouterDispatcher(router, agentId);
        await adapter.StartAsync(dispatcher);

        var inbound = new InboundMessage
        {
            ChannelType = ChannelKey.From(VirtualChannelAdapter.VirtualChannelType),
            SenderId = "alice",
            Sender = CitizenId.Of(UserId.From("alice")),
            ChannelAddress = ChannelAddress.From("virtual:alice"),
            Content = "hello agent",
        };

        // Act — drive the inbound through the adapter just like a real channel would.
        await adapter.SimulateInboundAsync(inbound);

        // Assert — the router created one conversation for this agent and bound it to the
        // (channel, address) pair the virtual adapter carried.
        adapter.InboundDispatchCount.ShouldBe(1);

        var conversations = await conversationStore.ListAsync(agentId);
        var conversation = conversations.ShouldHaveSingleItem();
        conversation.AgentId.ShouldBe(agentId);
        conversation.ActiveSessionId.ShouldNotBeNull();

        conversation.ChannelBindings.ShouldHaveSingleItem();
        var binding = conversation.ChannelBindings[0];
        binding.ChannelType.ShouldBe(ChannelKey.From(VirtualChannelAdapter.VirtualChannelType));
        binding.ChannelAddress.ShouldBe(ChannelAddress.From("virtual:alice"));

        // And the session the router created is reachable from the session store and
        // points back at the conversation that owns it.
        var session = await sessionStore.GetAsync(conversation.ActiveSessionId!.Value);
        session.ShouldNotBeNull();
        session!.AgentId.ShouldBe(agentId);
        session.Session.ConversationId.ShouldBe(conversation.ConversationId);

        // Routing decision was the result of the dispatcher actually hitting the router.
        dispatcher.LastResult.ShouldNotBeNull();
        dispatcher.LastResult!.IsNewSession.ShouldBeTrue();
        dispatcher.LastResult.Conversation.ConversationId.ShouldBe(conversation.ConversationId);
    }

    /// <summary>
    /// Minimal <see cref="IChannelDispatcher"/> that forwards inbound messages straight into
    /// the production router. This is the seam the future <c>VirtualWorld</c> harness will
    /// replace with the real <c>GatewayHost</c> dispatcher; until then it gives scenario
    /// tests an honest path from "virtual channel inbound" to "router + store" without
    /// needing the full hosted runtime.
    /// </summary>
    private sealed class RouterDispatcher : IChannelDispatcher
    {
        private readonly IConversationRouter _router;
        private readonly AgentId _agentId;

        public RouterDispatcher(IConversationRouter router, AgentId agentId)
        {
            _router = router;
            _agentId = agentId;
        }

        public ConversationRoutingResult? LastResult { get; private set; }

        public async Task DispatchAsync(InboundMessage message, CancellationToken cancellationToken = default)
        {
            LastResult = await _router.ResolveInboundAsync(
                _agentId,
                message.ChannelType,
                message.ChannelAddress,
                message.RoutingHints?.RequestedConversationId,
                cancellationToken);
        }
    }
}
