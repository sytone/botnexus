using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Channels.SignalR;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Services;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Services;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Tests.Dispatching;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class InspectableConversationHubWriteTests
{
    public static IEnumerable<object[]> MessageWrites()
    {
        string[] operations = ["send", "media", "deliver", "canvas", "steer", "steer-media", "interrupt", "interrupt-media", "follow-up", "follow-up-media"];
        foreach (var operation in operations)
        foreach (var visibility in Enum.GetValues<ConversationVisibility>())
            yield return [operation, visibility];
    }

    [Theory]
    [MemberData(nameof(MessageWrites))]
    public async Task MessageWrite_ControlScopedCaller_UsesStoredVisibilityBeforeAdmission(
        string operation, ConversationVisibility visibility)
    {
        var store = new InMemoryConversationStore();
        var sessions = new InMemorySessionStore();
        var conversation = InspectableConversationHttpWriteTests.CreateConversation("c_hub", visibility);
        var sessionId = SessionId.From("s_hub");
        conversation.ActiveSessionId = sessionId;
        conversation = await store.CreateAsync(conversation);
        var session = await sessions.GetOrCreateAsync(sessionId, conversation.AgentId);
        session.ConversationId = conversation.ConversationId;
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "existing transcript" });
        await sessions.SaveAsync(session);
        var before = InspectableConversationHttpWriteTests.Snapshot(await store.GetAsync(conversation.ConversationId));
        var beforeSessions = JsonSerializer.Serialize(await sessions.ListAsync());
        var orchestrator = new CapturingInboundMessageOrchestrator();
        var groups = new Mock<IGroupManager>();
        groups.Setup(value => value.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var hub = SignalRHubTests.CreateHub(sessions: sessions, orchestrator: orchestrator,
            conversationStore: store, groups: groups.Object, userScopes: [HubScopeGuard.ControlScopeValue]);
        var agentId = conversation.AgentId;
        var channel = ChannelKey.From("signalr");
        var id = conversation.ConversationId.Value;
        IReadOnlyList<MediaContentPartDto> media = [new() { MimeType = "text/plain", Text = "attachment", FileName = "note.txt" }];
        Func<Task> write = operation switch
        {
            "send" => () => hub.SendMessage(agentId, channel, "client mutation", id),
            "media" => () => hub.SendMessageWithMedia(agentId, channel, "", media, id),
            "deliver" => () => hub.DeliverMessageWithMedia(agentId, channel, "", media, id, InboundDeliveryMode.Auto),
            "canvas" => () => hub.SubmitCanvasPrompt(agentId, channel, "client mutation", id),
            "steer" => () => hub.Steer(agentId, sessionId, "client mutation", null),
            "steer-media" => () => hub.SteerWithMedia(agentId, sessionId, "", media, null),
            "interrupt" => () => hub.InterruptAndSteer(agentId, sessionId, "client mutation"),
            "interrupt-media" => () => hub.InterruptAndSteerWithMedia(agentId, sessionId, "", media),
            "follow-up" => () => hub.FollowUp(agentId, sessionId, "client mutation"),
            "follow-up-media" => () => hub.FollowUpWithMedia(agentId, sessionId, "", media),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        if (visibility != ConversationVisibility.UserFacing)
        {
            // Gate must throw on the awaited client boundary, not disappear into SafeDispatchAsync.
            await Should.ThrowAsync<HubException>(write);
            if (hub.LastFollowUpDispatch is { } dispatch) await dispatch;
            orchestrator.Captured.ShouldBeEmpty();
            InspectableConversationHttpWriteTests.Snapshot(await store.GetAsync(conversation.ConversationId)).ShouldBe(before);
            JsonSerializer.Serialize(await sessions.ListAsync()).ShouldBe(beforeSessions);
            groups.Verify(value => value.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        else
        {
            await write();
            if (hub.LastFollowUpDispatch is { } dispatch) await dispatch;
            var inbound = orchestrator.Captured.ShouldHaveSingleItem();
            inbound.RoutingHints.ShouldNotBeNull().RequestedAgentId.ShouldBe(agentId);
            // Session-addressed follow-up currently lets the orchestrator resolve the conversation.
            if (!operation.StartsWith("follow-up", StringComparison.Ordinal))
                inbound.RoutingHints.RequestedConversationId.ShouldBe(conversation.ConversationId);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Send_InspectableWithoutSession_RejectsBeforeDispatcherCanCreateSessionOrBinding(bool withMedia)
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync(InspectableConversationHttpWriteTests.CreateConversation("c_unbound", ConversationVisibility.InspectableReadOnly));
        var dispatcher = new Mock<IConversationDispatcher>(MockBehavior.Strict);
        var orchestrator = new CapturingInboundMessageOrchestrator();
        var hub = SignalRHubTests.CreateHub(conversationStore: store, conversationDispatcher: dispatcher.Object,
            orchestrator: orchestrator, userScopes: [HubScopeGuard.ControlScopeValue]);
        Func<Task> write = withMedia
            ? () => hub.SendMessageWithMedia(conversation.AgentId, ChannelKey.From("signalr"), "hello", [], conversation.ConversationId.Value)
            : () => hub.SendMessage(conversation.AgentId, ChannelKey.From("signalr"), "hello", conversation.ConversationId.Value);
        await Should.ThrowAsync<HubException>(write);
        dispatcher.VerifyNoOtherCalls();
        orchestrator.Captured.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(ConversationVisibility.InspectableReadOnly)]
    [InlineData(ConversationVisibility.InternalHidden)]
    [InlineData(ConversationVisibility.UserFacing)]
    public async Task RespondToAskUser_DurableCheckpoint_VisibilityGuardsClaimAndResume(ConversationVisibility visibility)
    {
        var store = new InMemoryConversationStore();
        var conversation = InspectableConversationHttpWriteTests.CreateConversation("c_checkpoint", visibility);
        var request = new AskUserRequest
        {
            RequestId = "req-checkpoint", ConversationId = conversation.ConversationId,
            SessionId = SessionId.From("s_checkpoint"), AgentId = conversation.AgentId, Prompt = "Resume me"
        };
        conversation.PendingAskUserJson = JsonSerializer.Serialize(request, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        conversation = await store.CreateAsync(conversation);
        var before = InspectableConversationHttpWriteTests.Snapshot(conversation);
        using var registry = new AskUserResponseRegistry();
        var resolver = new AskUserPromptResolver(registry, NullLogger<AskUserPromptResolver>.Instance);
        var resumer = new RecordingResumer();
        var checkpoints = new AskUserCheckpointService(resolver, store, NullLogger<AskUserCheckpointService>.Instance, resumer);
        var groups = new Mock<IGroupManager>();
        groups.Setup(value => value.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var hub = SignalRHubTests.CreateHub(conversationStore: store, groups: groups.Object,
            askUserPromptResolver: resolver, askUserCheckpointService: checkpoints, userScopes: [HubScopeGuard.ControlScopeValue]);
        Func<Task> write = () => hub.RespondToAskUser(conversation.ConversationId.Value, request.RequestId, "answer", null, false);
        if (visibility == ConversationVisibility.UserFacing)
        {
            await write();
            resumer.Calls.ShouldBe(1);
            (await store.GetAsync(conversation.ConversationId)).ShouldNotBeNull().PendingAskUserJson.ShouldBeNull();
        }
        else
        {
            await Should.ThrowAsync<HubException>(write);
            resumer.Calls.ShouldBe(0);
            InspectableConversationHttpWriteTests.Snapshot(await store.GetAsync(conversation.ConversationId)).ShouldBe(before);
            groups.Verify(value => value.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Fact]
    public async Task Send_ReadScopedCaller_IsRejectedBeforeConversationLookup()
    {
        var store = new Mock<IConversationStore>(MockBehavior.Strict);
        var hub = SignalRHubTests.CreateHub(conversationStore: store.Object, userScopes: [HubScopeGuard.ReadScopeValue]);
        await Should.ThrowAsync<HubException>(() => hub.SendMessage(AgentId.From("agent-a"), ChannelKey.From("signalr"), "hello", "c_guard"));
        store.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("abort")]
    [InlineData("reset")]
    [InlineData("compact")]
    public async Task Lifecycle_InspectableParent_RejectsBeforeSupervisorResetOrCompaction(string operation)
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync(InspectableConversationHttpWriteTests.CreateConversation("c_lifecycle", ConversationVisibility.InspectableReadOnly));
        var sessions = new InMemorySessionStore();
        var sessionId = SessionId.From("s_lifecycle");
        var session = await sessions.GetOrCreateAsync(sessionId, conversation.AgentId);
        session.ConversationId = conversation.ConversationId;
        await sessions.SaveAsync(session);
        var before = JsonSerializer.Serialize(await sessions.GetAsync(sessionId));
        var supervisor = new Mock<BotNexus.Gateway.Abstractions.Agents.IAgentSupervisor>(MockBehavior.Strict);
        var compactor = new Mock<BotNexus.Gateway.Abstractions.Sessions.ISessionCompactor>(MockBehavior.Strict);
        var reset = new Mock<IConversationResetService>(MockBehavior.Strict);
        var hub = SignalRHubTests.CreateHub(sessions: sessions, conversationStore: store, supervisor: supervisor.Object,
            compactor: compactor.Object, resetService: reset.Object, userScopes: [HubScopeGuard.ControlScopeValue]);
        Func<Task> write = operation switch
        {
            "abort" => () => hub.Abort(conversation.AgentId, sessionId),
            "reset" => () => hub.ResetSession(conversation.AgentId, sessionId),
            "compact" => () => hub.CompactSession(conversation.AgentId, sessionId),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        await Should.ThrowAsync<HubException>(write);
        supervisor.VerifyNoOtherCalls();
        compactor.VerifyNoOtherCalls();
        reset.VerifyNoOtherCalls();
        JsonSerializer.Serialize(await sessions.GetAsync(sessionId)).ShouldBe(before);
    }

    [Fact]
    public async Task Steer_WritableConversationHintCannotBypassInspectableSessionParent()
    {
        var store = new InMemoryConversationStore();
        var guarded = await store.CreateAsync(InspectableConversationHttpWriteTests.CreateConversation("c_guarded", ConversationVisibility.InspectableReadOnly));
        var writable = await store.CreateAsync(InspectableConversationHttpWriteTests.CreateConversation("c_writable", ConversationVisibility.UserFacing));
        var sessions = new InMemorySessionStore();
        var sessionId = SessionId.From("s_guarded");
        var session = await sessions.GetOrCreateAsync(sessionId, guarded.AgentId);
        session.ConversationId = guarded.ConversationId;
        await sessions.SaveAsync(session);
        var orchestrator = new CapturingInboundMessageOrchestrator();
        var groups = new Mock<IGroupManager>();
        groups.Setup(value => value.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var hub = SignalRHubTests.CreateHub(sessions: sessions, conversationStore: store, groups: groups.Object,
            orchestrator: orchestrator, userScopes: [HubScopeGuard.ControlScopeValue]);
        await Should.ThrowAsync<HubException>(() => hub.Steer(guarded.AgentId, sessionId, "bypass attempt", writable.ConversationId.Value));
        orchestrator.Captured.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImplicitSend_MissingStore_RejectsBeforeResolutionOrAdmission(bool withMedia)
    {
        var dispatcher = new Mock<IConversationDispatcher>(MockBehavior.Strict);
        var orchestrator = new CapturingInboundMessageOrchestrator();
        var groups = new Mock<IGroupManager>(MockBehavior.Strict);
        var hub = SignalRHubTests.CreateHub(conversationDispatcher: dispatcher.Object,
            orchestrator: orchestrator, groups: groups.Object, omitConversationStore: true,
            userScopes: [HubScopeGuard.ControlScopeValue]);
        Func<Task> write = withMedia
            ? () => hub.SendMessageWithMedia(AgentId.From("agent-a"), ChannelKey.From("signalr"), "hello", [])
            : () => hub.SendMessage(AgentId.From("agent-a"), ChannelKey.From("signalr"), "hello");

        (await Should.ThrowAsync<HubException>(write)).Message.ShouldContain("404");
        dispatcher.VerifyNoOtherCalls();
        groups.VerifyNoOtherCalls();
        orchestrator.Captured.ShouldBeEmpty();
    }

    private sealed class RecordingResumer : IAskUserCheckpointResumer
    {
        public int Calls { get; private set; }
        public Task ResumeAsync(AskUserRequest request, AskUserResponse response, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }
}
