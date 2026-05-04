using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Conversations;

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

        var result = await router.ResolveInboundAsync(agentId, Channel(), "chat-123", null);

        result.ShouldNotBeNull();
        result.Conversation.ShouldNotBeNull();
        result.Conversation.AgentId.ShouldBe(agentId);
        result.Conversation.IsDefault.ShouldBeFalse(); // per-address conversation, not the default
        result.Conversation.ChannelBindings.ShouldHaveSingleItem();
        result.Conversation.ChannelBindings[0].ChannelAddress.ShouldBe("chat-123");
    }

    [Fact]
    public async Task ResolveInbound_CreatesDefaultConversation_ForAddresslessChannel()
    {
        var router = CreateRouter();
        var agentId = Agent();

        var result = await router.ResolveInboundAsync(agentId, Channel(), "", null);

        result.ShouldNotBeNull();
        result.Conversation.IsDefault.ShouldBeTrue(); // no address — falls back to default
    }

    [Fact]
    public async Task ResolveInbound_ReusesExistingConversation_ForKnownBinding()
    {
        var conversationStore = new InMemoryConversationStore();
        var agentId = Agent();
        var channel = Channel("signalr");
        const string address = "conn-abc";

        // Pre-create a conversation with a binding
        var existing = await conversationStore.GetOrCreateDefaultAsync(agentId);
        existing.ChannelBindings.Add(new ChannelBinding
        {
            ChannelType = channel,
            ChannelAddress = address
        });
        await conversationStore.SaveAsync(existing);

        var router = CreateRouter(conversationStore);
        var result = await router.ResolveInboundAsync(agentId, channel, address, null);

        result.Conversation.ConversationId.ShouldBe(existing.ConversationId);
    }

    [Fact]
    public async Task ResolveInbound_StampsSessionConversationId()
    {
        var sessionStore = new InMemorySessionStore();
        var router = CreateRouter(sessionStore: sessionStore);
        var agentId = Agent();

        var result = await router.ResolveInboundAsync(agentId, Channel(), "chat-999", null);

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

        var result = await router.ResolveInboundAsync(agentId, Channel(), "chat-777", null);

        var updated = await conversationStore.GetAsync(result.Conversation.ConversationId);
        updated.ShouldNotBeNull();
        updated!.ActiveSessionId.ShouldBe(result.SessionId);
    }

    [Fact]
    public async Task ResolveInbound_IsNewSession_TrueWhenNoActiveSession()
    {
        var router = CreateRouter();

        var result = await router.ResolveInboundAsync(Agent(), Channel(), "new-chat", null);

        result.IsNewSession.ShouldBeTrue();
    }

    // ── GetOutboundBindingsAsync ───────────────────────────────────────────────

    [Fact]
    public async Task GetOutboundBindings_ExcludesOriginatingBinding()
    {
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var agentId = Agent();

        // Create conversation with two bindings — each with an explicit BindingId
        var conversation = await conversationStore.GetOrCreateDefaultAsync(agentId);
        conversation.ChannelBindings.Add(new ChannelBinding { BindingId = "binding-tg", ChannelType = Channel("telegram"), ChannelAddress = "chat-A", Mode = BindingMode.Interactive });
        conversation.ChannelBindings.Add(new ChannelBinding { BindingId = "binding-sr", ChannelType = Channel("signalr"), ChannelAddress = "conn-B", Mode = BindingMode.Interactive });
        await conversationStore.SaveAsync(conversation);

        var sessionId = SessionId.Create();
        var session = await sessionStore.GetOrCreateAsync(sessionId, agentId);
        session.Session.ConversationId = conversation.ConversationId;
        await sessionStore.SaveAsync(session);

        var router = CreateRouter(conversationStore, sessionStore);
        // Exclude by BindingId, not ChannelAddress
        var bindings = await router.GetOutboundBindingsAsync(sessionId, "binding-tg");

        bindings.ShouldNotBeNull();
        bindings.Count.ShouldBe(1);
        bindings[0].ChannelAddress.ShouldBe("conn-B");
    }

    [Fact]
    public async Task GetOutboundBindings_ExcludesMutedBindings()
    {
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var agentId = Agent();

        var conversation = await conversationStore.GetOrCreateDefaultAsync(agentId);
        conversation.ChannelBindings.Add(new ChannelBinding { BindingId = "binding-orig", ChannelType = Channel("telegram"), ChannelAddress = "originator", Mode = BindingMode.Interactive });
        conversation.ChannelBindings.Add(new ChannelBinding { BindingId = "binding-muted", ChannelType = Channel("signalr"), ChannelAddress = "muted-chan", Mode = BindingMode.Muted });
        conversation.ChannelBindings.Add(new ChannelBinding { BindingId = "binding-active", ChannelType = Channel("slack"), ChannelAddress = "active-chan", Mode = BindingMode.Interactive });
        await conversationStore.SaveAsync(conversation);

        var sessionId = SessionId.Create();
        var session = await sessionStore.GetOrCreateAsync(sessionId, agentId);
        session.Session.ConversationId = conversation.ConversationId;
        await sessionStore.SaveAsync(session);

        var router = CreateRouter(conversationStore, sessionStore);
        var bindings = await router.GetOutboundBindingsAsync(sessionId, "binding-orig");

        bindings.ShouldNotContain(b => b.Mode == BindingMode.Muted);
        bindings.Count.ShouldBe(1);
        bindings[0].ChannelAddress.ShouldBe("active-chan");
    }

    [Fact]
    public async Task GetOutboundBindings_ReturnsEmpty_WhenNoConversationFound()
    {
        var router = CreateRouter();
        var sessionId = SessionId.Create();

        // Session does not exist in store
        var bindings = await router.GetOutboundBindingsAsync(sessionId, "anything");

        bindings.ShouldNotBeNull();
        bindings.Count.ShouldBe(0);
    }

    [Fact]
    public async Task GetOutboundBindings_IncludesNotifyOnlyBindings()
    {
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var agentId = Agent();

        var conversation = await conversationStore.GetOrCreateDefaultAsync(agentId);
        conversation.ChannelBindings.Add(new ChannelBinding { ChannelType = Channel("telegram"), ChannelAddress = "src", Mode = BindingMode.Interactive });
        conversation.ChannelBindings.Add(new ChannelBinding { ChannelType = Channel("slack"), ChannelAddress = "notify-only-chan", Mode = BindingMode.NotifyOnly });
        await conversationStore.SaveAsync(conversation);

        var sessionId = SessionId.Create();
        var session = await sessionStore.GetOrCreateAsync(sessionId, agentId);
        session.Session.ConversationId = conversation.ConversationId;
        await sessionStore.SaveAsync(session);

        var router = CreateRouter(conversationStore, sessionStore);
        var bindings = await router.GetOutboundBindingsAsync(sessionId, "src");

        bindings.ShouldContain(b => b.ChannelAddress == "notify-only-chan" && b.Mode == BindingMode.NotifyOnly);
    }
}
