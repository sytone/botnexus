using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Services;

namespace BotNexus.Gateway.Dispatching;

/// <summary>
/// Intercepts inbound channel messages when a conversation is waiting on <c>ask_user</c>,
/// converting the inbound text into a direct ask-user response instead of normal agent dispatch.
/// </summary>
/// <remarks>
/// <para>
/// Since #2322 this routes through <see cref="IAskUserPromptResolver"/> rather than constructing
/// an <see cref="AskUserResponse"/> and touching the registry itself. That matters beyond tidiness:
/// the interceptor previously could only ever express free-form text, so a channel answering with
/// a structured selection or an explicit cancel had no path through the inbound side at all.
/// Those fields are now reachable because the shared submission contract carries them.
/// </para>
/// <para>
/// As of issue #2047 <see cref="TryInterceptAsync"/> additionally resolves against durable
/// checkpoint state via <see cref="IAskUserCheckpointService"/>, so an answer that arrives after a
/// gateway restart is still captured (and resumes the conversation from the persisted checkpoint)
/// rather than being mis-dispatched as a fresh turn. The checkpoint service resolves its own live
/// path through the same resolver, so the #2322 single-resolution-path seam is preserved. When no
/// checkpoint service is wired it falls back to the live-only resolver path.
/// </para>
/// </remarks>
public sealed class PendingAskUserInterceptor(
    IAskUserPromptResolver resolver,
    IAskUserCheckpointService? checkpointService = null)
{
    /// <summary>
    /// Attempts to satisfy a pending ask-user request for the target conversation.
    /// Returns <c>true</c> when the inbound message was consumed by ask-user handling.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="TryInterceptAsync"/>: it resolves durable checkpoints and restart-resume.
    /// This synchronous overload preserves the live-only behaviour for callers that cannot await,
    /// and is kept so existing call sites and tests compile unchanged.
    /// </remarks>
    public bool TryIntercept(InboundMessage message, ConversationId conversationId)
        => TryIntercept(message, conversationId, cancelled: false);

    /// <summary>
    /// Attempts to satisfy a pending ask-user request, optionally as an explicit cancellation.
    /// </summary>
    /// <param name="message">The inbound message carrying the user's answer.</param>
    /// <param name="conversationId">Conversation that may have a prompt pending.</param>
    /// <param name="cancelled">True when the inbound message represents an explicit decline.</param>
    public bool TryIntercept(InboundMessage message, ConversationId conversationId, bool cancelled)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!resolver.TryGetPendingRequestId(conversationId, out var requestId))
            return false;

        var submission = new AskUserSubmission
        {
            ConversationId = conversationId,
            RequestId = requestId,
            FreeFormText = message.Content,
            Cancelled = cancelled,
            OriginChannel = message.ChannelType
        };

        // The resolver is synchronous in practice (an in-memory registry completion); the
        // ValueTask is awaited eagerly here so the dispatch loop keeps its existing shape.
        var result = resolver.ResolveAsync(submission).AsTask().GetAwaiter().GetResult();
        return result.Succeeded;
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
        ArgumentNullException.ThrowIfNull(message);

        if (checkpointService is null)
            return TryIntercept(message, conversationId);

        return await checkpointService
            .TryResolveInboundTextAsync(conversationId, message.Content, cancellationToken)
            .ConfigureAwait(false);
    }
}
