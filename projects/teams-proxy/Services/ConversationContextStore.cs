using System.Collections.Concurrent;
using BotNexus.TeamsProxy.Models;

namespace BotNexus.TeamsProxy.Services;

/// <summary>
/// In-memory store mapping Teams conversation identifiers to routing contexts.
/// Used to recover the service URL, original activity, and participant identities
/// needed to send a BotNexus reply back to Teams when the outbound queue envelope
/// is received (which carries only conversationId, not full Teams routing data).
/// </summary>
public sealed class ConversationContextStore
{
    private readonly ConcurrentDictionary<string, TeamsConversationContext> _contexts =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Stores or updates the routing context for a conversation.
    /// Called by <see cref="InboundQueuePublisher"/> when an inbound Teams message is queued.
    /// </summary>
    public void Store(string conversationId, TeamsConversationContext context)
        => _contexts[conversationId] = context;

    /// <summary>
    /// Returns the routing context for <paramref name="conversationId"/>, or <c>null</c>
    /// if no context has been stored for that conversation in this process lifetime.
    /// </summary>
    public TeamsConversationContext? TryGet(string conversationId)
        => _contexts.TryGetValue(conversationId, out var ctx) ? ctx : null;
}
