namespace BotNexus.Cron;

/// <summary>
/// Canonical run-status values persisted for every cron run.
/// </summary>
/// <remarks>
/// <para>
/// These strings are a <b>contract</b>: they are written to the run-history store, compared in
/// the retention/abort paths, and parsed by the daily platform digest and PR-watch self-reschedule
/// logic. A bare string literal that is mistyped (e.g. <c>"timedout"</c> instead of
/// <c>"timed_out"</c>) compiles cleanly but silently corrupts run history — no query would match
/// it. Routing every producer and comparison through these constants turns such a typo into a
/// compile error.
/// </para>
/// <para>
/// Do not change these values without a coordinated migration: existing history rows and external
/// parsers depend on the exact strings.
/// </para>
/// </remarks>
public static class CronRunStatus
{
    /// <summary>The run completed successfully.</summary>
    public const string Ok = "ok";

    /// <summary>The run failed with an exception (or was aborted before completion).</summary>
    public const string Error = "error";

    /// <summary>The run exceeded its configured timeout and was cancelled.</summary>
    public const string TimedOut = "timed_out";
    /// <summary>
    /// #2985: an <b>execution-class</b> run that completed without throwing but performed zero tool
    /// invocations. For a job whose contract is to do work, that is by definition a run that did
    /// nothing - yet the turn completed, so the pre-#2985 outcome was <see cref="Ok"/> with a null
    /// error, byte-identical to a healthy run.
    ///
    /// <para>
    /// This is a <b>terminal non-success</b> outcome: it is written to run history, it is what
    /// <c>LastRunStatus</c> shows, it participates in the failure-alert streak alongside
    /// <see cref="Error"/>, and it is purgeable by retention like any other terminal row. It is a
    /// separate value rather than a reuse of <see cref="Error"/> so run history distinguishes
    /// "the action threw" from "the action returned having done nothing" - two different
    /// operator responses.
    /// </para>
    /// <para>
    /// Only ever written for a job with <c>ExecutionClass = true</c> whose action reported a tool
    /// count. A job that is not execution-class, or an action that reports no count at all
    /// (command/webhook), can never reach this status.
    /// </para>
    /// </summary>
    public const string NoToolCalls = "no_tool_calls";

    /// <summary>The run has been started and stamped but has not yet reached a terminal state.</summary>
    public const string Running = "running";

    /// <summary>
    /// A fire that was suppressed before the action was invoked because the job is past its
    /// <c>ExpiresAt</c> instant (#2634). Returned to the caller for visibility; deliberately NOT
    /// written to run history, because a suppressed fire is the absence of a run, not a run.
    /// </summary>
    public const string Skipped = "skipped";

    /// <summary>
    /// A scheduled occurrence that elapsed while the gateway was down and was never executed.
    /// Written only by startup missed-run detection, which stamps the row with the scheduled
    /// occurrence instant so the (job, occurrence) pair is a stable identity across restarts.
    /// </summary>
    public const string Missed = "missed";
}
