using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// A channel-agnostic answer to a pending <c>ask_user</c> prompt (#2322).
/// </summary>
/// <remarks>
/// This contract carries all three response kinds uniformly - free-form text, structured
/// selections, and explicit cancellation - so a channel that can only produce one of them
/// (a text-only bot, a button-only surface) uses the same entry point as one that produces
/// all three. Previously the inbound-message path could express free text only, leaving
/// structured selection and cancel unreachable from any channel other than SignalR.
/// </remarks>
public sealed record AskUserSubmission
{
    /// <summary>Conversation whose pending prompt is being answered.</summary>
    public required ConversationId ConversationId { get; init; }

    /// <summary>
    /// Correlation id of the prompt being answered. When null, the resolver targets whatever
    /// prompt is currently pending on the conversation - the shape an inbound text reply takes,
    /// since the user never types a request id.
    /// </summary>
    public string? RequestId { get; init; }

    /// <summary>Free-form text answer, when supplied.</summary>
    public string? FreeFormText { get; init; }

    /// <summary>Structured choice values selected by the user, when supplied.</summary>
    public IReadOnlyList<string>? SelectedValues { get; init; }

    /// <summary>True when the user explicitly declined or cancelled the prompt.</summary>
    public bool Cancelled { get; init; }

    /// <summary>Channel the answer arrived on, for auditing and diagnostics.</summary>
    public ChannelKey? OriginChannel { get; init; }
}

/// <summary>
/// Outcome of an <see cref="AskUserSubmission"/>, replacing the per-channel ad-hoc exception
/// and boolean conventions with one shared vocabulary.
/// </summary>
public sealed record AskUserResolutionResult
{
    private AskUserResolutionResult(AskUserResolutionStatus status, string? requestId, string? failureReason)
    {
        Status = status;
        RequestId = requestId;
        FailureReason = failureReason;
    }

    /// <summary>Outcome classification.</summary>
    public AskUserResolutionStatus Status { get; }

    /// <summary>The request id that was resolved, when resolution succeeded.</summary>
    public string? RequestId { get; }

    /// <summary>Human-readable explanation for a non-success outcome.</summary>
    public string? FailureReason { get; }

    /// <summary>True when the pending prompt was resolved and the blocked tool call resumed.</summary>
    public bool Succeeded => Status == AskUserResolutionStatus.Resolved;

    /// <summary>Creates a successful outcome for the specified request.</summary>
    public static AskUserResolutionResult Resolved(string requestId)
        => new(AskUserResolutionStatus.Resolved, requestId, null);

    /// <summary>Creates an outcome indicating no prompt was pending on the conversation.</summary>
    public static AskUserResolutionResult NoPendingPrompt(string reason)
        => new(AskUserResolutionStatus.NoPendingPrompt, null, reason);

    /// <summary>Creates an outcome indicating the submission itself was malformed.</summary>
    public static AskUserResolutionResult InvalidSubmission(string reason)
        => new(AskUserResolutionStatus.InvalidSubmission, null, reason);
}

/// <summary>Classification of an <c>ask_user</c> resolution attempt.</summary>
public enum AskUserResolutionStatus
{
    /// <summary>The pending prompt was resolved and the waiting tool call resumed.</summary>
    Resolved,

    /// <summary>No prompt was pending for the conversation, or the request id did not match.</summary>
    NoPendingPrompt,

    /// <summary>The submission was rejected before reaching the pending prompt.</summary>
    InvalidSubmission
}
