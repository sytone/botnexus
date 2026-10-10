using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Tests.Dispatching;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class InspectableConversationMessageWriteTests
{
    [Theory]
    [InlineData(ConversationVisibility.InspectableReadOnly, false, false)]
    [InlineData(ConversationVisibility.InspectableReadOnly, true, false)]
    [InlineData(ConversationVisibility.InspectableReadOnly, false, true)]
    [InlineData(ConversationVisibility.InspectableReadOnly, true, true)]
    [InlineData(ConversationVisibility.InternalHidden, false, false)]
    [InlineData(ConversationVisibility.InternalHidden, true, false)]
    [InlineData(ConversationVisibility.UserFacing, false, false)]
    [InlineData(ConversationVisibility.UserFacing, true, false)]
    [InlineData(ConversationVisibility.UserFacing, false, true)]
    [InlineData(ConversationVisibility.UserFacing, true, true)]
    public async Task Post_StoredVisibility_RejectsBeforeResolutionOrAllowsUserFacing(
        ConversationVisibility visibility, bool wake, bool hasSession)
    {
        var store = new InMemoryConversationStore();
        var sessions = new InMemorySessionStore();
        var conversation = InspectableConversationHttpWriteTests.CreateConversation("c_message", visibility);
        var sessionId = SessionId.From("s_existing");
        if (hasSession)
        {
            conversation.ActiveSessionId = sessionId;
            var session = await sessions.GetOrCreateAsync(sessionId, conversation.AgentId);
            session.ConversationId = conversation.ConversationId;
            session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "existing transcript" });
            await sessions.SaveAsync(session);
        }
        conversation = await store.CreateAsync(conversation);
        var before = InspectableConversationHttpWriteTests.Snapshot(conversation);
        var beforeSessions = System.Text.Json.JsonSerializer.Serialize(await sessions.ListAsync());
        var router = new DefaultConversationRouter(store, sessions, NullLogger<DefaultConversationRouter>.Instance);
        var dispatcher = new RecordingDispatcher(new DefaultConversationDispatcher(router, store));
        var orchestrator = new CapturingInboundMessageOrchestrator();
        var agents = new Mock<IAgentRegistry>();
        agents.Setup(value => value.Contains(conversation.AgentId)).Returns(true);
        var controller = InspectableConversationHttpWriteTests.Authenticate(new ConversationMessagesController(
            store, sessions, dispatcher, orchestrator, agents.Object, NullLogger<ConversationMessagesController>.Instance));

        var result = await controller.PostMessage("agent-a", conversation.ConversationId.Value,
            new PostConversationMessageRequest("client mutation", Wake: wake), CancellationToken.None);

        if (visibility != ConversationVisibility.UserFacing)
        {
            InspectableConversationHttpWriteTests.Status(result).ShouldBe(visibility == ConversationVisibility.InternalHidden ? 404 : 403);
            dispatcher.Calls.ShouldBe(0);
            orchestrator.Captured.ShouldBeEmpty();
            InspectableConversationHttpWriteTests.Snapshot(await store.GetAsync(conversation.ConversationId)).ShouldBe(before);
            System.Text.Json.JsonSerializer.Serialize(await sessions.ListAsync()).ShouldBe(beforeSessions);
        }
        else
        {
            InspectableConversationHttpWriteTests.Status(result).ShouldBe(202);
            dispatcher.Calls.ShouldBe(1);
            var persisted = await store.GetAsync(conversation.ConversationId);
            persisted.ShouldNotBeNull().ActiveSessionId.ShouldNotBeNull();
            var boundSessionId = persisted.ActiveSessionId ?? throw new InvalidOperationException("No bound session");
            var session = await sessions.GetAsync(boundSessionId);
            session.ShouldNotBeNull();
            if (wake) orchestrator.Captured.ShouldHaveSingleItem();
            else
            {
                orchestrator.Captured.ShouldBeEmpty();
                session.History.ShouldContain(entry => entry.Content == "client mutation" && entry.Role == MessageRole.User);
            }
        }
    }

    private sealed class RecordingDispatcher(IConversationDispatcher inner) : IConversationDispatcher
    {
        public int Calls { get; private set; }
        public Task<DispatchResult> DispatchAsync(InboundMessageContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            return inner.DispatchAsync(context, cancellationToken);
        }
    }
}
