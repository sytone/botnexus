using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Services;

namespace BotNexus.Gateway.Dispatching;

/// <summary>
/// Intercepts inbound channel messages when a conversation is waiting on <c>ask_user</c>,
/// converting the inbound text into a direct ask-user response instead of normal agent dispatch.
/// </summary>
public sealed class PendingAskUserInterceptor(IAskUserResponseRegistry registry)
{
    /// <summary>
    /// Attempts to satisfy a pending ask-user request for the target conversation.
    /// Returns <c>true</c> when the inbound message was consumed by ask-user handling.
    /// </summary>
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
}
