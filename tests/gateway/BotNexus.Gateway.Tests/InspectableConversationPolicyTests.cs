using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using BotNexus.Extensions.Channels.SignalR;
using BotNexus.Gateway.Tests.Dispatching;
using Microsoft.AspNetCore.SignalR;

namespace BotNexus.Gateway.Tests;

public sealed class InspectableConversationPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionPolicy_MissingStore_OnlyUnlinkedLegacySessionPasses(bool linked)
    {
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s_policy"), AgentId.From("agent-a"));
        if (linked) session.ConversationId = ConversationId.From("c_policy");
        (await ConversationClientWritePolicy.EvaluateSessionAsync(null, session, session.AgentId)).ShouldBe(linked ? 404 : 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImplicitSend_BoundInspectableCandidate_RejectsBeforeReactivation(bool archived)
    {
        var store = new InMemoryConversationStore();
        var conversation = InspectableConversationHttpWriteTests.CreateConversation("c_implicit", ConversationVisibility.InspectableReadOnly);
        conversation.Status = archived ? ConversationStatus.Archived : ConversationStatus.Active;
        conversation.ChannelBindings.Add(new ChannelBinding
        {
            ChannelType = ChannelKey.From("signalr"), ChannelAddress = ChannelAddress.From("agent-a")
        });
        conversation = await store.CreateAsync(conversation);
        var before = InspectableConversationHttpWriteTests.Snapshot(conversation);
        var sessions = new InMemorySessionStore();
        var orchestrator = new CapturingInboundMessageOrchestrator();
        var hub = SignalRHubTests.CreateHub(conversationStore: store, sessions: sessions, orchestrator: orchestrator,
            userScopes: [HubScopeGuard.ControlScopeValue]);
        await Should.ThrowAsync<HubException>(() => hub.SendMessage(conversation.AgentId, ChannelKey.From("telegram"), "hello"));
        InspectableConversationHttpWriteTests.Snapshot(await store.GetAsync(conversation.ConversationId)).ShouldBe(before);
        (await sessions.ListAsync()).ShouldBeEmpty();
        orchestrator.Captured.ShouldBeEmpty();
    }

    [Fact]
    public async Task SessionPolicy_UserFacingParentOwnedByAnotherAgent_IsNotFound()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync(InspectableConversationHttpWriteTests.CreateConversation("c_other", ConversationVisibility.UserFacing));
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s_other"), AgentId.From("agent-b"));
        session.ConversationId = conversation.ConversationId;
        (await ConversationClientWritePolicy.EvaluateSessionAsync(store, session, session.AgentId)).ShouldBe(404);
    }
}
