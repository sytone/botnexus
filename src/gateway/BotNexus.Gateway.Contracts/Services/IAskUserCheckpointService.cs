using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Services;

/// <summary>
/// Single source of truth for resolving an <c>ask_user</c> prompt as a durable, resumable
/// checkpoint (issue #2047). Unlike <see cref="IAskUserResponseRegistry"/>, which only tracks
/// live in-memory waiters, this service resolves against persisted
/// <see cref="Conversation.PendingAskUserJson"/> state so a response or cancellation can resume
/// the conversation even after a gateway restart, reload, or conversation switch that destroyed
/// the original blocked tool task, provider stream, and <c>TaskCompletionSource</c>.
/// </summary>
/// <remarks>
/// The resolution is atomic and idempotent: exactly one submission for a given request id can
/// claim and clear the checkpoint. Duplicate responses, stale request ids, and competing clients
/// resolve to a non-resuming outcome so the conversation never continues twice.
/// </remarks>
public interface IAskUserCheckpointService
{
    /// <summary>
    /// Resolves a user response or cancellation for a conversation's pending <c>ask_user</c> prompt.
    /// Completes the live waiter when one exists; otherwise atomically claims and clears the durable
    /// checkpoint and dispatches a continuation to resume execution from persisted state.
    /// </summary>
    /// <param name="conversationId">Conversation that owns the pending prompt.</param>
    /// <param name="requestId">Correlation id of the prompt being answered or cancelled.</param>
    /// <param name="response">The normalized response (free-form text, selected values, or cancellation).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome describing how (or whether) the prompt resolved.</returns>
    Task<AskUserResolveOutcome> ResolveAsync(
        ConversationId conversationId,
        string requestId,
        AskUserResponse response,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves an inbound free-form message against a conversation's durable pending prompt, if any.
    /// Returns <c>true</c> when the message was consumed as an <c>ask_user</c> response (live or
    /// checkpoint-resumed) and must not enter normal agent dispatch; <c>false</c> when no prompt is
    /// pending and the message should be dispatched normally.
    /// </summary>
    /// <param name="conversationId">Conversation the inbound message targets.</param>
    /// <param name="freeFormText">The inbound message text to interpret as a response.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> TryResolveInboundTextAsync(
        ConversationId conversationId,
        string freeFormText,
        CancellationToken cancellationToken = default);
}
