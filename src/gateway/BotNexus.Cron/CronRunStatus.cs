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

    /// <summary>The run has been started and stamped but has not yet reached a terminal state.</summary>
    public const string Running = "running";
}
