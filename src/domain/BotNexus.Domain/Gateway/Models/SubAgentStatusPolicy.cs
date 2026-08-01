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
