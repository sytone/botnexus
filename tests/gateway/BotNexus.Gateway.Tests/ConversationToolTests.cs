using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Tools;
using NSubstitute;

namespace BotNexus.Gateway.Tests;

public sealed class ConversationToolTests
{
    [Fact]
    public async Task Get_CurrentConversation_ReturnsConversationContext()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync(CreateConversation("agent-a", "Release Planning", "Coordinate releases"));
        var tool = new ConversationTool(store, AgentId.From("agent-a"), conversation.ConversationId);

        var result = await tool.ExecuteAsync("call-1", Args("get"));
        using var document = JsonDocument.Parse(ReadText(result));

        document.RootElement.GetProperty("id").GetString().ShouldBe(conversation.ConversationId.Value);
        document.RootElement.GetProperty("displayName").GetString().ShouldBe("Release Planning");
        document.RootElement.GetProperty("purpose").GetString().ShouldBe("Coordinate releases");
    }

    [Fact]
    public async Task SetPurpose_UpdatesCurrentConversation()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync(CreateConversation("agent-a", "Planning", null));
        var tool = new ConversationTool(store, AgentId.From("agent-a"), conversation.ConversationId);

        await tool.ExecuteAsync("call-1", Args("set_purpose", purpose: "Plan the sprint"));

        var updated = await store.GetAsync(conversation.ConversationId);
        updated.ShouldNotBeNull();
        updated.Purpose.ShouldBe("Plan the sprint");
    }

    [Fact]
    public async Task SetTitle_UsesDisplayNameAlias()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync(CreateConversation("agent-a", "Old", null));
        var tool = new ConversationTool(store, AgentId.From("agent-a"), conversation.ConversationId);

        await tool.ExecuteAsync("call-1", Args("set_title", displayName: "New display name"));

        var updated = await store.GetAsync(conversation.ConversationId);
        updated.ShouldNotBeNull();
        updated.Title.ShouldBe("New display name");
    }

    [Fact]
    public async Task List_OwnAccess_DeniesOtherAgent()
    {
        var store = new InMemoryConversationStore();
        var tool = new ConversationTool(store, AgentId.From("agent-a"), accessLevel: ConversationAccessLevel.Own);

        Func<Task> act = () => tool.ExecuteAsync("call-1", Args("list", agentId: "agent-b"));

        await act.ShouldThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task List_AllowlistAccess_ReturnsOtherAgentConversations()
    {
        var store = new InMemoryConversationStore();
        await store.CreateAsync(CreateConversation("agent-b", "Nova Planning", "Plan with Nova"));
        var tool = new ConversationTool(store, AgentId.From("agent-a"), accessLevel: ConversationAccessLevel.Allowlist, allowedAgents: ["agent-b"]);

        var result = await tool.ExecuteAsync("call-1", Args("list", agentId: "agent-b"));
        var text = ReadText(result);

        text.ShouldContain("Nova Planning");
        text.ShouldContain("Plan with Nova");
    }

    [Fact]
    public async Task New_AllAccess_CreatesConversationForRequestedAgent()
    {
        var store = new InMemoryConversationStore();
        var tool = new ConversationTool(store, AgentId.From("orchestrator"), accessLevel: ConversationAccessLevel.All);

        var result = await tool.ExecuteAsync("call-1", Args(
            "new",
            agentId: "nova",
            displayName: "Sprint Planning",
            purpose: "Plan the next sprint"));

        using var document = JsonDocument.Parse(ReadText(result));
        document.RootElement.GetProperty("agentId").GetString().ShouldBe("nova");
        document.RootElement.GetProperty("displayName").GetString().ShouldBe("Sprint Planning");
        document.RootElement.GetProperty("purpose").GetString().ShouldBe("Plan the next sprint");

        var conversations = await store.ListAsync(AgentId.From("nova"));
        conversations.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task New_WithMessage_SeedsInitialUserMessageInNewSession()
    {
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var tool = new ConversationTool(conversationStore, AgentId.From("orchestrator"), accessLevel: ConversationAccessLevel.All, sessionStore: sessionStore);

        var result = await tool.ExecuteAsync("call-1", Args(
            "new",
            agentId: "nova",
            displayName: "Handoff",
            message: "Please investigate issue #249"));

        using var document = JsonDocument.Parse(ReadText(result));
        var conversationId = document.RootElement.GetProperty("conversationId").GetString()
            ?? throw new InvalidOperationException("Expected conversationId in tool response.");
        var activeSessionId = document.RootElement.GetProperty("activeSessionId").GetString()
            ?? throw new InvalidOperationException("Expected activeSessionId in tool response.");
        conversationId.ShouldNotBeNullOrWhiteSpace();
        activeSessionId.ShouldNotBeNullOrWhiteSpace();

        var session = await sessionStore.GetAsync(SessionId.From(activeSessionId));
        session.ShouldNotBeNull();
        session.Session.ConversationId.ShouldBe(ConversationId.From(conversationId));
        var entry = session.GetHistorySnapshot().ShouldHaveSingleItem();
        entry.Role.ShouldBe(MessageRole.User);
        entry.Content.ShouldBe("Please investigate issue #249");
    }

    [Fact]
    public async Task New_WithMessage_WithoutSessionStore_Throws()
    {
        var conversationStore = new InMemoryConversationStore();
        var tool = new ConversationTool(conversationStore, AgentId.From("orchestrator"), accessLevel: ConversationAccessLevel.All);

        Func<Task> act = () => tool.ExecuteAsync("call-1", Args(
            "new",
            agentId: "nova",
            message: "Please investigate issue #249"));

        var exception = await act.ShouldThrowAsync<InvalidOperationException>();
        exception.Message.ShouldContain("Session store is required");
    }

    [Fact]
    public async Task New_WithMessage_AndDispatcher_DispatchesInboundMessageToTriggerAgentTurn()
    {
        // Arrange
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var dispatcher = Substitute.For<IChannelDispatcher>();
        var tool = new ConversationTool(
            conversationStore,
            AgentId.From("orchestrator"),
            accessLevel: ConversationAccessLevel.All,
            sessionStore: sessionStore,
            channelDispatcher: dispatcher);

        // Act
        var result = await tool.ExecuteAsync("call-1", Args(
            "new",
            agentId: "nova",
            displayName: "Handoff",
            message: "Please investigate issue #285"));

        using var document = JsonDocument.Parse(ReadText(result));
        var conversationId = document.RootElement.GetProperty("conversationId").GetString()!;
        var activeSessionId = document.RootElement.GetProperty("activeSessionId").GetString()!;

        // Assert: dispatcher was called exactly once with the correct inbound message
        await dispatcher.Received(1).DispatchAsync(
            Arg.Is<InboundMessage>(m =>
                m.TargetAgentId == "nova" &&
                m.Content == "Please investigate issue #285" &&
                m.SessionId == activeSessionId &&
                m.ConversationId == conversationId &&
                m.ChannelType.Equals(ChannelKey.From("internal"))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task New_WithMessage_WithoutDispatcher_DoesNotThrowAndStillSeedsHistory()
    {
        // Arrange — no dispatcher: existing behaviour is preserved, no agent turn triggered
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var tool = new ConversationTool(
            conversationStore,
            AgentId.From("orchestrator"),
            accessLevel: ConversationAccessLevel.All,
            sessionStore: sessionStore,
            channelDispatcher: null);

        // Act — must not throw
        var result = await tool.ExecuteAsync("call-1", Args(
            "new",
            agentId: "nova",
            message: "Seed message without turn trigger"));

        // Assert: conversation and session created, history seeded
        using var document = JsonDocument.Parse(ReadText(result));
        var activeSessionId = document.RootElement.GetProperty("activeSessionId").GetString()!;
        activeSessionId.ShouldNotBeNullOrWhiteSpace();

        var session = await sessionStore.GetAsync(SessionId.From(activeSessionId));
        session.ShouldNotBeNull();
        var entry = session.GetHistorySnapshot().ShouldHaveSingleItem();
        entry.Content.ShouldBe("Seed message without turn trigger");
    }

    [Fact]
    public async Task New_WithoutMessage_DoesNotInvokeDispatcher()
    {
        // Arrange
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var dispatcher = Substitute.For<IChannelDispatcher>();
        var tool = new ConversationTool(
            conversationStore,
            AgentId.From("orchestrator"),
            accessLevel: ConversationAccessLevel.All,
            sessionStore: sessionStore,
            channelDispatcher: dispatcher);

        // Act
        await tool.ExecuteAsync("call-1", Args("new", agentId: "nova", displayName: "Empty conv"));

        // Assert: no dispatch when no message
        await dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<InboundMessage>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendMessage_DispatchesInboundMessageToTargetConversation()
    {
        // Arrange
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var dispatcher = Substitute.For<IChannelDispatcher>();
        var conversation = await conversationStore.CreateAsync(CreateConversation("nova", "Planning", null));

        var tool = new ConversationTool(
            conversationStore,
            AgentId.From("orchestrator"),
            accessLevel: ConversationAccessLevel.All,
            sessionStore: sessionStore,
            channelDispatcher: dispatcher);

        // Act
        var result = await tool.ExecuteAsync("call-1", Args(
            "message",
            conversationId: conversation.ConversationId.Value,
            message: "Hello Nova!"));

        // Assert: dispatcher called with the user message
        await dispatcher.Received(1).DispatchAsync(
            Arg.Is<InboundMessage>(m =>
                m.Content == "Hello Nova!" &&
                m.TargetAgentId == "nova" &&
                m.ChannelType == ChannelKey.From("internal")),
            Arg.Any<CancellationToken>());

        // Result includes conversation and session IDs
        using var document = JsonDocument.Parse(ReadText(result));
        document.RootElement.GetProperty("conversationId").GetString().ShouldBe(conversation.ConversationId.Value);
        document.RootElement.GetProperty("sessionId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task SendMessage_WithoutMessage_Throws()
    {
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var conversation = await conversationStore.CreateAsync(CreateConversation("nova", "Planning", null));

        var tool = new ConversationTool(
            conversationStore,
            AgentId.From("orchestrator"),
            accessLevel: ConversationAccessLevel.All,
            sessionStore: sessionStore,
            channelDispatcher: null);

        Func<Task> act = () => tool.ExecuteAsync("call-1", Args(
            "message",
            conversationId: conversation.ConversationId.Value));

        await act.ShouldThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SendMessage_WithoutDispatcher_Throws()
    {
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var conversation = await conversationStore.CreateAsync(CreateConversation("nova", "Planning", null));

        var tool = new ConversationTool(
            conversationStore,
            AgentId.From("orchestrator"),
            accessLevel: ConversationAccessLevel.All,
            sessionStore: sessionStore,
            channelDispatcher: null);

        Func<Task> act = () => tool.ExecuteAsync("call-1", Args(
            "message",
            conversationId: conversation.ConversationId.Value,
            message: "Hi!"));

        await act.ShouldThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SendMessage_DeniedByAccessLevel_Throws()
    {
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var dispatcher = Substitute.For<IChannelDispatcher>();
        var conversation = await conversationStore.CreateAsync(CreateConversation("nova", "Planning", null));

        // agent-a with Own access cannot message nova's conversation
        var tool = new ConversationTool(
            conversationStore,
            AgentId.From("agent-a"),
            accessLevel: ConversationAccessLevel.Own,
            sessionStore: sessionStore,
            channelDispatcher: dispatcher);

        Func<Task> act = () => tool.ExecuteAsync("call-1", Args(
            "message",
            conversationId: conversation.ConversationId.Value,
            message: "Sneak message"));

        await act.ShouldThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task SendMessage_ReusesExistingActiveSession_WhenPresent()
    {
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var dispatcher = Substitute.For<IChannelDispatcher>();

        var session = await sessionStore.GetOrCreateAsync(SessionId.Create(), AgentId.From("nova"), default);
        var conversation = await conversationStore.CreateAsync(
            CreateConversation("nova", "Planning", null) with
            {
                ActiveSessionId = session.SessionId
            });
        session.Session.ConversationId = conversation.ConversationId;
        await sessionStore.SaveAsync(session, default);

        var tool = new ConversationTool(
            conversationStore,
            AgentId.From("orchestrator"),
            accessLevel: ConversationAccessLevel.All,
            sessionStore: sessionStore,
            channelDispatcher: dispatcher);

        var result = await tool.ExecuteAsync("call-1", Args(
            "message",
            conversationId: conversation.ConversationId.Value,
            message: "Use existing session"));

        using var document = JsonDocument.Parse(ReadText(result));
        document.RootElement.GetProperty("sessionId").GetString().ShouldBe(session.SessionId.Value);
    }

    private static Conversation CreateConversation(string agentId, string title, string? purpose)
        => new()
        {
            ConversationId = ConversationId.Create(),
            AgentId = AgentId.From(agentId),
            Title = title,
            Purpose = purpose,
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private static Dictionary<string, object?> Args(
        string action,
        string? agentId = null,
        string? conversationId = null,
        string? displayName = null,
        string? purpose = null,
        string? message = null)
    {
        var args = new Dictionary<string, object?> { ["action"] = action };
        if (agentId is not null) args["agentId"] = agentId;
        if (conversationId is not null) args["conversationId"] = conversationId;
        if (displayName is not null) args["displayName"] = displayName;
        if (purpose is not null) args["purpose"] = purpose;
        if (message is not null) args["message"] = message;
        return args;
    }

    private static string ReadText(AgentToolResult result)
        => result.Content.Single(c => c.Type == AgentToolContentType.Text).Value;
}
