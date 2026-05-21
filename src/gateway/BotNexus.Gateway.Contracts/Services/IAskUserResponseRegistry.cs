using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Services;

/// <summary>
/// Coordinates pending <c>ask_user</c> waits outside the session queue so channels can
/// fulfill blocked tool calls directly and resume agent execution safely.
/// </summary>
public interface IAskUserResponseRegistry
{
    /// <summary>
    /// Registers a new pending request for a conversation and returns the request correlation
    /// identifier plus the task that completes when a response arrives.
    /// </summary>
    /// <param name="conversationId">Conversation that owns the pending request.</param>
    /// <param name="timeout">Optional timeout after which the wait completes as timed out.</param>
    /// <returns>Generated request id and completion task.</returns>
    (string RequestId, Task<AskUserResponse> Task) Register(ConversationId conversationId, TimeSpan? timeout);

    /// <summary>
    /// Attempts to complete a pending request for the specified conversation.
    /// Returns <c>false</c> when no matching pending request exists.
    /// </summary>
    bool TryComplete(ConversationId conversationId, string requestId, AskUserResponse response);

    /// <summary>
    /// Cancels a pending request by request id if it is still waiting.
    /// </summary>
    void Cancel(string requestId);

    /// <summary>
    /// Cancels all pending requests for a conversation, typically during archive/close.
    /// </summary>
    void CancelAllForConversation(ConversationId conversationId);

    /// <summary>
    /// Returns the pending request id for a conversation when a wait is active.
    /// </summary>
    bool TryGetPendingRequestId(ConversationId conversationId, out string requestId);
}
