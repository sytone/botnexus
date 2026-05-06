using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Conversations;

/// <summary>
/// Resolves the active conversation and session for an inbound message,
/// and determines which channel bindings should receive outbound replies.
/// </summary>
public interface IConversationRouter
{
    /// <summary>
    /// Resolves or creates the conversation for an inbound message.
    /// Uses (AgentId, ChannelType, ChannelAddress, ThreadId?) as the lookup key when conversationId is null.
    /// When conversationId is provided, routes directly to that conversation, bypassing binding lookup.
    /// Every channel gets its own conversation on first contact regardless of address.
    /// Stamps Session.ConversationId when creating/resolving sessions.
    /// </summary>
    Task<ConversationRoutingResult> ResolveInboundAsync(
        AgentId agentId,
        ChannelKey channelType,
        string channelAddress,
        string? threadId,
        string? conversationId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Returns channel bindings that should receive outbound fan-out for a session.
    /// Excludes the originating binding from fan-out to prevent echo.
    /// Only returns bindings with BindingMode.Interactive or BindingMode.NotifyOnly.
    /// </summary>
    Task<IReadOnlyList<ChannelBinding>> GetOutboundBindingsAsync(
        SessionId sessionId,
        string? originatingBindingId,
        CancellationToken ct = default);

    /// <summary>
    /// Moves a channel binding from its current conversation to the target conversation.
    /// Used to explicitly share a channel address across conversations (e.g. /share command).
    /// </summary>
    /// <param name="bindingId">The binding to move.</param>
    /// <param name="targetConversationId">The conversation that should own the binding after the call.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ReattachBindingAsync(string bindingId, ConversationId targetConversationId, CancellationToken ct = default);

    /// <summary>
    /// Demotes a channel binding to <see cref="BindingMode.Muted"/> so it no longer receives fan-out.
    /// Used to self-heal stale bindings when a send attempt fails (e.g. SignalR connection gone).
    /// </summary>
    /// <param name="conversationId">The conversation that owns the binding.</param>
    /// <param name="bindingId">The binding to silence.</param>
    /// <param name="ct">Cancellation token.</param>
    Task MuteBindingAsync(ConversationId conversationId, string bindingId, CancellationToken ct = default);

    /// <summary>
    /// Finds and mutes the binding associated with the given channel type and address.
    /// Used by SignalR OnDisconnectedAsync to silence stale bindings by connection ID.
    /// </summary>
    /// <param name="agentId">The agent whose conversations should be searched, or <c>null</c> to search all agents.</param>
    /// <param name="channelType">The channel type (e.g. signalr).</param>
    /// <param name="channelAddress">The channel address (e.g. SignalR connection ID).</param>
    /// <param name="ct">Cancellation token.</param>
    Task MuteBindingByAddressAsync(AgentId? agentId, ChannelKey channelType, string channelAddress, CancellationToken ct = default);
}

/// <summary>
/// Result of resolving an inbound message to a conversation and session.
/// </summary>
/// <param name="Conversation">The resolved or created conversation.</param>
/// <param name="SessionId">The session to dispatch the message into.</param>
/// <param name="IsNewSession">True if a new session was created for this message.</param>
/// <param name="OriginatingBinding">
/// The specific <see cref="ChannelBinding"/> that matched the inbound message's channel type,
/// address, and thread id. Null when no exact binding match was found (e.g. new default conversation).
/// Callers use this to stamp BindingId, ThreadId, and DisplayPrefix on outbound responses without
/// a second lookup into the conversation's binding list.
/// </param>
public sealed record ConversationRoutingResult(
    Conversation Conversation,
    SessionId SessionId,
    bool IsNewSession,
    ChannelBinding? OriginatingBinding = null);
