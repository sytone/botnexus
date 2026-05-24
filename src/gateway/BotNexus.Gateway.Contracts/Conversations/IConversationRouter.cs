using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
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
    /// Uses (AgentId, ChannelType, ChannelAddress) as the lookup key when conversationId is null.
    /// When conversationId is provided, routes directly to that conversation, bypassing binding lookup.
    /// Every channel gets its own conversation on first contact regardless of address.
    /// Stamps Session.ConversationId when creating/resolving sessions.
    /// </summary>
    /// <remarks>
    /// Native sub-addresses (e.g. Telegram forum topics) are encoded into
    /// <paramref name="channelAddress"/> by the originating adapter — the router treats
    /// the address as opaque.
    /// </remarks>
    /// <param name="agentId">The owning agent for the resolved conversation.</param>
    /// <param name="channelType">Channel species (e.g. signalr, telegram, tui).</param>
    /// <param name="channelAddress">Channel-specific address — opaque to the router.</param>
    /// <param name="conversationId">Explicit conversation id to bypass binding lookup.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="initiator">
    /// The citizen who triggered this resolution — typically the inbound message's resolved
    /// <see cref="Domain.World.CitizenId"/> sender. Stamped onto <see cref="Conversation.Initiator"/>
    /// when a new conversation is created; ignored for existing conversations to preserve the
    /// write-once provenance invariant. Pass <c>null</c> when the initiator is unknown (legacy /
    /// system-system paths); existing conversations also remain <c>null</c> until they are next
    /// created fresh.
    /// </param>
    Task<ConversationRoutingResult> ResolveInboundAsync(
        AgentId agentId,
        ChannelKey channelType,
        ChannelAddress channelAddress,
        string? conversationId = null,
        CancellationToken ct = default,
        CitizenId? initiator = null);

    /// <summary>
    /// Returns channel bindings that should receive outbound fan-out for a session.
    /// Excludes the originating binding from fan-out to prevent echo.
    /// Only returns bindings with BindingMode.Interactive or BindingMode.NotifyOnly.
    /// </summary>
    Task<IReadOnlyList<ChannelBinding>> GetOutboundBindingsAsync(
        SessionId sessionId,
        BindingId? originatingBindingId,
        CancellationToken ct = default);

    /// <summary>
    /// Moves a channel binding from its current conversation to the target conversation.
    /// Used to explicitly share a channel address across conversations (e.g. /share command).
    /// </summary>
    /// <param name="bindingId">The binding to move.</param>
    /// <param name="targetConversationId">The conversation that should own the binding after the call.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ReattachBindingAsync(BindingId bindingId, ConversationId targetConversationId, CancellationToken ct = default);

    /// <summary>
    /// Demotes a channel binding to <see cref="BindingMode.Muted"/> so it no longer receives fan-out.
    /// Used to self-heal stale bindings when a send attempt fails (e.g. SignalR connection gone).
    /// </summary>
    /// <param name="conversationId">The conversation that owns the binding.</param>
    /// <param name="bindingId">The binding to silence.</param>
    /// <param name="ct">Cancellation token.</param>
    Task MuteBindingAsync(ConversationId conversationId, BindingId bindingId, CancellationToken ct = default);

    /// <summary>
    /// Finds and mutes the binding associated with the given channel type and address.
    /// Used by SignalR OnDisconnectedAsync to silence stale bindings by connection ID.
    /// </summary>
    /// <param name="agentId">The agent whose conversations should be searched, or <c>null</c> to search all agents.</param>
    /// <param name="channelType">The channel type (e.g. signalr).</param>
    /// <param name="channelAddress">The channel address (e.g. SignalR connection ID).</param>
    /// <param name="ct">Cancellation token.</param>
    Task MuteBindingByAddressAsync(AgentId? agentId, ChannelKey channelType, ChannelAddress channelAddress, CancellationToken ct = default);
}

/// <summary>
/// Result of resolving an inbound message to a conversation and session.
/// </summary>
/// <param name="Conversation">The resolved or created conversation.</param>
/// <param name="SessionId">The session to dispatch the message into.</param>
/// <param name="IsNewSession">True if a new session was created for this message.</param>
/// <param name="OriginatingBinding">
/// The specific <see cref="ChannelBinding"/> that matched the inbound message's channel type
/// and address. Null when no exact binding match was found (e.g. new default conversation).
/// Callers use this to stamp BindingId and DisplayPrefix on outbound responses without
/// a second lookup into the conversation's binding list.
/// </param>
public sealed record ConversationRoutingResult(
    Conversation Conversation,
    SessionId SessionId,
    bool IsNewSession,
    ChannelBinding? OriginatingBinding = null);
