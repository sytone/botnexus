namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Single source of truth for "is this <see cref="SubAgentStatus"/> terminal?" (issue #2677),
/// mirroring the placement and intent of <c>SessionMutationPolicy.IsTerminal(SessionStatus)</c>.
/// <para>
/// Before this type existed the answer was encoded twice - once as a five-value enum pattern in
/// <c>DefaultSubAgentManager</c> and once as a four-value case-insensitive
/// <c>HashSet&lt;string&gt;</c> in <c>SubAgentWorkspaceReaper</c>. #2656 added
/// <see cref="SubAgentStatus.BudgetExhausted"/> and updated only the first, so every workspace
/// belonging to a sub-agent that ran out of turns was classified as still running and no prune
/// path would ever reclaim it. The missing value was the symptom; two independently-maintained
/// lists was the defect.
/// </para>
/// </summary>
public static class SubAgentStatusPolicy
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="status"/> means the sub-agent run is over and its
    /// resources (workspace directory, cancellation registration, timeout) may be reclaimed.
    /// <para>
    /// Deliberately written as a switch expression with <b>one arm per declared member and no
    /// catch-all <c>_</c> arm</b>. Adding a seventh <see cref="SubAgentStatus"/> value therefore
    /// produces CS8509 ("does not handle all possible values"), which is an error under this
    /// repository's <c>TreatWarningsAsErrors</c>. The new state must be classified explicitly
    /// instead of silently inheriting "not terminal" - which is exactly how #2656's addition
    /// slipped past the reaper.
    /// </para>
    /// </summary>
    /// <param name="status">The sub-agent lifecycle status.</param>
    public static bool IsTerminal(SubAgentStatus status)
        // CS8524 is the *unnamed*-value counterpart of CS8509: it fires only because a discard arm
        // is absent, and adding one would swallow future named members and defeat the whole point
        // of this type. Suppressed deliberately and narrowly so CS8509 - the diagnostic that
        // actually guards against drift - stays active. An out-of-range cast value falls through
        // to a SwitchExpressionException, which is fail-loud rather than fail-silent.
#pragma warning disable CS8524
        => status switch
        {
            SubAgentStatus.Running => false,
            SubAgentStatus.Completed => true,
            SubAgentStatus.Failed => true,
            SubAgentStatus.Killed => true,
            SubAgentStatus.TimedOut => true,
            SubAgentStatus.BudgetExhausted => true,
            // #2725: a spawn-only handoff has ended - the run delegated and stayed silent, and
            // its resources are reclaimable exactly like any other finished run.
            SubAgentStatus.HandedOff => true,
        };
#pragma warning restore CS8524

    /// <summary>
    /// Returns <c>true</c> when <paramref name="status"/> is a terminal state the run did NOT
    /// reach successfully - i.e. it ended, but not by completing its work.
    /// <para>
    /// This is the second decision that was being hand-maintained. <c>DefaultSubAgentManager</c>
    /// carried an <c>is Failed or TimedOut or BudgetExhausted</c> chain to choose between the
    /// <c>SubAgentCompleted</c> and <c>SubAgentFailed</c> lifecycle activities. It has the same
    /// drift exposure as the terminal list did: a future failure-ish status silently falls to the
    /// success branch, or to no branch at all, and the parent is never told the child failed.
    /// Written with the same exhaustive-switch discipline for the same reason.
    /// </para>
    /// <para>
    /// <c>Running</c> is <c>false</c> here because it is not terminal at all - callers must gate
    /// on <see cref="IsTerminal"/> first. <c>Killed</c> is <c>false</c> because a deliberate kill
    /// is an operator action, not a fault, and it was excluded from the original chain.
    /// </para>
    /// </summary>
    /// <param name="status">The sub-agent lifecycle status.</param>
    public static bool IsUnsuccessfulTermination(SubAgentStatus status)
#pragma warning disable CS8524
        => status switch
        {
            SubAgentStatus.Running => false,
            SubAgentStatus.Completed => false,
            SubAgentStatus.Killed => false,
            SubAgentStatus.Failed => true,
            SubAgentStatus.TimedOut => true,
            SubAgentStatus.BudgetExhausted => true,
            // #2725: a handoff is a SUCCESS. The run delegated, the descendant's result was
            // delivered, and no fault occurred. Classifying it here would re-redden every alert
            // keyed on cron run status - which is the defect #2725 exists to remove.
            SubAgentStatus.HandedOff => false,
        };
#pragma warning restore CS8524

    /// <summary>
    /// Parses a <b>persisted</b> status string and reports whether it is terminal. Used by the
    /// workspace reaper, whose input is the raw <c>sub_agent_sessions.status</c> text rather than
    /// the enum.
    /// <para>
    /// <b>Fail-safe:</b> a <c>null</c>, empty, or unparseable value returns <c>false</c>
    /// (non-terminal). Never delete a workspace whose state cannot be established - an
    /// unrecognised status is far more likely to come from a newer writer than from a dead run.
    /// Non-alphabetic text is rejected outright because
    /// <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> happily converts <c>"9"</c> or
    /// <c>"Running,Failed"</c> into a value that is not a declared member.
    /// </para>
    /// </summary>
    /// <param name="status">The persisted status string.</param>
    public static bool IsTerminalStatusName(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        var trimmed = status.Trim();

        foreach (var c in trimmed)
        {
            if (!char.IsAsciiLetter(c))
                return false;
        }

        return Enum.TryParse<SubAgentStatus>(trimmed, ignoreCase: true, out var parsed)
            && IsTerminal(parsed);
    }
}
