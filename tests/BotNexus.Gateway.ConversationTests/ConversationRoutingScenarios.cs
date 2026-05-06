using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;

namespace BotNexus.Gateway.ConversationTests;

/// <summary>
/// End-to-end conversation routing scenarios using real in-memory stores and a real
/// <see cref="DefaultConversationRouter"/>. No network, no external process.
///
/// These tests validate the conversation-first routing model introduced by the
/// refactor/conversation-first-routing branch:
///  - InboundMessage.ConversationId bypasses binding lookup (direct routing)
///  - Binding lookup (channelType, channelAddress, threadId) is the fallback
///  - No duplicate bindings, no double fan-out from portal secondary conversations
/// </summary>
public sealed class ConversationRoutingScenarios
{
    private static AgentId Agent(string id = "agent1") => AgentId.From(id);
    private static ChannelKey Telegram() => ChannelKey.From("telegram");
    private static ChannelKey SignalR() => ChannelKey.From("signalr");

    // ──────────────────────────────────────────────────────────────────────────────
    // Scenario 1: Single channel, single conversation — Telegram DM always same conv
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Scenario1_TelegramDm_AlwaysRoutesToSameConversation()
    {
        // Arrange
        var convStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var router = CreateRouter(convStore, sessionStore);
        var agentId = Agent();

        // Act — two messages from the same Telegram chat ID
        var result1 = await router.ResolveInboundAsync(agentId, Telegram(), ChannelAddress.From("chat-123"), null);
        var result2 = await router.ResolveInboundAsync(agentId, Telegram(), ChannelAddress.From("chat-123"), null);

        // Assert — same conversation, same session
        result1.Conversation.ConversationId.ShouldBe(result2.Conversation.ConversationId,
            "repeated messages from same Telegram DM must route to same conversation");
        result2.IsNewSession.ShouldBeFalse("session must be reused on second message");
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Scenario 2: Portal default conversation — null conversationId → default
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Scenario2_Portal_NullConversationId_RoutesToDefaultConversation()
    {
        // Arrange
        var convStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var router = CreateRouter(convStore, sessionStore);
        var agentId = Agent();

        // Act — portal sends with null conversationId (binding lookup path)
        var result1 = await router.ResolveInboundAsync(agentId, SignalR(), ChannelAddress.From("agent1"), null);
        var result2 = await router.ResolveInboundAsync(agentId, SignalR(), ChannelAddress.From("agent1"), null);

        // Assert — same conversation on both calls (binding found on second call)
        result1.Conversation.ConversationId.ShouldBe(result2.Conversation.ConversationId,
            "null conversationId should resolve to the same portal conversation each time");
        result2.IsNewSession.ShouldBeFalse("session must be reused");
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Scenario 3: Portal secondary conversation — explicit conversationId → direct
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Scenario3_Portal_ExplicitConversationId_RoutesDirectlyNoDuplicateBindings()
    {
        // Arrange
        var convStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var router = CreateRouter(convStore, sessionStore);
        var agentId = Agent();

        // Pre-create a secondary conversation (as the portal API would)
        var secondaryConv = new Conversation
        {
            ConversationId = ConversationId.Create(),
            AgentId = agentId,
            Title = "Secondary"
        };
        await convStore.SaveAsync(secondaryConv);

        // Act — portal sends with explicit conversationId (direct routing path)
        var result = await router.ResolveInboundAsync(
            agentId, SignalR(), ChannelAddress.From("agent1"), null,
            conversationId: secondaryConv.ConversationId.Value);

        // Assert — routes to the correct conversation with NO new binding added
        result.Conversation.ConversationId.ShouldBe(secondaryConv.ConversationId,
            "explicit conversationId must route directly to that conversation");

        // The critical invariant: no duplicate thread binding was added
        // (old design added a binding with ThreadId=conversationId, causing double fan-out)
        var conv = await convStore.GetAsync(secondaryConv.ConversationId);
        conv.ShouldNotBeNull();
        conv!.ChannelBindings.Count.ShouldBe(0,
            "direct conversationId routing must NOT add a binding — no binding hack");
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Scenario 4: Cross-channel fan-out — Telegram + portal bound to same conversation
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Scenario4_CrossChannelFanOut_BothBindingsReceiveOutbound()
    {
        // Arrange — a conversation with both Telegram and SignalR bindings
        var convStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var router = CreateRouter(convStore, sessionStore);
        var agentId = Agent();

        // First contact via Telegram creates the conversation with a Telegram binding
        var result = await router.ResolveInboundAsync(agentId, Telegram(), ChannelAddress.From("chat-789"), null);
        var conversationId = result.Conversation.ConversationId;

        // Attach a SignalR binding (simulates portal joining the same conversation)
        var conv = await convStore.GetAsync(conversationId);
        conv!.ChannelBindings.Add(new ChannelBinding
        {
            ChannelType = SignalR(),
            ChannelAddress = ChannelAddress.From("conn-abc"),
            Mode = BindingMode.Interactive
        });
        await convStore.SaveAsync(conv);

        // Act — get outbound bindings for fan-out
        var sessionId = result.SessionId;
        var outbound = await router.GetOutboundBindingsAsync(sessionId, originatingBindingId: null);

        // Assert — both bindings are available for fan-out
        outbound.Count.ShouldBe(2,
            "cross-channel fan-out should include both Telegram and SignalR bindings");
        outbound.ShouldContain(b => b.ChannelType == Telegram());
        outbound.ShouldContain(b => b.ChannelType == SignalR());
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Scenario 5: Session reuse after expiry — expired session is reactivated
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Scenario5_ExpiredSession_IsReusedNotReplaced()
    {
        // Arrange
        var convStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var router = CreateRouter(convStore, sessionStore);
        var agentId = Agent();

        // First message creates session
        var result1 = await router.ResolveInboundAsync(agentId, Telegram(), ChannelAddress.From("chat-exp"), null);
        var originalSessionId = result1.SessionId;

        // Expire the session
        var session = await sessionStore.GetAsync(originalSessionId);
        session!.Status = Abstractions.Models.SessionStatus.Expired;
        await sessionStore.SaveAsync(session);

        // Act — second message should reuse the expired session (not create a new one)
        var result2 = await router.ResolveInboundAsync(agentId, Telegram(), ChannelAddress.From("chat-exp"), null);

        // Assert — same session reused (GatewayHost reactivates expired sessions)
        result2.SessionId.ShouldBe(originalSessionId,
            "expired sessions must be reused — GatewayHost handles reactivation, not the router");
        result2.IsNewSession.ShouldBeFalse();
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Scenario 6: Session sealed → new session is created
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Scenario6_SealedSession_TriggersNewSessionCreation()
    {
        // Arrange
        var convStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var router = CreateRouter(convStore, sessionStore);
        var agentId = Agent();

        // First message creates session
        var result1 = await router.ResolveInboundAsync(agentId, Telegram(), ChannelAddress.From("chat-seal"), null);
        var originalSessionId = result1.SessionId;

        // Seal the session (simulates an explicit reset/archive)
        var session = await sessionStore.GetAsync(originalSessionId);
        session!.Status = Abstractions.Models.SessionStatus.Sealed;
        await sessionStore.SaveAsync(session);

        // Act — next message must create a new session
        var result2 = await router.ResolveInboundAsync(agentId, Telegram(), ChannelAddress.From("chat-seal"), null);

        // Assert
        result2.SessionId.ShouldNotBe(originalSessionId,
            "sealed session must trigger new session creation");
        result2.IsNewSession.ShouldBeTrue();

        // Same conversation — session is replaced but conversation is preserved
        result2.Conversation.ConversationId.ShouldBe(result1.Conversation.ConversationId,
            "conversation must be preserved even after session replacement");
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Scenario 7: Multiple agents, same channel — separate conversations
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Scenario7_TwoAgents_SameTelegramBot_GetSeparateConversations()
    {
        // Arrange
        var convStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var routerA = CreateRouter(convStore, sessionStore);
        var routerB = CreateRouter(convStore, sessionStore);
        var agentA = Agent("agent-a");
        var agentB = Agent("agent-b");

        // Act — same Telegram chat ID sends to both agents
        var resultA = await routerA.ResolveInboundAsync(agentA, Telegram(), ChannelAddress.From("shared-chat"), null);
        var resultB = await routerB.ResolveInboundAsync(agentB, Telegram(), ChannelAddress.From("shared-chat"), null);

        // Assert — each agent gets its own conversation
        resultA.Conversation.ConversationId.ShouldNotBe(resultB.Conversation.ConversationId,
            "different agents on same channel must have separate conversations");
        resultA.Conversation.AgentId.ShouldBe(agentA);
        resultB.Conversation.AgentId.ShouldBe(agentB);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Scenario 8: Thread isolation — different forum topics are separate conversations
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Scenario8_DifferentThreadIds_GetSeparateConversations()
    {
        // Arrange
        var convStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var router = CreateRouter(convStore, sessionStore);
        var agentId = Agent();

        // Act — same group chat but different topics
        var result42 = await router.ResolveInboundAsync(agentId, Telegram(), ChannelAddress.From("group-1"), ThreadId.From("42"));
        var result99 = await router.ResolveInboundAsync(agentId, Telegram(), ChannelAddress.From("group-1"), ThreadId.From("99"));

        // Assert — thread isolation: different topics → different conversations
        result42.Conversation.ConversationId.ShouldNotBe(result99.Conversation.ConversationId,
            "Telegram forum topics 42 and 99 must route to separate conversations");

        // Verify bindings have correct thread IDs
        result42.OriginatingBinding.ShouldNotBeNull();
        result42.OriginatingBinding!.ThreadId.ShouldBe(ThreadId.From("42"));
        result99.OriginatingBinding.ShouldNotBeNull();
        result99.OriginatingBinding!.ThreadId.ShouldBe(ThreadId.From("99"));
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Scenario 9: ConversationId routing bypasses binding lookup entirely
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Scenario9_ExplicitConversationId_SkipsBindingLookup()
    {
        // Arrange — two separate conversations for the same agent
        var convStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore();
        var router = CreateRouter(convStore, sessionStore);
        var agentId = Agent();

        // Create conversation A via binding lookup
        var resultA = await router.ResolveInboundAsync(agentId, SignalR(), ChannelAddress.From("agent1"), null);
        var convAId = resultA.Conversation.ConversationId;

        // Create conversation B via API (no binding)
        var convB = new Conversation
        {
            ConversationId = ConversationId.Create(),
            AgentId = agentId,
            Title = "Conversation B"
        };
        await convStore.SaveAsync(convB);

        // Act — send to conversation B by explicit ID
        // Note: (SignalR, "agent1", null) binding points to conversation A,
        // but we override with conversationId=B
        var resultB = await router.ResolveInboundAsync(
            agentId, SignalR(), ChannelAddress.From("agent1"), null,
            conversationId: convB.ConversationId.Value);

        // Assert — routed to conversation B, not A
        resultB.Conversation.ConversationId.ShouldBe(convB.ConversationId,
            "explicit conversationId must bypass binding lookup and route directly");
        resultB.Conversation.ConversationId.ShouldNotBe(convAId,
            "must NOT fall back to the binding-matched conversation A");

        // Conversation A's binding is untouched — no duplicate bindings
        var updatedConvA = await convStore.GetAsync(convAId);
        updatedConvA.ShouldNotBeNull();
        // Conversation A still has only the original binding
        updatedConvA!.ChannelBindings.Count.ShouldBe(1);
        updatedConvA.ChannelBindings[0].ChannelType.ShouldBe(SignalR());
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────────

    private static DefaultConversationRouter CreateRouter(
        IConversationStore convStore,
        ISessionStore sessionStore)
        => new(convStore, sessionStore, NullLogger<DefaultConversationRouter>.Instance);
}
