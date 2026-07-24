using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Services;

namespace BotNexus.Gateway.Dispatching;

/// <summary>
/// Intercepts inbound channel messages when a conversation is waiting on <c>ask_user</c>,
/// converting the inbound text into a direct ask-user response instead of normal agent dispatch.
/// </summary>
/// <remarks>
/// As of issue #2047 interception resolves against durable checkpoint state via
/// <see cref="IAskUserCheckpointService"/>, not only the in-memory registry, so an answer that
/// arrives after a gateway restart is still captured (and resumes the conversation from the
/// persisted checkpoint) rather than being mis-dispatched as a fresh turn. When the checkpoint
/// service is unavailable it falls back to the legacy in-memory registry path.
/// </remarks>
public sealed class PendingAskUserInterceptor(
    IAskUserResponseRegistry registry,
    IAskUserCheckpointService? checkpointService = null)
{
    /// <summary>
    /// Attempts to satisfy a pending ask-user request for the target conversation.
    /// Returns <c>true</c> when the inbound message was consumed by ask-user handling.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="TryInterceptAsync"/>: it resolves durable checkpoints and restart-resume.
    /// This synchronous overload preserves the legacy in-memory-only behaviour for callers that
    /// cannot await, and is kept so existing call sites and tests compile unchanged.
    /// </remarks>
    public bool TryIntercept(InboundMessage message, ConversationId conversationId)
    {
        if (!registry.TryGetPendingRequestId(conversationId, out var requestId))
            return false;

        var response = new AskUserResponse
        {
            RequestId = requestId,
            FreeFormText = message.Content
        };

        return registry.TryComplete(conversationId, requestId, response);
    }

    /// <summary>
    /// Attempts to satisfy a pending ask-user request for the target conversation, resolving against
    /// durable checkpoint state so an answer after a gateway restart still resumes the conversation.
    /// Returns <c>true</c> when the inbound message was consumed by ask-user handling and must not
    /// enter normal agent dispatch.
    /// </summary>
    public async Task<bool> TryInterceptAsync(
        InboundMessage message,
        ConversationId conversationId,
        CancellationToken cancellationToken = default)
    {
        if (checkpointService is null)
            return TryIntercept(message, conversationId);

        return await checkpointService
            .TryResolveInboundTextAsync(conversationId, message.Content, cancellationToken)
            .ConfigureAwait(false);
    }
}
