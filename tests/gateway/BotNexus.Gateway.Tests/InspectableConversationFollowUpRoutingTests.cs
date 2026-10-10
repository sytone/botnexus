using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Channels.SignalR;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class InspectableConversationFollowUpRoutingTests
{
    public static IEnumerable<object[]> RoutingCases()
    {
        foreach (var linked in new[] { false, true })
        foreach (var archived in new[] { false, true })
        foreach (var media in new[] { false, true })
        foreach (var visibility in Enum.GetValues<ConversationVisibility>())
        foreach (var idleHandle in new[] { false, true })
            yield return [linked, archived, media, visibility, idleHandle];
    }

    [Theory]
    [MemberData(nameof(RoutingCases))]
    public async Task IdleFollowUp_ChecksAndPinsActualDestinationBeforeRouting(
        bool linked, bool archived, bool media, ConversationVisibility visibility, bool idleHandle)
    {
        var store = new InMemoryConversationStore();
        var sessions = new InMemorySessionStore();
        var bound = InspectableConversationHttpWriteTests.CreateConversation("c_bound", visibility);
        bound.Status = archived ? ConversationStatus.Archived : ConversationStatus.Active;
        bound.ChannelBindings.Add(new ChannelBinding
        {
            ChannelType = ChannelKey.From("signalr"), ChannelAddress = ChannelAddress.From("agent-a"),
            Mode = BindingMode.Interactive
        });
        var boundSession = await sessions.GetOrCreateAsync(SessionId.From("s_bound"), bound.AgentId);
        boundSession.ConversationId = bound.ConversationId;
        boundSession.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "bound transcript" });
        await sessions.SaveAsync(boundSession);
        bound.ActiveSessionId = boundSession.SessionId;
        bound = await store.CreateAsync(bound);
        var beforeBoundSession = SnapshotSession(await sessions.GetAsync(boundSession.SessionId));
        var supplied = await sessions.GetOrCreateAsync(SessionId.From("s_supplied"), bound.AgentId);
        var writable = InspectableConversationHttpWriteTests.CreateConversation("c_parent", ConversationVisibility.UserFacing);
        writable.ActiveSessionId = supplied.SessionId;
        writable = await store.CreateAsync(writable);
        if (linked) supplied.ConversationId = writable.ConversationId;
        supplied.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "original transcript" });
        await sessions.SaveAsync(supplied);
        var beforeBound = InspectableConversationHttpWriteTests.Snapshot(await store.GetAsync(bound.ConversationId));
        var beforeConversations = JsonSerializer.Serialize(await store.ListAsync(bound.AgentId));
        var beforeSessions = SnapshotSessions(await sessions.ListAsync());
        var router = new DefaultConversationRouter(store, sessions, NullLogger<DefaultConversationRouter>.Instance);
        var processor = new RoutingProcessor(new DefaultConversationDispatcher(router, store), sessions);
        var supervisor = new Mock<IAgentSupervisor>(MockBehavior.Strict);
        var queueAttempts = new List<string>();
        var handle = new Mock<IAgentHandle>(MockBehavior.Strict);
        handle.Setup(value => value.TryFollowUpWhileRunningAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((content, _) => queueAttempts.Add(content)).ReturnsAsync(false);
        handle.Setup(value => value.TryFollowUpWhileRunningAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Callback<AgentUserMessage, CancellationToken>((message, _) => queueAttempts.Add(message.Content)).ReturnsAsync(false);
        supervisor.Setup(value => value.GetHandle(bound.AgentId, supplied.SessionId)).Returns(idleHandle ? handle.Object : null);
        var groups = new Mock<IGroupManager>();
        groups.Setup(value => value.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var hub = SignalRHubTests.CreateHub(sessions: sessions, conversationStore: store,
            orchestrator: processor, supervisor: supervisor.Object, groups: groups.Object,
            userScopes: [HubScopeGuard.ControlScopeValue]);
        Func<Task> write = media
            ? () => hub.FollowUpWithMedia(bound.AgentId, supplied.SessionId, "", [new() { MimeType = "text/plain", Text = "client attachment" }])
            : () => hub.FollowUp(bound.AgentId, supplied.SessionId, "client text");

        if (!linked && visibility != ConversationVisibility.UserFacing)
        {
            (await Should.ThrowAsync<HubException>(write)).Message.ShouldContain(visibility == ConversationVisibility.InternalHidden ? "404" : "403");
            processor.Dispatches.ShouldBeEmpty();
            queueAttempts.ShouldBeEmpty();
            handle.VerifyNoOtherCalls();
            supervisor.Verify(value => value.GetHandle(It.IsAny<AgentId>(), It.IsAny<SessionId>()), Times.Never);
            groups.Verify(value => value.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            JsonSerializer.Serialize(await store.ListAsync(bound.AgentId)).ShouldBe(beforeConversations);
            SnapshotSessions(await sessions.ListAsync()).ShouldBe(beforeSessions);
        }
        else
        {
            await write();
            await (hub.LastFollowUpDispatch ?? throw new InvalidOperationException("No follow-up dispatch"));
            var dispatch = processor.Dispatches.ShouldHaveSingleItem();
            var expected = linked ? writable.ConversationId : bound.ConversationId;
            dispatch.Context.RequestedConversationId.ShouldBe(expected);
            dispatch.Resolution.ConversationId.ShouldBe(expected);
            dispatch.Context.RequestedSessionId.ShouldBe(supplied.SessionId);
            if (media) dispatch.Context.Message.ContentParts.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBeOfType<TextContentPart>().Text.ShouldBe("client attachment");
            else dispatch.Context.Message.Content.ShouldBe("client text");
            (await sessions.GetAsync(dispatch.Resolution.SessionId)).ShouldNotBeNull().History.ShouldContain(entry => entry.Role == MessageRole.User);
            if (linked)
            {
                InspectableConversationHttpWriteTests.Snapshot(await store.GetAsync(bound.ConversationId)).ShouldBe(beforeBound);
                SnapshotSession(await sessions.GetAsync(boundSession.SessionId)).ShouldBe(beforeBoundSession);
            }
            if (idleHandle) queueAttempts.ShouldHaveSingleItem();
            else queueAttempts.ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyFollowUp_NoBinding_AllowsNewUserFacingDestination(bool media)
    {
        var store = new InMemoryConversationStore();
        var sessions = new InMemorySessionStore();
        var agentId = AgentId.From("agent-a");
        var supplied = await sessions.GetOrCreateAsync(SessionId.From("s_legacy"), agentId);
        var router = new DefaultConversationRouter(store, sessions, NullLogger<DefaultConversationRouter>.Instance);
        var processor = new RoutingProcessor(new DefaultConversationDispatcher(router, store), sessions);
        var hub = SignalRHubTests.CreateHub(sessions: sessions, conversationStore: store, orchestrator: processor);
        if (media) await hub.FollowUpWithMedia(agentId, supplied.SessionId, "", [new() { MimeType = "text/plain", Text = "attachment" }]);
        else await hub.FollowUp(agentId, supplied.SessionId, "text");
        await (hub.LastFollowUpDispatch ?? throw new InvalidOperationException("No dispatch"));
        var dispatch = processor.Dispatches.ShouldHaveSingleItem();
        (await store.GetAsync(dispatch.Resolution.ConversationId)).ShouldNotBeNull().Visibility.ShouldBe(ConversationVisibility.UserFacing);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FollowUp_MissingStore_RejectsUnknownDestinationBeforeAnyAdmission(bool linked, bool media)
    {
        var sessions = new InMemorySessionStore();
        var supplied = await sessions.GetOrCreateAsync(SessionId.From("s_missing_store"), AgentId.From("agent-a"));
        if (linked) supplied.ConversationId = ConversationId.From("c_missing_store");
        await sessions.SaveAsync(supplied);
        var before = SnapshotSessions(await sessions.ListAsync());
        var processor = new Mock<IInboundMessageOrchestrator>(MockBehavior.Strict);
        var supervisor = new Mock<IAgentSupervisor>(MockBehavior.Strict);
        var groups = new Mock<IGroupManager>(MockBehavior.Strict);
        var hub = SignalRHubTests.CreateHub(sessions: sessions, omitConversationStore: true,
            orchestrator: processor.Object, supervisor: supervisor.Object, groups: groups.Object);
        Func<Task> write = media
            ? () => hub.FollowUpWithMedia(supplied.AgentId, supplied.SessionId, "", [new() { MimeType = "text/plain", Text = "attachment" }])
            : () => hub.FollowUp(supplied.AgentId, supplied.SessionId, "text");
        (await Should.ThrowAsync<HubException>(write)).Message.ShouldContain("404");
        processor.VerifyNoOtherCalls();
        supervisor.VerifyNoOtherCalls();
        groups.VerifyNoOtherCalls();
        SnapshotSessions(await sessions.ListAsync()).ShouldBe(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveLinkedFollowUp_WritableParent_IgnoresDifferentReadOnlyBinding(bool media)
    {
        var store = new InMemoryConversationStore();
        var sessions = new InMemorySessionStore();
        var bound = InspectableConversationHttpWriteTests.CreateConversation("c_live_bound", ConversationVisibility.InspectableReadOnly);
        bound.ChannelBindings.Add(new ChannelBinding { ChannelType = ChannelKey.From("signalr"), ChannelAddress = ChannelAddress.From("agent-a") });
        bound = await store.CreateAsync(bound);
        var parent = await store.CreateAsync(InspectableConversationHttpWriteTests.CreateConversation("c_live_parent", ConversationVisibility.UserFacing));
        var supplied = await sessions.GetOrCreateAsync(SessionId.From("s_live"), parent.AgentId);
        supplied.ConversationId = parent.ConversationId;
        await sessions.SaveAsync(supplied);
        var beforeBound = InspectableConversationHttpWriteTests.Snapshot(bound);
        var beforeSessions = SnapshotSessions(await sessions.ListAsync());
        var queue = new List<string>();
        var handle = new Mock<IAgentHandle>();
        handle.Setup(value => value.TryFollowUpWhileRunningAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((content, _) => queue.Add(content)).ReturnsAsync(true);
        handle.Setup(value => value.TryFollowUpWhileRunningAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Callback<AgentUserMessage, CancellationToken>((message, _) => queue.Add(message.Content)).ReturnsAsync(true);
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(value => value.GetHandle(parent.AgentId, supplied.SessionId)).Returns(handle.Object);
        var router = new DefaultConversationRouter(store, sessions, NullLogger<DefaultConversationRouter>.Instance);
        var processor = new RoutingProcessor(new DefaultConversationDispatcher(router, store), sessions);
        var hub = SignalRHubTests.CreateHub(sessions: sessions, conversationStore: store, orchestrator: processor, supervisor: supervisor.Object);
        if (media) await hub.FollowUpWithMedia(parent.AgentId, supplied.SessionId, "", [new() { MimeType = "text/plain", Text = "live attachment" }]);
        else await hub.FollowUp(parent.AgentId, supplied.SessionId, "live text");
        await (hub.LastFollowUpDispatch ?? throw new InvalidOperationException("No dispatch"));
        queue.ShouldHaveSingleItem().ShouldContain(media ? "live attachment" : "live text");
        processor.Dispatches.ShouldBeEmpty();
        InspectableConversationHttpWriteTests.Snapshot(await store.GetAsync(bound.ConversationId)).ShouldBe(beforeBound);
        SnapshotSessions(await sessions.ListAsync()).ShouldBe(beforeSessions);
    }

    private static string SnapshotSession(GatewaySession? session)
        => JsonSerializer.Serialize(ProjectSession(session));

    private static string SnapshotSessions(IReadOnlyList<GatewaySession> sessions)
        => JsonSerializer.Serialize(sessions.Select(ProjectSession));

    // Legacy/unlinked fixtures intentionally retain the uninitialized ConversationId sentinel.
    // Project every distinct session field without serializing that sentinel or the redundant
    // Session/Runtime facades. Include replay events as well as the serialized sequence counter.
    private static object? ProjectSession(GatewaySession? session)
        => session is null ? null : new
        {
            session.SessionId,
            session.AgentId,
            session.ChannelType,
            session.CallerId,
            session.SessionType,
            ConversationId = session.ConversationId.IsInitialized() ? session.ConversationId.Value : null,
            session.IsInteractive,
            session.CreatedAt,
            session.UpdatedAt,
            session.Status,
            session.ExpiresAt,
            session.History,
            session.MessageCount,
            session.LastRenderedSystemPrompt,
            session.LastRenderedSystemPromptAt,
            session.Metadata,
            session.ExchangeCompletion,
            session.RunCompletion,
            StreamReplay = new
            {
                session.StreamReplay.NextSequenceId,
                Events = session.StreamReplay.GetEventSnapshot()
            }
        };

    // Accept executes the same typed context projection and real dispatcher/router as production,
    // then appends a user entry to the resolved session. A hint-only capture cannot catch misrouting.
    private sealed class RoutingProcessor(DefaultConversationDispatcher dispatcher, InMemorySessionStore sessions) : IInboundMessageOrchestrator
    {
        public List<DispatchResult> Dispatches { get; } = [];
        public async Task<InboundDispatchResult> AcceptAsync(InboundMessage message, CancellationToken cancellationToken = default)
        {
            var agentId = message.RoutingHints?.RequestedAgentId ?? throw new InvalidOperationException("No agent hint");
            var dispatch = await dispatcher.DispatchAsync(InboundMessageContext.FromInboundMessage(agentId, message), cancellationToken);
            Dispatches.Add(dispatch);
            var session = await sessions.GetAsync(dispatch.Resolution.SessionId, cancellationToken) ?? throw new InvalidOperationException("No routed session");
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = message.Content });
            await sessions.SaveAsync(session, cancellationToken);
            return InboundDispatchResult.Accepted([dispatch]);
        }
        public async Task<InboundDispatchStatus> PostAsync(InboundMessage message, CancellationToken cancellationToken = default)
            => (await AcceptAsync(message, cancellationToken)).Status;
        public bool Post(InboundMessage message) => throw new InvalidOperationException("Unexpected fire-and-forget admission");
    }
}
