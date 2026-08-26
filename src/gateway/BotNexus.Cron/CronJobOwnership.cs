using BotNexus.Domain.Primitives;

namespace BotNexus.Cron;

/// <summary>
/// The single definition of "may this caller manage this cron job?".
/// </summary>
/// <remarks>
/// <para>
/// Two seams reach one store: the model-facing <c>CronTool</c> and the REST
/// <c>CronController</c>. The predicate used to live as a <c>private bool CanManage</c> on
/// <c>CronTool</c>, so the controller could not reuse it even in principle and shipped with no
/// ownership check at all (#3575). Hoisting it here follows the same anti-duplicate-spelling
/// pattern as <see cref="CronTimeZoneResolver"/> (#2748) and <see cref="CronAlertTarget"/>
/// (#2671): one rule, one place, both callers delegating.
/// </para>
/// <para>
/// This is a CALLER-seam predicate. It deliberately does not reach into the store's WHERE
/// clause - the store-level ownership predicate that closes the check-then-write window is
/// tracked separately as #3573 and layers on top of this type rather than replacing it.
/// </para>
/// </remarks>
public static class CronJobOwnership
{
    /// <summary>
    /// Returns whether <paramref name="callerAgentId"/> may manage <paramref name="job"/>.
    /// </summary>
    /// <param name="job">The job whose ownership is being tested.</param>
    /// <param name="callerAgentId">The agent identity acting.</param>
    /// <param name="allowCrossAgentCron">
    /// When true the calling agent is explicitly configured for cross-agent cron and every job is
    /// manageable. This mirrors the descriptor-level opt-in <c>CronTool</c> already honours.
    /// </param>
    /// <returns><c>true</c> when the caller created the job or the job targets it.</returns>
    public static bool CanManage(CronJob job, AgentId callerAgentId, bool allowCrossAgentCron = false)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (allowCrossAgentCron)
            return true;

        var isCreator = string.Equals(job.CreatedBy, callerAgentId.Value, StringComparison.OrdinalIgnoreCase);
        var isTarget = job.AgentId.HasValue && job.AgentId.Value == callerAgentId;
        return isCreator || isTarget;
    }

    /// <summary>
    /// Returns whether ANY of <paramref name="callerAgentIds"/> may manage <paramref name="job"/>.
    /// </summary>
    /// <remarks>
    /// The REST seam authenticates a caller, not an agent, and that caller carries a set of
    /// permitted agent ids rather than a single one. This overload applies the SAME per-agent rule
    /// across that set so the REST decision cannot drift from the tool decision - it is a fold of
    /// <see cref="CanManage(CronJob, AgentId, bool)"/>, not a second rule.
    /// </remarks>
    /// <param name="job">The job whose ownership is being tested.</param>
    /// <param name="callerAgentIds">The agent ids the authenticated caller is scoped to.</param>
    /// <returns><c>true</c> when at least one scoped agent may manage the job.</returns>
    public static bool CanManageAsAny(CronJob job, IReadOnlyList<string>? callerAgentIds)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (callerAgentIds is null || callerAgentIds.Count == 0)
            return false;

        foreach (var candidate in callerAgentIds)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            if (CanManage(job, AgentId.From(candidate)))
                return true;
        }

        return false;
    }
}
