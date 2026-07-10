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
/// Tests for SignalR portal reconnect duplicate conversation bug (#441).
/// When the portal uses an explicit conversationId, the router must bind that
/// conversation to the (channelType, channelAddress) so reconnects can find it.
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
    /// First send: conversationId provided, conversation has no binding.
    /// Router should add a binding so future reconnects can find it.
    /// </summary>
    [Fact]
    public async Task ResolveInbound_WithExplicitConversationId_AddsBindingWhenNoneExists()
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

        // First send with explicit conversationId, no existing binding
        var result = await router.ResolveInboundAsync(agentId, channel, address, conversationId: conv.ConversationId);

        result.ShouldNotBeNull();
        result.Conversation.ConversationId.ShouldBe(conv.ConversationId);

        // Binding should now exist on the conversation
        var updated = await store.GetAsync(conv.ConversationId);
        updated.ShouldNotBeNull();
        updated!.ChannelBindings.ShouldNotBeEmpty();
        updated.ChannelBindings.ShouldContain(b =>
            b.ChannelType == channel &&
            b.ChannelAddress == address &&
            b.Mode != BindingMode.Muted,
            "a binding must be added so reconnects can find this conversation without an explicit conversationId");
    }

    /// <summary>
    /// After the binding is created, reconnect without conversationId must find the same conversation.
    /// This is the core scenario: no duplicate should be created.
    /// </summary>
    [Fact]
    public async Task ReconnectWithoutConversationId_FindsExistingConversation_AfterFirstSend()
    {
        var store = new InMemoryConversationStore();
        var agentId = AgentId.From("nova");
        var channel = ChannelKey.From("signalr");
        var address = ChannelAddress.From("nova");

        // Pre-create a REST conversation with no bindings
        var conv = new Conversation
        {
            ConversationId = ConversationId.Create(),
            AgentId = agentId,
            Title = "My Conversation",
            IsDefault = false
        };
        await store.SaveAsync(conv);

        var router = CreateRouter(store);

        // First send: explicit conversationId (adds binding)
        await router.ResolveInboundAsync(agentId, channel, address, conversationId: conv.ConversationId);

        // Reconnect: NO conversationId - must find the same conversation
        var reconnectResult = await router.ResolveInboundAsync(agentId, channel, address, conversationId: null);

        reconnectResult.ShouldNotBeNull();
        reconnectResult.Conversation.ConversationId.ShouldBe(conv.ConversationId,
            "reconnect without conversationId must return the same conversation, not create signalr:nova duplicate");
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

        await router.ResolveInboundAsync(agentId, channel, address, conversationId: conv.ConversationId);

        var updated = await store.GetAsync(conv.ConversationId);
        updated.ShouldNotBeNull();
        updated!.ChannelBindings.Count.ShouldBe(1, "no duplicate binding should be added when one already exists");
        updated.ChannelBindings[0].BindingId.ShouldBe(bindingId, "original binding must not be replaced");
    }

    /// <summary>
    /// A muted binding should be reactivated (not duplicated) when the conversation is re-used explicitly.
    /// </summary>
    [Fact]
    public async Task ResolveInbound_WithExplicitConversationId_ReactivatesMutedBinding()
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

        await router.ResolveInboundAsync(agentId, channel, address, conversationId: conv.ConversationId);

        var updated = await store.GetAsync(conv.ConversationId);
        updated.ShouldNotBeNull();
        updated!.ChannelBindings.Count.ShouldBe(1, "muted binding should be reactivated, not duplicated");
        updated.ChannelBindings[0].Mode.ShouldBe(BindingMode.Interactive, "muted binding must be reactivated to Interactive");
    }
}
