using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Services;

/// <summary>
/// Resumes agent/session execution for an <c>ask_user</c> prompt that was answered or cancelled
/// while no live in-memory waiter existed (issue #2047) - the restart/reload/switch case. The
/// checkpoint service invokes this after it has atomically claimed and cleared the durable
/// <see cref="Conversation.PendingAskUserJson"/> checkpoint, so an implementation must be safe to
/// treat every call as a single, already-deduplicated continuation.
/// </summary>
/// <remarks>
/// The continuation is modelled as a new turn seeded with the user's answer (or an explicit
/// cancellation notice) rather than by reconstructing an arbitrary provider call stack, matching
/// the design direction in the issue: the resume must not depend on the original process, provider
/// stream, or <c>TaskCompletionSource</c> still existing.
/// </remarks>
public interface IAskUserCheckpointResumer
{
    /// <summary>
    /// Dispatches a continuation that resumes the conversation from the durable checkpoint.
    /// </summary>
    /// <param name="request">The pending request reconstructed from persisted state.</param>
    /// <param name="response">The normalized user response (answer or cancellation).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ResumeAsync(AskUserRequest request, AskUserResponse response, CancellationToken cancellationToken = default);
}
