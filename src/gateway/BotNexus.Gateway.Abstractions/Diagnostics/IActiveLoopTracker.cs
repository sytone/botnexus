namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Identifies one tracked agent-loop run. Returned by <see cref="IActiveLoopTracker.TrackStart"/>
/// and handed back to <see cref="IActiveLoopTracker.TrackEnd"/> so completion removes the exact
/// run that started rather than "some" run - a plain counter decrement cannot tell two concurrent
/// loops apart, which is what made the old aggregate-only tracker unable to explain its own count
/// (issue #2794).
/// </summary>
/// <param name="Id">Opaque per-run identity. <see cref="Guid.Empty"/> means "not tracked".</param>
public readonly record struct ActiveLoopRegistration(Guid Id)
{
    /// <summary>A registration that refers to no tracked run; <c>TrackEnd</c> ignores it.</summary>
    public static ActiveLoopRegistration None => new(Guid.Empty);

    /// <summary>True when this registration refers to a real tracked run.</summary>
    public bool IsTracked => Id != Guid.Empty;
}

/// <summary>
/// Operator-facing metadata retained for a single in-flight agent loop. Deliberately small and
/// bounded: just enough for an operator to decide whether a gateway restart is safe (which agent,
/// which conversation, how long it has been running) without becoming a second activity registry.
/// </summary>
public sealed record ActiveLoopDetail
{
    /// <summary>Opaque run identity, stable for the lifetime of the loop.</summary>
    public required string LoopId { get; init; }

    /// <summary>Agent executing the loop, when known.</summary>
    public string? AgentId { get; init; }

    /// <summary>Conversation the loop belongs to, when known. Drives portal addressability.</summary>
    public string? ConversationId { get; init; }

    /// <summary>Session the loop is running under, when known.</summary>
    public string? SessionId { get; init; }

    /// <summary>UTC instant the loop started, used to derive run age.</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }
}

/// <summary>
/// A single point-in-time view of the tracker. The headline <see cref="ActiveCount"/> is derived
/// from the very same materialised <see cref="ActiveLoops"/> collection, so a concurrent start or
/// completion can never produce a count that disagrees with the list the portal renders (AC2).
/// </summary>
public sealed record ActiveLoopSnapshot
{
    /// <summary>Number of loops in <see cref="ActiveLoops"/>. Never derived independently.</summary>
    public required int ActiveCount { get; init; }

    /// <summary>Peak concurrent loop count since startup.</summary>
    public required int PeakCount { get; init; }

    /// <summary>Total completed loops since startup.</summary>
    public required long TotalCompleted { get; init; }

    /// <summary>The in-flight loops, oldest first.</summary>
    public required IReadOnlyList<ActiveLoopDetail> ActiveLoops { get; init; }
}

/// <summary>
/// Tracks concurrently active agent loops for capacity monitoring, retaining per-run operator
/// context so the count can be inspected rather than merely observed.
/// </summary>
public interface IActiveLoopTracker
{
    /// <summary>
    /// Current number of active agent loops.
    /// </summary>
    int ActiveCount { get; }

    /// <summary>
    /// Peak concurrent loop count since startup.
    /// </summary>
    int PeakCount { get; }

    /// <summary>
    /// Total number of completed loops since startup.
    /// </summary>
    long TotalCompleted { get; }

    /// <summary>
    /// Records that an agent loop has started, retaining the supplied operator context.
    /// </summary>
    /// <returns>The registration that must be passed to <see cref="TrackEnd"/>.</returns>
    ActiveLoopRegistration TrackStart(string? agentId = null, string? conversationId = null, string? sessionId = null);

    /// <summary>
    /// Records that the run identified by <paramref name="registration"/> has ended. Unknown or
    /// already-removed registrations are ignored so a double-completion cannot corrupt the counters.
    /// </summary>
    void TrackEnd(ActiveLoopRegistration registration);

    /// <summary>
    /// Captures counters and in-flight detail together as one consistent point-in-time snapshot.
    /// </summary>
    ActiveLoopSnapshot GetSnapshot();
}
