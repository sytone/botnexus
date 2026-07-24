using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Services;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Services;

/// <summary>
/// The one gateway-owned resolution path for pending <c>ask_user</c> prompts (#2322).
/// </summary>
/// <remarks>
/// <para>
/// All normalisation of a user's answer lives here: trimming, dropping blank selected values,
/// and deciding when an empty submission is meaningful. Channels supply raw user input and get
/// a classified <see cref="AskUserResolutionResult"/> back; they no longer own any part of the
/// resolution semantics, and they no longer see <see cref="IAskUserResponseRegistry"/> at all.
/// </para>
/// <para>
/// Request-id handling is deliberately lenient in one direction only: a submission may omit the
/// request id (an inbound text reply cannot carry one), in which case the currently pending
/// prompt for the conversation is targeted. A submission that <em>does</em> carry a request id
/// must match, so a stale button press on a superseded prompt is rejected rather than silently
/// answering a different question.
/// </para>
/// </remarks>
public sealed class AskUserPromptResolver(
    IAskUserResponseRegistry registry,
    ILogger<AskUserPromptResolver> logger) : IAskUserPromptResolver
{
    /// <inheritdoc />
    public ValueTask<AskUserResolutionResult> ResolveAsync(
        AskUserSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        cancellationToken.ThrowIfCancellationRequested();

        if (!submission.ConversationId.IsInitialized())
        {
            return ValueTask.FromResult(
                AskUserResolutionResult.InvalidSubmission("A conversation id is required to resolve an ask_user prompt."));
        }

        if (!registry.TryGetPendingRequestId(submission.ConversationId, out var pendingRequestId))
        {
            return ValueTask.FromResult(
                AskUserResolutionResult.NoPendingPrompt(
                    $"No ask_user request is pending for conversation '{submission.ConversationId.Value}'."));
        }

        var requestedId = submission.RequestId?.Trim();
        if (!string.IsNullOrEmpty(requestedId) && !string.Equals(requestedId, pendingRequestId, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(
                AskUserResolutionResult.NoPendingPrompt(
                    $"Request '{requestedId}' does not match the prompt pending for this conversation."));
        }

        var freeFormText = string.IsNullOrWhiteSpace(submission.FreeFormText)
            ? null
            : submission.FreeFormText.Trim();

        var selectedValues = submission.SelectedValues is { Count: > 0 }
            ? submission.SelectedValues
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToArray()
            : null;

        if (selectedValues is { Length: 0 })
            selectedValues = null;

        // An answer must say something. Cancellation is itself an answer; silence is not, and
        // completing the wait with an empty response would resume the tool with nothing to act on.
        if (!submission.Cancelled && freeFormText is null && selectedValues is null)
        {
            return ValueTask.FromResult(
                AskUserResolutionResult.InvalidSubmission(
                    "An ask_user response must carry free-form text, at least one selected value, or an explicit cancellation."));
        }

        var response = new AskUserResponse
        {
            RequestId = pendingRequestId,
            FreeFormText = freeFormText,
            SelectedValues = selectedValues,
            WasCancelled = submission.Cancelled
        };

        if (!registry.TryComplete(submission.ConversationId, pendingRequestId, response))
        {
            return ValueTask.FromResult(
                AskUserResolutionResult.NoPendingPrompt(
                    $"The ask_user request pending for conversation '{submission.ConversationId.Value}' was already resolved."));
        }

        logger.LogInformation(
            "Resolved ask_user request {RequestId} for conversation {ConversationId} from channel {Channel} (cancelled: {Cancelled}).",
            pendingRequestId,
            submission.ConversationId,
            submission.OriginChannel?.Value ?? "unknown",
            submission.Cancelled);

        return ValueTask.FromResult(AskUserResolutionResult.Resolved(pendingRequestId));
    }

    /// <inheritdoc />
    public bool TryGetPendingRequestId(ConversationId conversationId, out string requestId)
        => registry.TryGetPendingRequestId(conversationId, out requestId);
}
