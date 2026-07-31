namespace BotNexus.Agent.Core.Hooks;

/// <summary>
/// Defines the outcome of pre-tool-call interception.
/// </summary>
/// <param name="Block">Indicates whether the tool call should be blocked (true prevents execution).</param>
/// <param name="Reason">An optional reason for blocking (used in error tool result when Block=true).</param>
/// <remarks>
/// <para>
/// Return from BeforeToolCallDelegate to prevent tool execution.
/// When Block=true, the tool result is marked as an error with the provided Reason.
/// </para>
/// <para>
/// Issue #2476: a boolean cannot express an <b>ambiguous</b> approval decision. An approval
/// provider whose reviewers split, whose quorum failed, or whose aggregation raced has no clear
/// verdict, and coercing that silence into <c>Block=false</c> is an auto-approve - the exact
/// failure mode this type must not have. <see cref="IsIndeterminate"/> lets such a provider say
/// "I have no unambiguous allow for you", and the executor treats it as a DENY, so the decision
/// falls back to a human rather than to the tool running.
/// </para>
/// <para>
/// The signal is purely additive and defaults to <c>false</c>: every existing caller that
/// constructs <c>new BeforeToolCallResult(block, reason)</c> keeps its exact prior meaning, and a
/// <c>null</c> result still means "no opinion, allow".
/// </para>
/// </remarks>
public record BeforeToolCallResult(bool Block, string? Reason = null)
{
    /// <summary>
    /// The message used when an indeterminate verdict carries no reason of its own.
    /// </summary>
    public const string DefaultIndeterminateReason =
        "Tool call was not unambiguously approved by policy and has been blocked pending human review.";

    /// <summary>
    /// When <c>true</c>, the hook could not reach an unambiguous decision and the tool call must
    /// fail closed. Defaults to <c>false</c> so pre-existing hooks are unaffected.
    /// </summary>
    /// <remarks>
    /// This is deliberately distinct from <see cref="Block"/>. <c>Block=true</c> is a positive
    /// decision to deny; <c>IsIndeterminate=true</c> is the <i>absence</i> of a decision. Both
    /// prevent execution, but keeping them apart lets callers, logs and operators tell "policy said
    /// no" apart from "policy could not say", which are very different things to investigate.
    /// </remarks>
    public bool IsIndeterminate { get; init; }

    /// <summary>
    /// <c>true</c> only when this result is a positive, unambiguous authorisation to run the tool.
    /// Anything else - an explicit block, or an indeterminate verdict - must not execute.
    /// </summary>
    public bool IsUnambiguousAllow => !Block && !IsIndeterminate;

    /// <summary>
    /// The reason to surface when this result prevents execution, falling back to a generic
    /// message so a block is never reported as an unexplained refusal.
    /// </summary>
    public string EffectiveBlockReason =>
        string.IsNullOrWhiteSpace(Reason)
            ? (IsIndeterminate ? DefaultIndeterminateReason : "Tool call was blocked by policy.")
            : Reason!;

    /// <summary>
    /// Creates a verdict that is neither an allow nor a deliberate deny. The executor fails closed
    /// on it. Use this when an approval decision genuinely could not be reached.
    /// </summary>
    /// <param name="reason">Optional explanation of why no decision was reachable.</param>
    public static BeforeToolCallResult Indeterminate(string? reason = null) =>
        new(Block: false, Reason: reason) { IsIndeterminate = true };
}
