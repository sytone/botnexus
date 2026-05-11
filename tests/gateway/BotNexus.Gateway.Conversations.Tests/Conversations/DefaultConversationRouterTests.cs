using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Conversations.Tests.Conversations;

/// <summary>
/// Unit tests for <see cref="DefaultConversationRouter"/>.
/// </summary>
public sealed class DefaultConversationRouterTests
{
    private static AgentId Agent(string id = "agent1") => AgentId.From(id);
    private static ChannelKey Channel(string type = "telegram") => ChannelKey.From(type);

    private static DefaultConversationRouter CreateRouter(
        IConversationStore? conversationStore = null,
        ISessionStore? sessionStore = null)
    {
        return new DefaultConversationRouter(
            conversationStore ?? new InMemoryConversationStore(),
            sessionStore ?? new InMemorySessionStore(),
            NullLogger<DefaultConversationRouter>.Instance);
    }

    // ── ResolveInboundAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveInbound_CreatesPerAddressConversation_ForNewChannelAddress()
    {
        var router = CreateRouter();
        var agentId = Agent();

        var result = await router.ResolveInboundAsync(agentId, Channel(), ChannelAddress.From("chat-123"), null);

        result.ShouldNotBeNull();
        result.Conversation.ShouldNotBeNull();
        result.Conversation.AgentId.ShouldBe(agentId);
        result.Conversation.IsDefault.ShouldBeFalse(); // per-address conversation, not the default
        result.Conversation.ChannelBindings.ShouldHaveSingleItem();
        result.Conversation.ChannelBindings[0].ChannelAddress.ShouldBe(ChannelAddress.From("chat-123"));
    }

    [Fact]
    public async Task ResolveInbound_EmptyAddress_CreatesConversation()
    {
        // Empty address no longer falls back to the default conversation —
        // every channel, even addressless ones, gets its own conversation on first contact.
        var router = CreateRouter();
        var agentId = Agent();

        var result = await router.ResolveInboundAsync(agentId, Channel(), ChannelAddress.From(""), null);

        result.ShouldNotBeNull();
        result.Conversation.ShouldNotBeNull();
        result.Conversation.AgentId.ShouldBe(agentId);
        result.Conversation.ChannelBindings.ShouldHaveSingleItem();
        result.Conversation.ChannelBindings[0].ChannelAddress.ShouldBe(ChannelAddress.From(""));
    }

    [Fact]
    public async Task ResolveInbound_ReusesExistingConversation_ForKnownBinding()
    {
        var conversationStore = new InMemoryConversationStore();
        var agentId = Agent();
        var channel = Channel("signalr");
        const string address = "conn-abc";

        // Pre-create a conversation with a binding
        var existing = await conversationStore.CreateAsync(new BotNexus.Gateway.Abstractions.Models.Conversation { ConversationId = BotNexus.Domain.Primitives.ConversationId.Create(), AgentId = agentId, Title = "test-conversation", IsDefault = false });
        existing.ChannelBindings.Add(new ChannelBinding
        {
            ChannelType = channel,
            ChannelAddress = ChannelAddress.From(address)
        });
        await conversationStore.SaveAsync(existing);

        var router = CreateRouter(conversationStore);
        var result = await router.ResolveInboundAsync(agentId, channel, ChannelAddress.From(address), null);

        result.Conversation.ConversationId.ShouldBe(existing.ConversationId);
    }

    [Fact]
    public async Task ResolveInbound_StampsSessionConversationId()
    {
        var sessionStore = new InMemorySessionStore();
        var router = CreateRouter(sessionStore: sessionStore);
        var agentId = Agent();

        var result = await router.ResolveInboundAsync(agentId, Channel(), ChannelAddress.From("chat-999"), null);

        var session = await sessionStore.GetAsync(result.SessionId);
        session.ShouldNotBeNull();
        session!.Session.ConversationId.ShouldNotBeNull();
        session.Session.ConversationId!.Value.ShouldBe(result.Conversation.ConversationId);
    }

