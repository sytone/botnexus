using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Cron;

/// <summary>
/// Classification of a cron job's owning agent, produced by <see cref="CronAgentPreflight.Resolve"/>.
/// </summary>
public enum CronAgentPreflightKind
{
    /// <summary>
    /// No agent registry was available, so the job's owner could not be checked. Treated as
    /// "cannot know", never as a rejection - a host that has not registered <see cref="IAgentRegistry"/>
    /// (minimal test hosts, early startup) must not start reporting every cron job's agent as
    /// missing. This is the DI condition, and it is deliberately not an "agent missing" report.
    /// </summary>
    RegistryUnavailable,

    /// <summary>The job's agent id resolved to a registered descriptor.</summary>
    Resolved,

    /// <summary>
    /// The registry was present and positively reported no descriptor for the job's agent id -
    /// the agent was deleted, renamed, or never registered.
    /// </summary>
    Missing
}

/// <summary>
/// The outcome of preflighting a cron job's <c>AgentId</c> against the agent registry.
/// </summary>
/// <param name="Kind">The classification.</param>
/// <param name="Descriptor">The resolved descriptor when <see cref="CronAgentPreflightKind.Resolved"/>; otherwise <see langword="null"/>.</param>
/// <param name="Reason">Operator-facing reason and recovery guidance when the agent is missing; otherwise <see langword="null"/>.</param>
public readonly record struct CronAgentPreflightResult(
    CronAgentPreflightKind Kind,
    AgentDescriptor? Descriptor,
    string? Reason)
{
    /// <summary>
    /// Whether this result should fail the run fast. Only the "we positively know this agent does
    /// not exist" kind is a rejection; a missing registry deliberately is not.
    /// </summary>
    public bool IsRejection => Kind == CronAgentPreflightKind.Missing;
}

/// <summary>
/// Preflight and classification for a cron job's owning agent (#3210).
/// <para>
/// Before this existed, <c>AgentPromptAction</c> resolved <c>registry?.Get(agentId)</c> and could
/// not tell a <see langword="null"/> descriptor (agent deleted/renamed/never registered) apart from
/// a live agent with soul disabled: both fell through to <c>TriggerType.Cron</c> and dispatched
/// anyway. The job then failed once per scheduled fire, forever, with an opaque error raised deep
/// inside the trigger that named the symptom but not the cause.
/// </para>
/// <para>
/// This mirrors <see cref="CronModelPreflight"/> exactly - same defect class, same method, four
/// lines apart. An unresolvable owner should fail fast, once, with recovery guidance.
/// </para>
/// </summary>
public static class CronAgentPreflight
{
    /// <summary>Maximum length of any reason string this type emits, bounding the run record.</summary>
    public const int MaxReasonLength = CronModelPreflight.MaxReasonLength;

    /// <summary>
    /// Classifies <paramref name="agentId"/> against <paramref name="registry"/>.
    /// </summary>
    /// <param name="registry">The agent registry, or <see langword="null"/> when the host has none.</param>
    /// <param name="agentId">The job's configured agent id.</param>
    /// <returns>The classification; never throws.</returns>
    public static CronAgentPreflightResult Resolve(IAgentRegistry? registry, AgentId agentId)
    {
        if (registry is null)
            return new CronAgentPreflightResult(CronAgentPreflightKind.RegistryUnavailable, null, null);

        var descriptor = registry.Get(agentId);
        if (descriptor is not null)
            return new CronAgentPreflightResult(CronAgentPreflightKind.Resolved, descriptor, null);

        return new CronAgentPreflightResult(
            CronAgentPreflightKind.Missing,
            null,
            Truncate(
                $"Cron job's agent '{agentId.Value}' is not registered. " +
                "Re-register the agent, or delete this job or reassign it to a registered agent."));
    }

    /// <summary>
    /// Run-time guard for the cron actions: throws with the classified reason when the job's agent
    /// positively does not exist, so the scheduler records that reason - naming the agent id and the
    /// recovery action - on the run record instead of an opaque error raised deep inside the trigger.
    /// Returns the resolved descriptor (or <see langword="null"/> when the registry is unavailable)
    /// so callers do not re-query.
    /// </summary>
    /// <param name="registry">The agent registry, or <see langword="null"/> when the host has none.</param>
    /// <param name="agentId">The job's configured agent id.</param>
    /// <returns>The resolved descriptor, or <see langword="null"/> when no registry was available.</returns>
    /// <exception cref="InvalidOperationException">The registry positively reports no such agent.</exception>
    public static AgentDescriptor? EnsureResolvable(IAgentRegistry? registry, AgentId agentId)
    {
        var result = Resolve(registry, agentId);
        return result.IsRejection
            ? throw new InvalidOperationException(result.Reason)
            : result.Descriptor;
    }

    private static string Truncate(string value)
    {
        if (value.Length <= MaxReasonLength)
            return value;

        const string ellipsis = "...";
        return value[..(MaxReasonLength - ellipsis.Length)] + ellipsis;
    }
}
