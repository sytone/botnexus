using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Conversations;

/// <summary>Stored visibility fence for client mutations only. Runtime producers do not use this policy.</summary>
public static class ConversationClientWritePolicy
{
    /// <summary>Returns zero when writable, 404 when absent/hidden/not owned, or 403 when inspectable.</summary>
    public static int Evaluate(Conversation? conversation, AgentId? expectedAgent = null)
    {
        if (conversation is null || (expectedAgent.HasValue && conversation.AgentId != expectedAgent.Value))
            return 404;
        return conversation.Visibility switch
        {
            ConversationVisibility.UserFacing => 0,
            ConversationVisibility.InspectableReadOnly => 403,
            _ => 404
        };
    }

    /// <summary>Checks a session's stored parent; unlinked legacy sessions remain compatible.</summary>
    public static async Task<int> EvaluateSessionAsync(IConversationStore? store, GatewaySession? session,
        AgentId? expectedAgent = null, CancellationToken cancellationToken = default)
    {
        if (session is null)
            return 0;
        if (expectedAgent.HasValue && session.AgentId != expectedAgent.Value)
            return 404;
        if (!session.ConversationId.IsInitialized())
            return 0;
        var conversation = store is null ? null : await store.GetAsync(session.ConversationId, cancellationToken);
        return Evaluate(conversation, expectedAgent ?? session.AgentId);
    }
}
