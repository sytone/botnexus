using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class InspectableConversationAdditionalWriteTests
{
    [Theory]
    [InlineData("assign")]
    [InlineData("unassign")]
    public async Task SectionAssignment_InspectableConversation_RejectsBeforeStoreWrite(string operation)
    {
        var conversations = new InMemoryConversationStore();
        var conversation = await conversations.CreateAsync(InspectableConversationHttpWriteTests.CreateConversation("c_section", ConversationVisibility.InspectableReadOnly));
        var sections = new Mock<IConversationSectionStore>(MockBehavior.Strict);
        var controller = InspectableConversationHttpWriteTests.Authenticate(new ConversationSectionsController(sections.Object, conversations));
        ActionResult result = operation == "assign"
            ? await controller.Assign("agent-a", "section", conversation.ConversationId.Value, CancellationToken.None)
            : await controller.Unassign("agent-a", conversation.ConversationId.Value, CancellationToken.None);
        InspectableConversationHttpWriteTests.Status(result).ShouldBe(403);
        sections.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("send")]
    [InlineData("steer")]
    [InlineData("follow-up")]
    public async Task Chat_InspectableSession_RejectsBeforeHandleOrOverrideWrite(string operation)
    {
        var conversations = new InMemoryConversationStore();
        var conversation = await conversations.CreateAsync(InspectableConversationHttpWriteTests.CreateConversation("c_chat", ConversationVisibility.InspectableReadOnly));
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s_chat"), conversation.AgentId);
        session.ConversationId = conversation.ConversationId;
        await sessions.SaveAsync(session);
        var before = System.Text.Json.JsonSerializer.Serialize(await sessions.GetAsync(session.SessionId));
        var supervisor = new Mock<IAgentSupervisor>(MockBehavior.Strict);
        var controller = InspectableConversationHttpWriteTests.Authenticate(new ChatController(supervisor.Object, sessions, conversations: conversations));
        IActionResult result = operation switch
        {
            "send" => (await controller.Send(new ChatRequest("agent-a", "hello", "s_chat", "changed", "high"), CancellationToken.None)).Result ?? throw new InvalidOperationException(),
            "steer" => await controller.Steer(new AgentControlRequest("agent-a", "s_chat", "hello"), CancellationToken.None),
            _ => await controller.FollowUp(new AgentControlRequest("agent-a", "s_chat", "hello"), CancellationToken.None)
        };
        InspectableConversationHttpWriteTests.Status(result).ShouldBe(403);
        System.Text.Json.JsonSerializer.Serialize(await sessions.GetAsync(session.SessionId)).ShouldBe(before);
        supervisor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task KillSubAgent_InspectableParent_RejectsBeforeManagerLookup()
    {
        var conversations = new InMemoryConversationStore();
        var conversation = await conversations.CreateAsync(InspectableConversationHttpWriteTests.CreateConversation("c_kill", ConversationVisibility.InspectableReadOnly));
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s_kill"), conversation.AgentId);
        session.ConversationId = conversation.ConversationId;
        await sessions.SaveAsync(session);
        var manager = new Mock<ISubAgentManager>(MockBehavior.Strict);
        var controller = InspectableConversationHttpWriteTests.Authenticate(new SessionsController(sessions, manager.Object, conversations: conversations));
        InspectableConversationHttpWriteTests.Status(await controller.KillSubAgent("s_kill", "sub", CancellationToken.None)).ShouldBe(403);
        manager.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StopInstance_InspectableSession_RejectsBeforeSupervisorStop()
    {
        var conversations = new InMemoryConversationStore();
        var conversation = await conversations.CreateAsync(InspectableConversationHttpWriteTests.CreateConversation("c_stop", ConversationVisibility.InspectableReadOnly));
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s_stop"), conversation.AgentId);
        session.ConversationId = conversation.ConversationId;
        await sessions.SaveAsync(session);
        var supervisor = new Mock<IAgentSupervisor>(MockBehavior.Strict);
        var controller = InspectableConversationHttpWriteTests.Authenticate(new AgentsController(
            Mock.Of<IAgentRegistry>(), supervisor.Object, Mock.Of<IAgentConfigurationWriter>(), sessions: sessions, conversations: conversations));
        InspectableConversationHttpWriteTests.Status(await controller.StopInstance("agent-a", "s_stop", CancellationToken.None)).ShouldBe(403);
        supervisor.VerifyNoOtherCalls();
    }
}