    [Fact]
    public async Task ResolveInbound_StampsConversationActiveSessionId()
    {
        var conversationStore = new InMemoryConversationStore();
        var router = CreateRouter(conversationStore);
        var agentId = Agent();

        var result = await router.ResolveInboundAsync(agentId, Channel(), ChannelAddress.From("chat-777"), null);

        var updated = await conversationStore.GetAsync(result.Conversation.ConversationId);
        updated.ShouldNotBeNull();
        updated!.ActiveSessionId.ShouldBe(result.SessionId);
    }

    [Fact]
    public async Task ResolveInbound_IsNewSession_TrueWhenNoActiveSession()
    {
        var router = CreateRouter();

        var result = await router.ResolveInboundAsync(Agent(), Channel(), ChannelAddress.From("new-chat"), null);

        result.IsNewSession.ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveInbound_SameAgentSameAddress_ReusesConversation()
    {
        // A second message from the same channel:address must land in the same conversation.
        var router = CreateRouter();
        var agentId = Agent();

        var result1 = await router.ResolveInboundAsync(agentId, Channel(), ChannelAddress.From("chat-abc"), null);
        var result2 = await router.ResolveInboundAsync(agentId, Channel(), ChannelAddress.From("chat-abc"), null);

        result2.Conversation.ConversationId.ShouldBe(result1.Conversation.ConversationId);
        result2.SessionId.ShouldBe(result1.SessionId);
    }


    [Fact]
    public async Task ResolveInbound_ReopensArchivedConversation_ForKnownBinding()
    {
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var router = CreateRouter(conversationStore, sessionStore);
        var agentId = Agent();
        var staleSessionId = SessionId.Create();

        var conversation = new Conversation
        {
            ConversationId = ConversationId.Create(),
            AgentId = agentId,
            Title = "archived",
            Status = ConversationStatus.Archived,
            ActiveSessionId = staleSessionId,
            ChannelBindings =
            [
                new ChannelBinding
                {
                    ChannelType = Channel("telegram"),
                    ChannelAddress = ChannelAddress.From("chat-restore")
                },
                new ChannelBinding
                {
                    ChannelType = Channel("signalr"),
                    ChannelAddress = ChannelAddress.From("conn-restore")
                }
            ]
        };
        await conversationStore.CreateAsync(conversation);

        var result = await router.ResolveInboundAsync(agentId, Channel("telegram"), ChannelAddress.From("chat-restore"), null);

        result.Conversation.ConversationId.ShouldBe(conversation.ConversationId);
        result.IsNewSession.ShouldBeTrue();
        var reopened = await conversationStore.GetAsync(conversation.ConversationId);
        reopened.ShouldNotBeNull();
        reopened!.Status.ShouldBe(ConversationStatus.Active);
        reopened.ChannelBindings.Count.ShouldBe(2);
        reopened.ActiveSessionId.ShouldBe(result.SessionId);
    }

    [Fact]
    public async Task ResolveInbound_WithExplicitArchivedConversationId_ReopensConversation()
    {
        var conversationStore = new InMemoryConversationStore();
        var router = CreateRouter(conversationStore);
        var agentId = Agent();

        var conversation = new Conversation
        {
            ConversationId = ConversationId.Create(),
            AgentId = agentId,
            Title = "explicit",
            Status = ConversationStatus.Archived
        };
        await conversationStore.CreateAsync(conversation);

        var result = await router.ResolveInboundAsync(
            agentId,
            Channel("telegram"),
            ChannelAddress.From("unused"),
            null,
            conversation.ConversationId.Value);

        result.Conversation.ConversationId.ShouldBe(conversation.ConversationId);
        var reopened = await conversationStore.GetAsync(conversation.ConversationId);
        reopened.ShouldNotBeNull();
        reopened!.Status.ShouldBe(ConversationStatus.Active);
    }

    [Fact]
    public async Task ReattachBinding_MovesBindingToTargetConversation()
    {
        // After ReattachBinding, the old conversation no longer has the binding
        // and the target conversation does.
        var conversationStore = new InMemoryConversationStore();
        var agentId = Agent();
        var channel = Channel("telegram");

        // Create source conversation with a binding
        var result = await new DefaultConversationRouter(
            conversationStore,
            new InMemorySessionStore(),
            NullLogger<DefaultConversationRouter>.Instance)
            .ResolveInboundAsync(agentId, channel, ChannelAddress.From("addr-src"), null);
        var sourceConversationId = result.Conversation.ConversationId;
        var bindingId = result.Conversation.ChannelBindings[0].BindingId;

        // Create target conversation
        var targetConv = new Conversation
        {
            ConversationId = ConversationId.Create(),
            AgentId = agentId,
            Title = "target"
        };
        await conversationStore.SaveAsync(targetConv);

        var router = CreateRouter(conversationStore);
        await router.ReattachBindingAsync(bindingId, targetConv.ConversationId);

        var source = await conversationStore.GetAsync(sourceConversationId);
        var target = await conversationStore.GetAsync(targetConv.ConversationId);

        source!.ChannelBindings.ShouldNotContain(b => b.BindingId == bindingId);
        target!.ChannelBindings.ShouldContain(b => b.BindingId == bindingId);
    }

    [Fact]
    public async Task ReattachBinding_FanOut_UsesNewConversationBindings()
    {
        // After reattaching a binding to a target conversation, the source conversation
        // no longer has that binding. Fan-out for a new session in the target will
        // include the moved binding alongside any bindings already in the target.
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var agentId = Agent();

        var router = new DefaultConversationRouter(conversationStore, sessionStore, NullLogger<DefaultConversationRouter>.Instance);

        // Source: conversation A with a telegram binding
        var inbound = await router.ResolveInboundAsync(agentId, Channel("telegram"), ChannelAddress.From("addr-a"), null);
        var telegramBindingId = inbound.Conversation.ChannelBindings[0].BindingId;
        var sourceConvId = inbound.Conversation.ConversationId;

        // Target: conversation B with a slack binding
        var targetConv = new Conversation
        {
            ConversationId = ConversationId.Create(),
            AgentId = agentId,
            Title = "target"
        };
        targetConv.ChannelBindings.Add(new ChannelBinding
        {
            ChannelType = Channel("slack"),
            ChannelAddress = ChannelAddress.From("slack-channel"),
            Mode = BindingMode.Interactive
        });
        await conversationStore.SaveAsync(targetConv);

        // Move telegram binding to target
        await router.ReattachBindingAsync(telegramBindingId, targetConv.ConversationId);

        // Source conversation should no longer have the telegram binding
        var source = await conversationStore.GetAsync(sourceConvId);
        source!.ChannelBindings.ShouldNotContain(b => b.BindingId == telegramBindingId);

        // Target conversation should now have both telegram and slack bindings
        var target = await conversationStore.GetAsync(targetConv.ConversationId);
        target!.ChannelBindings.ShouldContain(b => b.BindingId == telegramBindingId);
        target.ChannelBindings.ShouldContain(b => b.ChannelAddress == ChannelAddress.From("slack-channel"));
    }

    // ── GetOutboundBindingsAsync ───────────────────────────────────────────────

    [Fact]
    public async Task GetOutboundBindings_ExcludesOriginatingBinding()
    {
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var agentId = Agent();

        // Create conversation with two bindings — each with an explicit BindingId
        var conversation = await conversationStore.CreateAsync(new BotNexus.Gateway.Abstractions.Models.Conversation { ConversationId = BotNexus.Domain.Primitives.ConversationId.Create(), AgentId = agentId, Title = "test-conversation", IsDefault = false });
        conversation.ChannelBindings.Add(new ChannelBinding { BindingId = BindingId.From("binding-tg"), ChannelType = Channel("telegram"), ChannelAddress = ChannelAddress.From("chat-A"), Mode = BindingMode.Interactive });
        conversation.ChannelBindings.Add(new ChannelBinding { BindingId = BindingId.From("binding-sr"), ChannelType = Channel("signalr"), ChannelAddress = ChannelAddress.From("conn-B"), Mode = BindingMode.Interactive });
        await conversationStore.SaveAsync(conversation);

        var sessionId = SessionId.Create();
        var session = await sessionStore.GetOrCreateAsync(sessionId, agentId);
        session.Session.ConversationId = conversation.ConversationId;
        await sessionStore.SaveAsync(session);

        var router = CreateRouter(conversationStore, sessionStore);
        // Exclude by BindingId, not ChannelAddress
        var bindings = await router.GetOutboundBindingsAsync(sessionId, BindingId.From("binding-tg"));

        bindings.ShouldNotBeNull();
        bindings.Count.ShouldBe(1);
        bindings[0].ChannelAddress.ShouldBe(ChannelAddress.From("conn-B"));
    }

    [Fact]
    public async Task GetOutboundBindings_ExcludesMutedBindings()
    {
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var agentId = Agent();

        var conversation = await conversationStore.CreateAsync(new BotNexus.Gateway.Abstractions.Models.Conversation { ConversationId = BotNexus.Domain.Primitives.ConversationId.Create(), AgentId = agentId, Title = "test-conversation", IsDefault = false });
        conversation.ChannelBindings.Add(new ChannelBinding { BindingId = BindingId.From("binding-orig"), ChannelType = Channel("telegram"), ChannelAddress = ChannelAddress.From("originator"), Mode = BindingMode.Interactive });
        conversation.ChannelBindings.Add(new ChannelBinding { BindingId = BindingId.From("binding-muted"), ChannelType = Channel("signalr"), ChannelAddress = ChannelAddress.From("muted-chan"), Mode = BindingMode.Muted });
        conversation.ChannelBindings.Add(new ChannelBinding { BindingId = BindingId.From("binding-active"), ChannelType = Channel("slack"), ChannelAddress = ChannelAddress.From("active-chan"), Mode = BindingMode.Interactive });
        await conversationStore.SaveAsync(conversation);

        var sessionId = SessionId.Create();
        var session = await sessionStore.GetOrCreateAsync(sessionId, agentId);
        session.Session.ConversationId = conversation.ConversationId;
        await sessionStore.SaveAsync(session);

        var router = CreateRouter(conversationStore, sessionStore);
        var bindings = await router.GetOutboundBindingsAsync(sessionId, BindingId.From("binding-orig"));

        bindings.ShouldNotContain(b => b.Mode == BindingMode.Muted);
        bindings.Count.ShouldBe(1);
        bindings[0].ChannelAddress.ShouldBe(ChannelAddress.From("active-chan"));
    }

    [Fact]
    public async Task GetOutboundBindings_ReturnsEmpty_WhenNoConversationFound()
    {
        var router = CreateRouter();
        var sessionId = SessionId.Create();

        // Session does not exist in store
        var bindings = await router.GetOutboundBindingsAsync(sessionId, BindingId.From("anything"));

        bindings.ShouldNotBeNull();
        bindings.Count.ShouldBe(0);
    }

    [Fact]
    public async Task GetOutboundBindings_IncludesNotifyOnlyBindings()
    {
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var agentId = Agent();

        var conversation = await conversationStore.CreateAsync(new BotNexus.Gateway.Abstractions.Models.Conversation { ConversationId = BotNexus.Domain.Primitives.ConversationId.Create(), AgentId = agentId, Title = "test-conversation", IsDefault = false });
        conversation.ChannelBindings.Add(new ChannelBinding { ChannelType = Channel("telegram"), ChannelAddress = ChannelAddress.From("src"), Mode = BindingMode.Interactive });
        conversation.ChannelBindings.Add(new ChannelBinding { ChannelType = Channel("slack"), ChannelAddress = ChannelAddress.From("notify-only-chan"), Mode = BindingMode.NotifyOnly });
        await conversationStore.SaveAsync(conversation);

        var sessionId = SessionId.Create();
        var session = await sessionStore.GetOrCreateAsync(sessionId, agentId);
        session.Session.ConversationId = conversation.ConversationId;
        await sessionStore.SaveAsync(session);

        var router = CreateRouter(conversationStore, sessionStore);
        var bindings = await router.GetOutboundBindingsAsync(sessionId, BindingId.From("src"));

        bindings.ShouldContain(b => b.ChannelAddress == ChannelAddress.From("notify-only-chan") && b.Mode == BindingMode.NotifyOnly);
    }
}

