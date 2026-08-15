namespace BotNexus.Extensions.ExecTool;

/// <summary>
/// How much the platform actually knows about whether a command's side effect happened.
/// </summary>
/// <remarks>
/// Before #2726 every exec failure was a flat error string, so a no-output-timeout kill
/// ("Process killed: no output for 120000ms.") was indistinguishable from a command that never
/// started. The natural recovery from a flat error is to rerun it - which for any non-idempotent
/// command executes the side effect a second time. The disposition exists so the tool can say
/// "I do not know" out loud instead of implying "it failed".
/// </remarks>
public enum ExecOutcomeDisposition
{
    /// <summary>The command ran to completion and its status is authoritative.</summary>
    Completed,

    /// <summary>
    /// The command provably never started, so no side effect can have occurred. Retrying after
    /// resolving the underlying cause is safe.
    /// </summary>
    NotDispatched,

    /// <summary>
    /// The command was dispatched but no authoritative result was obtained (killed on a timeout,
    /// a no-output timeout, or cancellation). It may have completed its side effect. NOT retry-safe.
    /// </summary>
    OutcomeUnknown,
}

/// <summary>
/// The continuation guidance the agent actually reads for each non-authoritative disposition.
/// </summary>
/// <remarks>
/// These two strings must stay behaviourally distinct: the whole point of #2726 is that
/// "safe to retry" and "do not retry" are opposite instructions. Collapsing them onto a shared
/// string reddens <c>ExecOutcomeDispositionTests.Guidance_ForTheTwoDispositions_DoesNotShareRetryPhrasing</c>.
/// </remarks>
public static class ExecOutcomeGuidance
{
    /// <summary>Guidance appended when the command provably did not run.</summary>
    public const string NotDispatched =
        "[not-dispatched] The command did not run - no side effect occurred. " +
        "It is safe to retry once the cause above is resolved.";

    /// <summary>Guidance appended when the command may or may not have run.</summary>
    public const string OutcomeUnknown =
        "[outcome-unknown] The command was dispatched and may have executed; no authoritative " +
        "result was obtained. Do NOT rerun it automatically and do NOT report it as denied or " +
        "failed - verify the intended side effect before any retry.";

    /// <summary>
    /// Returns the guidance line for a disposition, or an empty string for
    /// <see cref="ExecOutcomeDisposition.Completed"/> (an authoritative result needs no caveat).
    /// </summary>
    public static string For(ExecOutcomeDisposition disposition) => disposition switch
    {
        ExecOutcomeDisposition.NotDispatched => NotDispatched,
        ExecOutcomeDisposition.OutcomeUnknown => OutcomeUnknown,
        _ => string.Empty,
    };

    /// <summary>
    /// Maps a termination reason onto a disposition. Anything that ended by killing a live child
    /// is <see cref="ExecOutcomeDisposition.OutcomeUnknown"/>: the kill says nothing about whether
    /// the side effect already landed.
    /// </summary>
    public static ExecOutcomeDisposition Classify(string termination) => termination switch
    {
        "timeout" or "no-output-timeout" or "cancelled" => ExecOutcomeDisposition.OutcomeUnknown,
        _ => ExecOutcomeDisposition.Completed,
    };
}
