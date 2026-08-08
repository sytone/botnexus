namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Names the condition that stopped (or would stop) a ralph loop. Never <see cref="None"/> on a
/// decision that halts: the whole point of the enum is that a halt is <em>disclosed</em> and
/// attributable to one named condition (issue #2818, following #2789 — a silently applied limit
/// teaches the caller a false constant).
/// </summary>
public enum RalphStopReason
{
    /// <summary>No stop condition has fired; the loop may continue.</summary>
    None = 0,

    /// <summary>The hard iteration budget (<see cref="RalphLoopConfig.MaxIterations"/>) is exhausted.</summary>
    MaxIterations = 1,

    /// <summary>The wall-clock ceiling (<see cref="RalphLoopConfig.MaxDurationMinutes"/>) is exceeded.</summary>
    MaxDuration = 2,

    /// <summary>The consecutive-failure circuit breaker tripped.</summary>
    Failed = 3,

    /// <summary>The agent signalled "nothing to do" from within a turn; resumable.</summary>
    Paused = 4,

    /// <summary>An external kill switch was thrown (explicit stop, disable, or archive).</summary>
    Killed = 5,

    /// <summary>The conversation is no longer active (archived or otherwise not accepting sessions).</summary>
    NotActive = 6,

    /// <summary>The conversation has no instructions to seed the next iteration with.</summary>
    NoInstructions = 7,

    /// <summary>The conversation is not a ralph conversation, so it never loops.</summary>
    NotRalph = 8
}

/// <summary>
/// Gateway-enforced bounds for a ralph loop. Enforcement is the gateway's job, never the prompt's:
/// an instruction asking the agent to stop has no enforcement and no retry if a turn ends early.
/// </summary>
/// <param name="MaxIterations">
/// Hard iteration budget. <c>null</c> means unbounded by iteration count (another condition must bind).
/// </param>
/// <param name="MaxDurationMinutes">
/// Wall-clock ceiling measured from <see cref="RalphLoopState.StartedAt"/>. <c>null</c> means
/// unbounded by duration. Enforced <em>independently</em> of <paramref name="MaxIterations"/>;
/// whichever binds first stops the loop and is named in the recorded stop reason.
/// </param>
/// <param name="MaxConsecutiveFailures">
/// Circuit breaker. After this many consecutive failed turns the loop halts with
/// <see cref="RalphStopReason.Failed"/> so a turn that fails immediately cannot produce a tight
/// retry storm. Defaults to 3.
/// </param>
public sealed record RalphLoopConfig(
    int? MaxIterations = null,
    int? MaxDurationMinutes = null,
    int MaxConsecutiveFailures = RalphLoopConfig.DefaultMaxConsecutiveFailures)
{
    /// <summary>The default circuit-breaker threshold (issue #2818 acceptance criterion 7).</summary>
    public const int DefaultMaxConsecutiveFailures = 3;

    /// <summary>The configuration used when a ralph conversation carries none of its own.</summary>
    public static RalphLoopConfig Default { get; } = new();
}

/// <summary>
/// The mutable bookkeeping a ralph loop carries between iterations. Deliberately durable state
/// rather than in-context state: continuity across iterations must survive a gateway restart,
/// because each iteration is a fresh session that inherits no transcript.
/// </summary>
/// <param name="Iterations">Number of iterations already started.</param>
/// <param name="StartedAt">When the loop began; the origin for the wall-clock ceiling.</param>
/// <param name="ConsecutiveFailures">Consecutive failed turns since the last success.</param>
/// <param name="IsPaused">Whether the agent signalled pause from within a turn.</param>
/// <param name="IsKilled">Whether an external kill switch was thrown.</param>
/// <param name="StopReason">The condition that stopped the loop, or <see cref="RalphStopReason.None"/>.</param>
/// <param name="StopDetail">Human-readable disclosure of the stop, naming the condition and its bound.</param>
public sealed record RalphLoopState(
    int Iterations = 0,
    DateTimeOffset? StartedAt = null,
    int ConsecutiveFailures = 0,
    bool IsPaused = false,
    bool IsKilled = false,
    RalphStopReason StopReason = RalphStopReason.None,
    string? StopDetail = null)
{
    /// <summary>A loop that has not yet run an iteration.</summary>
    public static RalphLoopState Initial { get; } = new();
}

/// <summary>
/// The single result of the single decision: whether the loop re-triggers, and — when it does not —
/// which named condition stopped it and the disclosure text recorded on the conversation.
/// </summary>
/// <param name="ShouldContinue">Whether the gateway should start another iteration.</param>
/// <param name="Reason">
/// The condition that stopped the loop. <see cref="RalphStopReason.None"/> exactly when
/// <paramref name="ShouldContinue"/> is <c>true</c>.
/// </param>
/// <param name="Detail">
/// Disclosure text naming the condition and the bound that produced it, or <c>null</c> when
/// continuing. Recorded on the conversation so a caller is never taught a false constant (#2789).
/// </param>
public readonly record struct RalphLoopDecision(bool ShouldContinue, RalphStopReason Reason, string? Detail)
{
    /// <summary>The continue decision. Carries no reason by construction.</summary>
    public static RalphLoopDecision Continue { get; } = new(true, RalphStopReason.None, null);

    /// <summary>Builds a stop decision naming <paramref name="reason"/>.</summary>
    public static RalphLoopDecision Stop(RalphStopReason reason, string detail) => new(false, reason, detail);
}

