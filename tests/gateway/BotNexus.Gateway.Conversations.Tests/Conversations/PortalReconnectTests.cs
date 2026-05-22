using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace BotNexus.Gateway.Conversations.Tests.Conversations;

/// <summary>
/// Tests for conversation routing with explicit conversationId (#441, #472).
/// The router must NOT add bindings in the explicit conversationId path (#472 regression fix).
/// The portal always passes an explicit conversationId, so reconnects always use it too.
/// </summary>
public sealed class PortalReconnectTests
{
    private static DefaultConversationRouter CreateRouter(
        IConversationStore? conversationStore = null,
        ISessionStore? sessionStore = null)
        => new(
            conversationStore ?? new InMemoryConversationStore(),
            sessionStore ?? new InMemorySessionStore(),
            NullLogger<DefaultConversationRouter>.Instance);

    /// <summary>
    /// Explicit conversationId path must NOT add a binding (#472 regression fix).
    /// Adding a binding causes the implicit reconnect path to hijack this conversation
    /// when the user switches to a different conversation.
    /// The portal always passes conversationId explicitly, so no binding is needed.
    /// </summary>
    [Fact]
    public async Task ResolveInbound_WithExplicitConversationId_DoesNotAddBinding()
    {
        var store = new InMemoryConversationStore();
        var agentId = AgentId.From("nova");
        var channel = ChannelKey.From("signalr");
        var address = ChannelAddress.From("nova");

        // Pre-create a conversation with NO bindings (REST-created conversation)
        var conv = new Conversation
        {
            ConversationId = ConversationId.Create(),
            AgentId = agentId,
            Title = "My Conversation",
            IsDefault = false
        };
        await store.SaveAsync(conv);

        var router = CreateRouter(store);

        // Send with explicit conversationId
        var result = await router.ResolveInboundAsync(agentId, channel, address, threadId: null, conversationId: conv.ConversationId.Value);

        result.ShouldNotBeNull();
        result.Conversation.ConversationId.ShouldBe(conv.ConversationId);

        // Binding must NOT be added (#472 regression guard)
        var updated = await store.GetAsync(conv.ConversationId);
        updated.ShouldNotBeNull();
        updated!.ChannelBindings.ShouldBeEmpty(
            "explicit conversationId path must not add bindings -- would cause cross-routing on conversation switch (#472)");
    }

    /// <summary>
    /// Explicit path must still route correctly even with no binding.
    /// The portal always passes conversationId, so no binding lookup is needed.
    /// </summary>
    [Fact]
    public async Task ResolveInbound_WithExplicitConversationId_RoutesCorrectly_WithoutBinding()
    {
        var store = new InMemoryConversationStore();
        var agentId = AgentId.From("nova");
        var channel = ChannelKey.From("signalr");
        var address = ChannelAddress.From("nova");

        var conv = new Conversation
        {
            ConversationId = ConversationId.Create(),
            AgentId = agentId,
            Title = "My Conversation",
            IsDefault = false
        };
        await store.SaveAsync(conv);

        var router = CreateRouter(store);

        // First send with explicit conversationId
        var result1 = await router.ResolveInboundAsync(agentId, channel, address, threadId: null, conversationId: conv.ConversationId.Value);
        result1.Conversation.ConversationId.ShouldBe(conv.ConversationId);

        // Second send (reconnect) with same explicit conversationId -- must route to same conversation
        var result2 = await router.ResolveInboundAsync(agentId, channel, address, threadId: null, conversationId: conv.ConversationId.Value);
        result2.Conversation.ConversationId.ShouldBe(conv.ConversationId,
            "repeated explicit conversationId must always route to the same conversation");
    }

    /// <summary>
    /// If the conversation already has a binding for (channelType, channelAddress), do not add a duplicate.
    /// </summary>
    [Fact]
    public async Task ResolveInbound_WithExplicitConversationId_DoesNotDuplicateExistingBinding()
    {
        var store = new InMemoryConversationStore();
        var agentId = AgentId.From("nova");
        var channel = ChannelKey.From("signalr");
        var address = ChannelAddress.From("nova");

        // Pre-create a conversation that ALREADY has the binding
        var bindingId = BindingId.Create();
        var conv = new Conversation
        {
            ConversationId = ConversationId.Create(),
            AgentId = agentId,
            Title = "signalr:nova",
            IsDefault = false
        };
        conv.ChannelBindings.Add(new ChannelBinding
        {
            BindingId = bindingId,
            ChannelType = channel,
            ChannelAddress = address,
            Mode = BindingMode.Interactive
        });
        await store.SaveAsync(conv);

        var router = CreateRouter(store);

        await router.ResolveInboundAsync(agentId, channel, address, threadId: null, conversationId: conv.ConversationId.Value);

        var updated = await store.GetAsync(conv.ConversationId);
        updated.ShouldNotBeNull();
        updated!.ChannelBindings.Count.ShouldBe(1, "no duplicate binding should be added when one already exists");
        updated.ChannelBindings[0].BindingId.ShouldBe(bindingId, "original binding must not be replaced");
    }

    /// <summary>
    /// A muted binding is not reactivated in the explicit path (#472 regression fix).
    /// Muted binding management belongs in the binding management layer, not the routing layer.
    /// </summary>
    [Fact]
    public async Task ResolveInbound_WithExplicitConversationId_DoesNotReactivateMutedBinding()
    {
        var store = new InMemoryConversationStore();
        var agentId = AgentId.From("nova");
        var channel = ChannelKey.From("signalr");
        var address = ChannelAddress.From("nova");

        var conv = new Conversation
        {
            ConversationId = ConversationId.Create(),
            AgentId = agentId,
            Title = "signalr:nova",
            IsDefault = false
        };
        conv.ChannelBindings.Add(new ChannelBinding
        {
            ChannelType = channel,
            ChannelAddress = address,
            Mode = BindingMode.Muted
        });
        await store.SaveAsync(conv);

        var router = CreateRouter(store);

        var result = await router.ResolveInboundAsync(agentId, channel, address, threadId: null, conversationId: conv.ConversationId.Value);

        result.ShouldNotBeNull();
        result.Conversation.ConversationId.ShouldBe(conv.ConversationId,
            "explicit path must still route to the correct conversation even when binding is muted");

        var updated = await store.GetAsync(conv.ConversationId);
        updated.ShouldNotBeNull();
        updated!.ChannelBindings.Count.ShouldBe(1, "no new binding should be added");
        // Muted binding stays muted -- reactivation is not the router's job in the explicit path
        updated.ChannelBindings[0].Mode.ShouldBe(BindingMode.Muted,
            "muted binding must not be changed in explicit conversationId path");
    }
}
