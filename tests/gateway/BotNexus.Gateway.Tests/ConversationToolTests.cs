using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Abstractions.Conversations;
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
    public async Task New_WithMessage_SeedsInitialAssistantMessageInNewSession()
    {
        // Hybrid rule (#1650): the `new` action posts as the calling agent with no
        // speak_as override, so the seeded entry is recorded as assistant (the agent
        // speaking as itself) rather than the old hardcoded user role.
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
        entry.Role.ShouldBe(MessageRole.Assistant);
        entry.Content.ShouldBe("Please investigate issue #249");
    }

    [Fact]
    public async Task New_WithMessageAndSpeakAsUser_SeedsInitialUserMessage()
    {
        // Hybrid rule (#1650), AC 2: an explicit speak_as:"user" on the `new` action
        // records the seeded entry as user -- the on-behalf-of-user kickoff case --
        // overriding the agent-kind assistant default.
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var tool = new ConversationTool(conversationStore, AgentId.From("orchestrator"), accessLevel: ConversationAccessLevel.All, sessionStore: sessionStore);

        var result = await tool.ExecuteAsync("call-1", Args(
            "new",
            agentId: "nova",
            displayName: "Handoff",
            message: "Please investigate issue #249",
            speakAs: "user"));

        using var document = JsonDocument.Parse(ReadText(result));
        var activeSessionId = document.RootElement.GetProperty("activeSessionId").GetString()
            ?? throw new InvalidOperationException("Expected activeSessionId in tool response.");

        var session = await sessionStore.GetAsync(SessionId.From(activeSessionId));
        session.ShouldNotBeNull();
        var entry = session.GetHistorySnapshot().ShouldHaveSingleItem();
        entry.Role.ShouldBe(MessageRole.User);
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
        var orchestrator = Substitute.For<IInboundMessageOrchestrator>();
        var tool = new ConversationTool(
            conversationStore,
            AgentId.From("orchestrator"),
            accessLevel: ConversationAccessLevel.All,
            sessionStore: sessionStore,
            messageOrchestrator: orchestrator);

        // Act
        var result = await tool.ExecuteAsync("call-1", Args(
            "new",
            agentId: "nova",
            displayName: "Handoff",
            message: "Please investigate issue #285"));

        using var document = JsonDocument.Parse(ReadText(result));
        var conversationId = document.RootElement.GetProperty("conversationId").GetString()!;
        var activeSessionId = document.RootElement.GetProperty("activeSessionId").GetString()!;

        // Assert: dispatcher was called exactly once with the correct inbound message.
        // SpeakAs is null (no override) so GatewayHost derives the role from the
        // agent-kind sender; the posted sender must be the calling agent so that
        // derivation resolves to assistant rather than user.
        orchestrator.Received(1).Post(
            Arg.Is<InboundMessage>(m =>
                m.RoutingHints != null &&
                m.RoutingHints.RequestedAgentId != null && m.RoutingHints.RequestedAgentId.Value.Value == "nova" &&
                m.Content == "Please investigate issue #285" &&
                m.RoutingHints.RequestedSessionId != null && m.RoutingHints.RequestedSessionId.Value.Value == activeSessionId &&
                m.RoutingHints.RequestedConversationId != null && m.RoutingHints.RequestedConversationId.Value.Value == conversationId &&
                m.Sender.Kind == CitizenKind.Agent &&
                m.SpeakAs == null &&
                m.ChannelType.Equals(ChannelKey.From("internal"))));
    }

    [Fact]
    public async Task New_WithMessageAndSpeakAsUser_PostsInboundMessageWithUserSpeakAs()
    {
        // Hybrid rule (#1650): the speak_as override is threaded onto the posted
        // InboundMessage.SpeakAs so GatewayHost reads it rather than re-deriving.
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var orchestrator = Substitute.For<IInboundMessageOrchestrator>();
        var tool = new ConversationTool(
            conversationStore,
            AgentId.From("orchestrator"),
            accessLevel: ConversationAccessLevel.All,
            sessionStore: sessionStore,
            messageOrchestrator: orchestrator);

        await tool.ExecuteAsync("call-1", Args(
            "new",
            agentId: "nova",
            displayName: "Handoff",
            message: "Kick off on behalf of the user",
            speakAs: "user"));

        orchestrator.Received(1).Post(
            Arg.Is<InboundMessage>(m =>
                m.Content == "Kick off on behalf of the user" &&
                m.Sender.Kind == CitizenKind.Agent &&
                m.SpeakAs != null && m.SpeakAs == MessageRole.User));
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
            messageOrchestrator: null);

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
        var orchestrator = Substitute.For<IInboundMessageOrchestrator>();
        var tool = new ConversationTool(
            conversationStore,
            AgentId.From("orchestrator"),
            accessLevel: ConversationAccessLevel.All,
            sessionStore: sessionStore,
            messageOrchestrator: orchestrator);

        // Act
        await tool.ExecuteAsync("call-1", Args("new", agentId: "nova", displayName: "Empty conv"));

        // Assert: no dispatch when no message
        orchestrator.DidNotReceive().Post(
            Arg.Any<InboundMessage>());
    }

    [Fact]
    public async Task SendMessage_DispatchesInboundMessageToTargetConversation()
    {
        // Arrange
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var orchestrator = Substitute.For<IInboundMessageOrchestrator>();
        var conversation = await conversationStore.CreateAsync(CreateConversation("nova", "Planning", null));

        var tool = new ConversationTool(
            conversationStore,
            AgentId.From("orchestrator"),
            accessLevel: ConversationAccessLevel.All,
            sessionStore: sessionStore,
            messageOrchestrator: orchestrator);

        // Act
        var result = await tool.ExecuteAsync("call-1", Args(
            "message",
            conversationId: conversation.ConversationId.Value,
            message: "Hello Nova!"));

        // Assert: dispatcher called with the agent-authored message. SpeakAs is null
        // (no override) so GatewayHost derives the role from the agent-kind sender.
        orchestrator.Received(1).Post(
            Arg.Is<InboundMessage>(m =>
                m.Content == "Hello Nova!" &&
                m.RoutingHints != null &&
                m.RoutingHints.RequestedAgentId != null && m.RoutingHints.RequestedAgentId.Value.Value == "nova" &&
                m.Sender.Kind == CitizenKind.Agent &&
                m.SpeakAs == null &&
                m.ChannelType == ChannelKey.From("internal")));

        // Result includes conversation and session IDs
        using var document = JsonDocument.Parse(ReadText(result));
        document.RootElement.GetProperty("conversationId").GetString().ShouldBe(conversation.ConversationId.Value);
        document.RootElement.GetProperty("sessionId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task SendMessage_WhenConversationArchived_ReactivatesBeforeAssigningSession()
    {
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var orchestrator = Substitute.For<IInboundMessageOrchestrator>();
        var conversation = await conversationStore.CreateAsync(CreateConversation("nova", "Archived planning", "Preserve this purpose"));
        await conversationStore.ArchiveAsync(conversation.ConversationId);
        var router = new DefaultConversationRouter(
            conversationStore,
            sessionStore,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DefaultConversationRouter>.Instance);
        var tool = new ConversationTool(
            conversationStore,
            AgentId.From("orchestrator"),
            accessLevel: ConversationAccessLevel.All,
            sessionStore: sessionStore,
            messageOrchestrator: orchestrator,
            conversationRouter: router);

        var result = await tool.ExecuteAsync("call-1", Args(
            "message",
            conversationId: conversation.ConversationId.Value,
            message: "Reactivate this conversation"));

        using var document = JsonDocument.Parse(ReadText(result));
        var sessionId = SessionId.From(document.RootElement.GetProperty("sessionId").GetString() ?? string.Empty);
        var reopened = await conversationStore.GetAsync(conversation.ConversationId);
        reopened.ShouldNotBeNull();
        reopened.Status.ShouldBe(ConversationStatus.Active);
        reopened.ActiveSessionId.ShouldBe(sessionId);
        reopened.Title.ShouldBe("Archived planning");
        reopened.Purpose.ShouldBe("Preserve this purpose");
    }
    [Fact]
    public async Task SendMessage_WithSpeakAsUser_ThreadsUserSpeakAsOntoInboundMessage()
    {
        // Hybrid rule (#1650), AC 2: an explicit speak_as:"user" on the `message`
        // action is threaded onto the posted InboundMessage.SpeakAs so the message
        // is recorded as an on-behalf-of-user kickoff rather than an agent post.
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var orchestrator = Substitute.For<IInboundMessageOrchestrator>();
        var conversation = await conversationStore.CreateAsync(CreateConversation("nova", "Planning", null));

        var tool = new ConversationTool(
            conversationStore,
            AgentId.From("orchestrator"),
            accessLevel: ConversationAccessLevel.All,
            sessionStore: sessionStore,
            messageOrchestrator: orchestrator);

        await tool.ExecuteAsync("call-1", Args(
            "message",
            conversationId: conversation.ConversationId.Value,
            message: "Kick off on behalf of the user",
            speakAs: "user"));

        orchestrator.Received(1).Post(
            Arg.Is<InboundMessage>(m =>
                m.Content == "Kick off on behalf of the user" &&
                m.Sender.Kind == CitizenKind.Agent &&
                m.SpeakAs != null && m.SpeakAs == MessageRole.User));
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
            messageOrchestrator: null);

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
            messageOrchestrator: null);

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
        var orchestrator = Substitute.For<IInboundMessageOrchestrator>();
        var conversation = await conversationStore.CreateAsync(CreateConversation("nova", "Planning", null));

        // agent-a with Own access cannot message nova's conversation
        var tool = new ConversationTool(
            conversationStore,
            AgentId.From("agent-a"),
            accessLevel: ConversationAccessLevel.Own,
            sessionStore: sessionStore,
            messageOrchestrator: orchestrator);

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
        var orchestrator = Substitute.For<IInboundMessageOrchestrator>();

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
            messageOrchestrator: orchestrator);

        var result = await tool.ExecuteAsync("call-1", Args(
            "message",
            conversationId: conversation.ConversationId.Value,
            message: "Use existing session"));

        using var document = JsonDocument.Parse(ReadText(result));
        document.RootElement.GetProperty("sessionId").GetString().ShouldBe(session.SessionId.Value);
    }

    [Fact]
    public async Task SendMessage_WithSqliteStore_ReopensDurablyAndPreservesMetadataAndBindings()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"botnexus-2167-{Guid.NewGuid():N}.sqlite");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        try
        {
            var conversationStore = new SqliteConversationStore(
                connectionString,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteConversationStore>.Instance);
            var sessionStore = new SqliteSessionStore(
                connectionString,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteSessionStore>.Instance,
                conversationStore);
            var orchestrator = Substitute.For<IInboundMessageOrchestrator>();
            var conversation = CreateConversation("nova", "Durable title", "Durable purpose");
            conversation.ChannelBindings.Add(new ChannelBinding
            {
                ChannelType = ChannelKey.From("signalr"),
                ChannelAddress = ChannelAddress.From("portal-2167")
            });
            conversation = await conversationStore.CreateAsync(conversation);
            await conversationStore.ArchiveAsync(conversation.ConversationId);
            var router = new DefaultConversationRouter(
                conversationStore,
                sessionStore,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<DefaultConversationRouter>.Instance);
            var tool = new ConversationTool(
                conversationStore,
                AgentId.From("orchestrator"),
                accessLevel: ConversationAccessLevel.All,
                sessionStore: sessionStore,
                messageOrchestrator: orchestrator,
                conversationRouter: router);

            var result = await tool.ExecuteAsync("call-2167", Args(
                "message",
                conversationId: conversation.ConversationId.Value,
                message: "Reopen durably"));
            using var document = JsonDocument.Parse(ReadText(result));
            var sessionId = SessionId.From(document.RootElement.GetProperty("sessionId").GetString() ?? string.Empty);
            await conversationStore.TouchAsync(conversation.ConversationId);

            var reopenedStore = new SqliteConversationStore(
                connectionString,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteConversationStore>.Instance);
            var reopened = await reopenedStore.GetAsync(conversation.ConversationId);
            reopened.ShouldNotBeNull();
            reopened.Status.ShouldBe(ConversationStatus.Active);
            reopened.ActiveSessionId.ShouldBe(sessionId);
            reopened.Title.ShouldBe("Durable title");
            reopened.Purpose.ShouldBe("Durable purpose");
            reopened.ChannelBindings.ShouldHaveSingleItem().ChannelAddress.ShouldBe(ChannelAddress.From("portal-2167"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task SaveAsync_RejectsArchivedConversationWithActiveSession()
    {
        var store = new InMemoryConversationStore();
        var invalid = CreateConversation("nova", "Invalid", null) with
        {
            Status = ConversationStatus.Archived,
            ActiveSessionId = SessionId.Create()
        };

        await store.CreateAsync(invalid with { ActiveSessionId = null });
        Func<Task> save = () => store.SaveAsync(invalid);

        (await save.ShouldThrowAsync<InvalidOperationException>()).Message.ShouldContain("cannot be archived");
    }
    // --- List status filter tests (#1301) ---

    [Fact]
    public async Task List_WithActiveStatusFilter_ReturnsOnlyActiveConversations()
    {
        var store = new InMemoryConversationStore();
        await store.CreateAsync(CreateConversation("agent-a", "Active Chat", null));
        var archived = await store.CreateAsync(CreateConversation("agent-a", "Old Chat", null));
        archived.Status = ConversationStatus.Archived;
        await store.SaveAsync(archived, default);

        var tool = new ConversationTool(store, AgentId.From("agent-a"), accessLevel: ConversationAccessLevel.Own);
        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?> { ["action"] = "list", ["status"] = "active" });
        var text = ReadText(result);

        text.ShouldContain("Active Chat");
        text.ShouldNotContain("Old Chat");
    }

    [Fact]
    public async Task List_WithArchivedStatusFilter_ReturnsOnlyArchivedConversations()
    {
        var store = new InMemoryConversationStore();
        await store.CreateAsync(CreateConversation("agent-a", "Active Chat", null));
        var archived = await store.CreateAsync(CreateConversation("agent-a", "Old Chat", null));
        archived.Status = ConversationStatus.Archived;
        await store.SaveAsync(archived, default);

        var tool = new ConversationTool(store, AgentId.From("agent-a"), accessLevel: ConversationAccessLevel.Own);
        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?> { ["action"] = "list", ["status"] = "archived" });
        var text = ReadText(result);

        text.ShouldNotContain("Active Chat");
        text.ShouldContain("Old Chat");
    }

    [Fact]
    public async Task List_WithNoStatusFilter_ReturnsAllConversations()
    {
        var store = new InMemoryConversationStore();
        await store.CreateAsync(CreateConversation("agent-a", "Active Chat", null));
        var archived = await store.CreateAsync(CreateConversation("agent-a", "Old Chat", null));
        archived.Status = ConversationStatus.Archived;
        await store.SaveAsync(archived, default);

        var tool = new ConversationTool(store, AgentId.From("agent-a"), accessLevel: ConversationAccessLevel.Own);
        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?> { ["action"] = "list" });
        var text = ReadText(result);

        text.ShouldContain("Active Chat");
        text.ShouldContain("Old Chat");
    }

    [Fact]
    public async Task List_WithInvalidStatusFilter_ThrowsArgumentException()
    {
        var store = new InMemoryConversationStore();
        var tool = new ConversationTool(store, AgentId.From("agent-a"), accessLevel: ConversationAccessLevel.Own);

        Func<Task> act = () => tool.ExecuteAsync("call-1", new Dictionary<string, object?> { ["action"] = "list", ["status"] = "invalid" });

        (await act.ShouldThrowAsync<ArgumentException>()).Message.ShouldContain("Invalid status filter");
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
        string? message = null,
        string? speakAs = null,
        string[]? fields = null)
    {
        var args = new Dictionary<string, object?> { ["action"] = action };
        if (agentId is not null) args["agentId"] = agentId;
        if (conversationId is not null) args["conversationId"] = conversationId;
        if (displayName is not null) args["displayName"] = displayName;
        if (purpose is not null) args["purpose"] = purpose;
        if (message is not null) args["message"] = message;
        if (speakAs is not null) args["speak_as"] = speakAs;
        if (fields is not null) args["fields"] = fields;
        return args;
    }

    // --- Archive action tests (#700) ---

    [Fact]
    public async Task Archive_SetsConversationStatusToArchived()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync(CreateConversation("agent-a", "Old Chat", null));
        var tool = new ConversationTool(store, AgentId.From("agent-a"), conversation.ConversationId);

        var result = await tool.ExecuteAsync("call-1", Args("archive"));
        var text = ReadText(result);

        text.ShouldContain("archived");

        var updated = await store.GetAsync(conversation.ConversationId);
        updated.ShouldNotBeNull();
        updated.Status.ShouldBe(ConversationStatus.Archived);
    }

    [Fact]
    public async Task Archive_WithExplicitConversationId_ArchivesCorrectConversation()
    {
        var store = new InMemoryConversationStore();
        var conv1 = await store.CreateAsync(CreateConversation("agent-a", "Keep", null));
        var conv2 = await store.CreateAsync(CreateConversation("agent-a", "Remove", null));
        var tool = new ConversationTool(store, AgentId.From("agent-a"), conv1.ConversationId);

        await tool.ExecuteAsync("call-1", Args("archive", conversationId: conv2.ConversationId.Value));

        var kept = await store.GetAsync(conv1.ConversationId);
        var removed = await store.GetAsync(conv2.ConversationId);
        kept.ShouldNotBeNull();
        kept.Status.ShouldBe(ConversationStatus.Active);
        removed.ShouldNotBeNull();
        removed.Status.ShouldBe(ConversationStatus.Archived);
    }

    [Fact]
    public async Task Archive_OwnAccess_DeniesOtherAgentConversation()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync(CreateConversation("agent-b", "Secret", null));
        var tool = new ConversationTool(store, AgentId.From("agent-a"), accessLevel: ConversationAccessLevel.Own);

        Func<Task> act = () => tool.ExecuteAsync("call-1", Args("archive", conversationId: conversation.ConversationId.Value));

        await act.ShouldThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task Archive_AllAccess_CanArchiveOtherAgentConversation()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync(CreateConversation("agent-b", "Old", null));
        var tool = new ConversationTool(store, AgentId.From("agent-a"), accessLevel: ConversationAccessLevel.All);

        await tool.ExecuteAsync("call-1", Args("archive", conversationId: conversation.ConversationId.Value));

        var updated = await store.GetAsync(conversation.ConversationId);
        updated.ShouldNotBeNull();
        updated.Status.ShouldBe(ConversationStatus.Archived);
    }

    [Fact]
    public async Task Archive_NonExistentConversation_ThrowsKeyNotFoundException()
    {
        var store = new InMemoryConversationStore();
        var tool = new ConversationTool(store, AgentId.From("agent-a"), accessLevel: ConversationAccessLevel.Own);
        var fakeId = ConversationId.Create();

        Func<Task> act = () => tool.ExecuteAsync("call-1", Args("archive", conversationId: fakeId.Value));

        await act.ShouldThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task PrepareArguments_ArchiveAction_IsAccepted()
    {
        var store = new InMemoryConversationStore();
        var tool = new ConversationTool(store, AgentId.From("agent-a"));

        // Should not throw
        var result = await tool.PrepareArgumentsAsync(Args("archive"));
        result.ShouldNotBeNull();
    }

    // --- Notifier tests (#697) ---

    [Fact]
    public async Task SetTitle_FiresUpdatedNotification()
    {
        var store = new InMemoryConversationStore();
        var notifier = Substitute.For<IConversationChangeNotifier>();
        var conversation = await store.CreateAsync(CreateConversation("agent-a", "Old", null));
        var tool = new ConversationTool(store, AgentId.From("agent-a"), conversation.ConversationId, changeNotifier: notifier);

        await tool.ExecuteAsync("call-1", Args("set_title", displayName: "New Title"));

        await notifier.Received(1).NotifyConversationChangedAsync(
            "updated",
            "agent-a",
            conversation.ConversationId.Value);
    }

    [Fact]
    public async Task SetPurpose_FiresUpdatedNotification()
    {
        var store = new InMemoryConversationStore();
        var notifier = Substitute.For<IConversationChangeNotifier>();
        var conversation = await store.CreateAsync(CreateConversation("agent-a", "Chat", null));
        var tool = new ConversationTool(store, AgentId.From("agent-a"), conversation.ConversationId, changeNotifier: notifier);

        await tool.ExecuteAsync("call-1", Args("set_purpose", purpose: "New purpose"));

        await notifier.Received(1).NotifyConversationChangedAsync(
            "updated",
            "agent-a",
            conversation.ConversationId.Value);
    }

    [Fact]
    public async Task Set_FiresUpdatedNotification()
    {
        var store = new InMemoryConversationStore();
        var notifier = Substitute.For<IConversationChangeNotifier>();
        var conversation = await store.CreateAsync(CreateConversation("agent-a", "Chat", null));
        var tool = new ConversationTool(store, AgentId.From("agent-a"), conversation.ConversationId, changeNotifier: notifier);

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?> { ["action"] = "set", ["instructions"] = "Be brief" });

        await notifier.Received(1).NotifyConversationChangedAsync(
            "updated",
            "agent-a",
            conversation.ConversationId.Value);
    }

    [Fact]
    public async Task Archive_FiresArchivedNotification()
    {
        var store = new InMemoryConversationStore();
        var notifier = Substitute.For<IConversationChangeNotifier>();
        var conversation = await store.CreateAsync(CreateConversation("agent-a", "Old Chat", null));
        var tool = new ConversationTool(store, AgentId.From("agent-a"), conversation.ConversationId, changeNotifier: notifier);

        await tool.ExecuteAsync("call-1", Args("archive"));

        await notifier.Received(1).NotifyConversationChangedAsync(
            "archived",
            "agent-a",
            conversation.ConversationId.Value);
    }

    [Fact]
    public async Task SetTitle_WithoutNotifier_DoesNotThrow()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync(CreateConversation("agent-a", "Old", null));
        var tool = new ConversationTool(store, AgentId.From("agent-a"), conversation.ConversationId);

        // No changeNotifier injected - should not throw
        await Should.NotThrowAsync(() => tool.ExecuteAsync("call-1", Args("set_title", displayName: "New")));
    }

    [Fact]
    public async Task NotifierFailure_DoesNotPropagate_SetTitle()
    {
        var store = new InMemoryConversationStore();
        var notifier = Substitute.For<IConversationChangeNotifier>();
        notifier.NotifyConversationChangedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromException(new InvalidOperationException("SignalR hub unavailable")));
        var conversation = await store.CreateAsync(CreateConversation("agent-a", "Chat", null));
        var tool = new ConversationTool(store, AgentId.From("agent-a"), conversation.ConversationId, changeNotifier: notifier);

        // Notifier failure must not propagate to the caller
        await Should.NotThrowAsync(() => tool.ExecuteAsync("call-1", Args("set_title", displayName: "New")));

        // Mutation still persisted despite notifier failure
        var updated = await store.GetAsync(conversation.ConversationId);
        updated.ShouldNotBeNull();
        updated.Title.ShouldBe("New");
    }


    // --- Field-selection (sparse fieldsets) tests (#1783) ---

    [Fact]
    public async Task List_WithFields_ReturnsOnlyRequestedKeysPerConversation()
    {
        var store = new InMemoryConversationStore();
        await store.CreateAsync(CreateConversation("agent-a", "Release Planning", "Coordinate releases"));
        var tool = new ConversationTool(store, AgentId.From("agent-a"));

        var result = await tool.ExecuteAsync("call-1", Args("list", fields: ["conversationId", "title"]));
        using var document = JsonDocument.Parse(ReadText(result));

        var element = document.RootElement.EnumerateArray().Single();
        var names = element.EnumerateObject().Select(p => p.Name).ToArray();
        names.ShouldBe(["conversationId", "title"], ignoreOrder: true);
        element.GetProperty("title").GetString().ShouldBe("Release Planning");
        element.TryGetProperty("purpose", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task List_WithoutFields_ReturnsFullObject()
    {
        var store = new InMemoryConversationStore();
        await store.CreateAsync(CreateConversation("agent-a", "Release Planning", "Coordinate releases"));
        var tool = new ConversationTool(store, AgentId.From("agent-a"));

        var result = await tool.ExecuteAsync("call-1", Args("list"));
        using var document = JsonDocument.Parse(ReadText(result));

        var element = document.RootElement.EnumerateArray().Single();
        element.TryGetProperty("purpose", out _).ShouldBeTrue();
        element.TryGetProperty("agentId", out _).ShouldBeTrue();
        element.GetProperty("displayName").GetString().ShouldBe("Release Planning");
    }

    [Fact]
    public async Task List_WithEmptyFields_ReturnsFullObject()
    {
        var store = new InMemoryConversationStore();
        await store.CreateAsync(CreateConversation("agent-a", "Release Planning", "Coordinate releases"));
        var tool = new ConversationTool(store, AgentId.From("agent-a"));

        var result = await tool.ExecuteAsync("call-1", Args("list", fields: []));
        using var document = JsonDocument.Parse(ReadText(result));

        var element = document.RootElement.EnumerateArray().Single();
        element.TryGetProperty("purpose", out _).ShouldBeTrue();
        element.GetProperty("displayName").GetString().ShouldBe("Release Planning");
    }

    [Fact]
    public async Task List_WithUnknownField_IgnoresItLeniently()
    {
        var store = new InMemoryConversationStore();
        await store.CreateAsync(CreateConversation("agent-a", "Release Planning", "Coordinate releases"));
        var tool = new ConversationTool(store, AgentId.From("agent-a"));

        var result = await tool.ExecuteAsync("call-1", Args("list", fields: ["title", "doesNotExist"]));
        using var document = JsonDocument.Parse(ReadText(result));

        var element = document.RootElement.EnumerateArray().Single();
        var names = element.EnumerateObject().Select(p => p.Name).ToArray();
        names.ShouldBe(["title"]);
        element.TryGetProperty("doesNotExist", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task List_WithFields_IsCaseInsensitive()
    {
        var store = new InMemoryConversationStore();
        await store.CreateAsync(CreateConversation("agent-a", "Release Planning", "Coordinate releases"));
        var tool = new ConversationTool(store, AgentId.From("agent-a"));

        var result = await tool.ExecuteAsync("call-1", Args("list", fields: ["CONVERSATIONID", "TiTlE"]));
        using var document = JsonDocument.Parse(ReadText(result));

        var element = document.RootElement.EnumerateArray().Single();
        var names = element.EnumerateObject().Select(p => p.Name).ToArray();
        names.ShouldBe(["conversationId", "title"], ignoreOrder: true);
    }

    [Fact]
    public async Task Get_WithFields_ReturnsOnlyRequestedKeys()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync(CreateConversation("agent-a", "Release Planning", "Coordinate releases"));
        var tool = new ConversationTool(store, AgentId.From("agent-a"), conversation.ConversationId);

        var result = await tool.ExecuteAsync("call-1", Args("get", fields: ["conversationId", "title"]));
        using var document = JsonDocument.Parse(ReadText(result));

        var names = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        names.ShouldBe(["conversationId", "title"], ignoreOrder: true);
        document.RootElement.GetProperty("title").GetString().ShouldBe("Release Planning");
        document.RootElement.TryGetProperty("purpose", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Get_WithUnknownFieldOnly_ReturnsEmptyObject()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync(CreateConversation("agent-a", "Release Planning", "Coordinate releases"));
        var tool = new ConversationTool(store, AgentId.From("agent-a"), conversation.ConversationId);

        var result = await tool.ExecuteAsync("call-1", Args("get", fields: ["nope"]));
        using var document = JsonDocument.Parse(ReadText(result));

        document.RootElement.EnumerateObject().Count().ShouldBe(0);
    }

    private static string ReadText(AgentToolResult result)
        => result.Content.Single(c => c.Type == AgentToolContentType.Text).Value;
}

