namespace BotNexus.Cron;

/// <summary>
/// The ownership state a caller's authorization decision was made against, carried down to the
/// store so the commit can be conditioned on it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CronJobOwnership"/> answers "may this caller manage this job?" at the CALLER seam,
/// against a job snapshot. That answer goes stale: <c>CronTool.UpdateAsync</c> reads, decides, then
/// spends ~60 lines of argument parsing, model preflight and an awaited alert-target validation
/// before it writes, and the write itself rewrites the very columns the decision rested on
/// (<c>created_by</c>, <c>agent_id</c>). An ownership transfer landing in that window used to be
/// silently overwritten by a caller authorized against the previous owner (#3573).
/// </para>
/// <para>
/// Re-reading in the tool would only narrow the window, not close it, so the expectation is pushed
/// into the <c>WHERE</c> clause of the UPDATE instead: the commit either observes the ownership it
/// was authorized against or affects zero rows. This is the same seam-level discipline as the
/// #2133 narrow-write split rather than a second, weaker check layered above it.
/// </para>
/// </remarks>
/// <param name="CreatedBy">The <c>created_by</c> value observed when authorization was granted.</param>
/// <param name="AgentId">The <c>agent_id</c> value observed when authorization was granted.</param>
public readonly record struct CronJobOwnershipExpectation(string? CreatedBy, string? AgentId)
{
    /// <summary>
    /// Captures the ownership columns of <paramref name="job"/> as they were read.
    /// </summary>
    public static CronJobOwnershipExpectation From(CronJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new CronJobOwnershipExpectation(job.CreatedBy, job.AgentId?.Value);
    }
}
