using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Services;

namespace BotNexus.Gateway.Dispatching;

/// <summary>
/// Intercepts inbound channel messages when a conversation is waiting on <c>ask_user</c>,
/// converting the inbound text into a direct ask-user response instead of normal agent dispatch.
/// </summary>
/// <remarks>
/// Since #2322 this routes through <see cref="IAskUserPromptResolver"/> rather than constructing
/// an <see cref="AskUserResponse"/> and touching the registry itself. That matters beyond tidiness:
/// the interceptor previously could only ever express free-form text, so a channel answering with
/// a structured selection or an explicit cancel had no path through the inbound side at all.
/// Those fields are now reachable because the shared submission contract carries them.
/// </remarks>
public sealed class PendingAskUserInterceptor(IAskUserPromptResolver resolver)
{
    /// <summary>
    /// Attempts to satisfy a pending ask-user request for the target conversation.
    /// Returns <c>true</c> when the inbound message was consumed by ask-user handling.
    /// </summary>
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
}
