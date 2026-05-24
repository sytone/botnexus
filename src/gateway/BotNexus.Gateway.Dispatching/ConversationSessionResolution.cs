using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Dispatching;

/// <summary>
/// Represents the resolved conversation/session target for an inbound message after dispatch routing.
/// </summary>
/// <param name="ConversationId">Conversation selected for the inbound payload.</param>
/// <param name="SessionId">Session selected or created for agent processing.</param>
/// <param name="IsNewConversation">True when dispatch created a brand-new conversation binding.</param>
/// <param name="IsNewSession">True when dispatch created a new active session.</param>
/// <param name="OriginatingBindingId">
/// Optional binding identity for fan-out exclusion and correlated outbound delivery.
/// </param>
/// <param name="DisplayPrefix">Optional display prefix to preserve transport threading semantics.</param>
public sealed record ConversationSessionResolution(
    ConversationId ConversationId,
    SessionId SessionId,
    bool IsNewConversation,
    bool IsNewSession,
    BindingId? OriginatingBindingId = null,
    string? DisplayPrefix = null);
