namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Answers the one question any backstop sweeper must ask before it deletes a sub-agent workspace
/// directory: <b>is the sub-agent that owns this directory still running?</b> (issue #3569).
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this exists to close.</b> The age-based sweep (#2237) decided eligibility purely
/// from the directory's last-write time. A live sub-agent that spends several minutes thinking,
/// waiting on a provider, or awaiting a descendant writes nothing to its workspace during that
/// window, so its directory's age crosses the TTL while the run is still healthy. The sweep then
/// deleted the working directory out from under a live process: 66 tool failures across 37 distinct
/// sub-agents in a single 7-day window, none of which failed on their FIRST tool call - every one
/// worked, then lost its workspace. <b>Elapsed time is not a liveness signal</b>, and the grace
/// window only shrank the race rather than removing it.
/// </para>
/// <para>
/// <b>Why a probe rather than a timestamp heuristic.</b> Any age-derived rule is a guess about
/// liveness; this contract asks the component that actually knows. The gateway registers a child
/// agent descriptor when a sub-agent spawns and unregisters it during terminal cleanup, so
/// registration is an exact, lifecycle-driven liveness signal with no clock in it at all.
/// </para>
/// <para>
/// <b>Fail-safe direction is mandatory.</b> An implementation that cannot determine liveness must
/// answer <c>true</c> (assume live). Retaining a dead workspace for one more sweep interval costs
/// disk; deleting a live one destroys an entire sub-agent run and returns a confident-sounding but
/// wrong summary to the parent. Those costs are not symmetric, so the uncertain answer is always
/// "keep it".
/// </para>
/// </remarks>
public interface ISubAgentWorkspaceLivenessProbe
{
    /// <summary>
    /// Whether the sub-agent owning <paramref name="workspaceDirectoryName"/> is still running and
    /// its workspace must therefore be preserved.
    /// </summary>
    /// <param name="workspaceDirectoryName">
    /// The on-disk workspace directory name, which is the sanitized child agent id
    /// (e.g. <c>parent--subagent--coder--0f3c…</c>).
    /// </param>
    /// <returns>
    /// <c>true</c> when the sub-agent is live (or liveness could not be determined) and the
    /// directory must be retained; <c>false</c> only when it is positively known not to be running.
    /// </returns>
    bool IsLive(string workspaceDirectoryName);
}