/// <summary>
/// The <em>one and only</em> definition of "should this ralph loop keep going?" (issue #2818).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this lives in exactly one place.</b> Every stop condition — iteration budget, wall-clock
/// ceiling, consecutive-failure circuit breaker, agent-signalled pause, and the external kill switch
/// — is evaluated here and nowhere else, returning a single <see cref="RalphLoopDecision"/> that
/// carries both the outcome <em>and</em> the reason. Scattering <c>if (iterations &gt; max) return;</c>
/// guards along the re-trigger path would create several independent spellings of "is this loop
/// done". They drift, and the failure is silent in the worst direction: a newly added stop condition
/// gets wired into one spelling and not the others, so the loop keeps running past a limit that the
/// operator believes is enforced. Callers must not pre-filter and must not add their own guards —
/// if a new condition is needed, it is added to <see cref="Evaluate"/>.
/// </para>
/// <para>
/// <b>Pure by design.</b> The function takes a snapshot and a clock reading and returns a value. It
/// performs no I/O, so it is exhaustively testable and the same evaluation is reachable from the
/// turn-end subscriber, from a status query, and from a test without a gateway.
/// </para>
/// <para>
/// <b>Ordering is meaningful.</b> Terminal/structural conditions are checked before budget ones so
/// the recorded reason names the condition an operator would recognise: a killed loop reports
/// <see cref="RalphStopReason.Killed"/> even if it also happens to be out of iterations.
/// </para>
/// </remarks>
public static class RalphLoopPolicy
{
    /// <summary>
    /// Decides whether a ralph conversation re-triggers after a turn ends.
    /// </summary>
    /// <param name="kind">The conversation's pairing kind. Anything but <see cref="ConversationKind.Ralph"/> never loops.</param>
    /// <param name="status">The conversation's lifecycle status. Archiving stops the loop.</param>
    /// <param name="instructions">The conversation instructions used to seed the next iteration.</param>
    /// <param name="config">The gateway-enforced bounds.</param>
    /// <param name="state">The loop's durable bookkeeping.</param>
    /// <param name="now">The current clock reading, supplied so the decision stays pure.</param>
    public static RalphLoopDecision Evaluate(
        ConversationKind kind,
        ConversationStatus status,
        string? instructions,
        RalphLoopConfig config,
        RalphLoopState state,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(state);

        if (kind != ConversationKind.Ralph)
            return RalphLoopDecision.Stop(RalphStopReason.NotRalph, "Conversation kind is not 'ralph'; it never re-triggers.");

        // External kill switch first: it is the operator's override and must beat every other
        // explanation, including a coincidental budget exhaustion.
        if (state.IsKilled)
            return RalphLoopDecision.Stop(RalphStopReason.Killed, "Stopped by the external kill switch.");

        if (status != ConversationStatus.Active)
            return RalphLoopDecision.Stop(
                RalphStopReason.NotActive,
                $"Conversation is {status.ToString().ToLowerInvariant()}, so it no longer accepts new sessions.");

        if (state.IsPaused)
            return RalphLoopDecision.Stop(
                RalphStopReason.Paused,
                "Paused by the agent (nothing to do). The loop resumes only on an explicit resume.");

        if (config.MaxConsecutiveFailures > 0 && state.ConsecutiveFailures >= config.MaxConsecutiveFailures)
            return RalphLoopDecision.Stop(
                RalphStopReason.Failed,
                $"Circuit breaker tripped after {state.ConsecutiveFailures} consecutive failed turns (limit {config.MaxConsecutiveFailures}).");

        if (string.IsNullOrWhiteSpace(instructions))
            return RalphLoopDecision.Stop(
                RalphStopReason.NoInstructions,
                "Conversation instructions are empty, so there is no prompt to seed the next iteration with.");

        if (config.MaxIterations is { } maxIterations && state.Iterations >= maxIterations)
            return RalphLoopDecision.Stop(
                RalphStopReason.MaxIterations,
                $"Reached maxIterations={maxIterations} after {state.Iterations} iterations.");

        // Independent of the iteration budget: whichever binds first stops the loop, and the
        // recorded reason names which one fired (acceptance criterion 6).
        if (config.MaxDurationMinutes is { } maxMinutes && state.StartedAt is { } startedAt)
        {
            var elapsed = now - startedAt;
            if (elapsed >= TimeSpan.FromMinutes(maxMinutes))
                return RalphLoopDecision.Stop(
                    RalphStopReason.MaxDuration,
                    $"Reached maxDurationMinutes={maxMinutes} after {elapsed.TotalMinutes:F1} minutes.");
        }

        return RalphLoopDecision.Continue;
    }
}
