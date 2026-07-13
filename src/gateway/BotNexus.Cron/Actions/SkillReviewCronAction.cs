using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text;

namespace BotNexus.Cron.Actions;


/// <summary>
/// Optional periodic skill-review loop (PBI5 of epic #1827). A configurable background pass -
/// sibling to the memory-dreaming/self-improvement loop (<see cref="MemoryDreamingCronAction"/>) -
/// that, once per period, reads a lookback window of the agent's session history, derives review
/// signals from it, and (when they qualify) dispatches a sub-agent with a RESTRICTED toolset to
/// patch or create skills. The reviewer may only touch the skill surface (<c>skill_manage</c> plus
/// read/inspection tools); it may not run arbitrary <c>exec</c>/<c>write</c>.
/// </summary>
/// <remarks>
/// <para>
/// The pass is <b>config-gated and DEFAULT-OFF</b> so it is non-breaking. Enablement, the
/// tool-call threshold, and the lookback window are read from <see cref="CronJob.Metadata"/> (see
/// <see cref="SkillReviewConfig"/>). Metadata carries <b>configuration only</b> - never per-turn
/// signals. The signals are <b>derived at tick time</b> from the session transcripts the gateway
/// already persists during normal operation (<see cref="SkillReviewSignals.FromSessions"/>), exactly
/// as memory-dreaming derives its input from the daily notes that accumulate on disk. There is no
/// separate per-turn producer to wire up: the session store <i>is</i> the producer.
/// </para>
/// <para>
/// Trigger conditions (any one, when enabled): the aggregate tool-call count across the window met
/// the configured threshold, a skill was loaded, or a <c>skill_manage</c> operation failed. See
/// <see cref="ShouldTriggerReview"/>. This is a periodic consolidated pass (one dispatch per tick),
/// so the period itself is the natural rate-limit and an idle window dispatches nothing.
/// </para>
/// </remarks>
public sealed class SkillReviewCronAction : ICronAction
{
    /// <summary>The action type identifier used in cron job configuration.</summary>
    public const string TypeName = "skill-review";

    /// <summary>
    /// The restricted toolset the review pass is permitted to use. Limited to the skill surface
    /// (<c>skill_manage</c>) plus read/inspection tools - no arbitrary <c>exec</c>/<c>shell</c>/<c>write</c>/<c>edit</c>.
    /// </summary>
    public static IReadOnlyList<string> AllowedTools { get; } =
    [
        "skill_manage",
        "skills",
        "skills_list",
        "skill_view",
        "read",
        "grep",
        "glob",
        "ls"
    ];

    /// <inheritdoc/>
    public string ActionType => TypeName;

    /// <inheritdoc/>
    public async Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var agentId = context.Job.AgentId
            ?? throw new InvalidOperationException("Cron job must define an agent id for skill-review actions.");

        var logger = context.Services.GetService<ILogger<SkillReviewCronAction>>();

        var config = SkillReviewConfig.FromMetadata(context.Job.Metadata);

        // Config-gated, default-off: bail out cheaply when disabled so the pass is non-breaking.
        if (!config.Enabled)
        {
            logger?.LogDebug("Skill review skipped for agent '{AgentId}': review loop disabled", agentId.Value);
            return;
        }

        var signals = await GatherSignalsAsync(context.Services, agentId, config, logger, cancellationToken)
            .ConfigureAwait(false);
        if (!ShouldTriggerReview(signals, config))
        {
            logger?.LogInformation(
                "Skill review skipped for agent '{AgentId}': no qualifying trigger over {Hours}h window " +
                "(sessions={Sessions}, toolCalls={ToolCalls}, skillLoaded={SkillLoaded}, skillManageFailed={SkillFailed})",
                agentId.Value, config.LookbackHours, signals.SessionCount, signals.ToolCallCount,
                signals.SkillWasLoaded, signals.SkillManageFailed);
            return;
        }

        var registry = context.Services.GetService<IAgentRegistry>()
            ?? throw new InvalidOperationException("Agent registry is not available.");
        var descriptor = registry.Get(agentId);
        if (descriptor is null)
        {
            logger?.LogWarning("Skill review skipped: agent '{AgentId}' not found in registry", agentId.Value);
            return;
        }

        var prompt = BuildReviewPrompt(agentId, signals, config.LookbackHours);

        logger?.LogInformation(
            "Skill review for agent '{AgentId}': dispatching restricted review pass ({PromptLength} char prompt, {ToolCount} allowed tools)",
            agentId.Value, prompt.Length, AllowedTools.Count);

        var trigger = context.Services.GetServices<IInternalTrigger>()
            .FirstOrDefault(t => t.Type.Equals(TriggerType.Cron))
            ?? throw new InvalidOperationException("Cron internal trigger is not registered.");

        var triggerRequest = new InternalTriggerRequest
        {
            CronJobId = context.Job.Id,
            JobName = context.Job.Name,
            ModelOverride = context.Job.Model,
            ConversationId = context.Job.ConversationId,
            CreatedBy = context.Job.CreatedBy
        };

        var sessionId = await trigger
            .CreateSessionAsync(agentId, prompt, cancellationToken, triggerRequest)
            .ConfigureAwait(false);

        context.RecordSessionId(sessionId);
        if (triggerRequest.ResolvedConversationId is { } resolvedConversationId)
            context.RecordConversationId(resolvedConversationId);
    }

    /// <summary>
    /// Reads a lookback window of the agent's session history from the <see cref="ISessionStore"/>
    /// and derives the aggregate review signals from it. This is the consumer half of the
    /// producer/consumer pair: the producer is the normal turn-transcript persistence that already
    /// writes tool-call entries to the session store during operation. When no session store is
    /// registered (e.g. a minimal host), returns empty signals so the pass simply no-ops.
    /// </summary>
    private static async Task<SkillReviewSignals> GatherSignalsAsync(
        IServiceProvider services,
        AgentId agentId,
        SkillReviewConfig config,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var store = services.GetService<ISessionStore>();
        if (store is null)
        {
            logger?.LogDebug("Skill review: no ISessionStore registered; nothing to review for agent '{AgentId}'", agentId.Value);
            return new SkillReviewSignals();
        }

        var cutoff = DateTimeOffset.UtcNow.AddHours(-config.LookbackHours);

        // ListSummariesAsync is the transcript-free window read (same shape as memory-dreaming's
        // ReadDailyNotes lookback). We take the newest MaxSessions summaries for this agent, then
        // load only those sessions' transcripts to derive signals - bounding the work per tick.
        var summaries = await store.ListSummariesAsync(cutoff, cancellationToken).ConfigureAwait(false);
        var relevant = summaries
            .Where(s => string.Equals(s.AgentId, agentId.Value, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.UpdatedAt)
            .Take(config.MaxSessions)
            .ToList();

        if (relevant.Count == 0)
            return new SkillReviewSignals();

        var sessions = new List<GatewaySession>(relevant.Count);
        foreach (var summary in relevant)
        {
            var session = await store.GetAsync(SessionId.From(summary.SessionId), cancellationToken).ConfigureAwait(false);
            if (session is not null)
                sessions.Add(session);
        }

        return SkillReviewSignals.FromSessions(sessions, cutoff);
    }

    /// <summary>
    /// Decides whether a post-turn skill review should run. Returns <c>false</c> whenever the
    /// review loop is disabled (default), guaranteeing the pass is non-breaking. When enabled,
    /// returns <c>true</c> if any qualifying signal is present: the tool-call count met the
    /// configured threshold, a skill was loaded, the user corrected/frustrated the agent, a
    /// reusable workflow was discovered, <c>skill_manage</c> failed, or a loaded skill was stale.
    /// </summary>
    public static bool ShouldTriggerReview(SkillReviewSignals signals, SkillReviewConfig config)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(config);

        if (!config.Enabled)
            return false;

        return signals.ToolCallCount >= config.MinToolCalls
            || signals.SkillWasLoaded
            || signals.UserCorrectedOrFrustrated
            || signals.DiscoveredReusableWorkflow
            || signals.SkillManageFailed
            || signals.LoadedSkillFoundStale;
    }

    /// <summary>
    /// Builds the reviewer prompt. Encodes the reviewer preference order (patch loaded skill &gt;
    /// patch umbrella skill &gt; add supporting file &gt; create new umbrella skill), the restricted
    /// toolset, and the avoid-list (no one-off task narratives, no PR/issue numbers as skill names,
    /// no transient environment/setup failures encoded as durable constraints, no negative
    /// "tool X is broken" claims).
    /// </summary>
    public static string BuildReviewPrompt(AgentId agentId, SkillReviewSignals signals, int lookbackHours)
    {
        ArgumentNullException.ThrowIfNull(signals);

        var sb = new StringBuilder();
        sb.AppendLine("## Periodic Skill Review");
        sb.AppendLine();
        sb.AppendLine($"You are performing an optional periodic skill review for agent `{agentId.Value}`.");
        sb.AppendLine($"The last {lookbackHours}h of activity qualified for review. Decide whether a durable,");
        sb.AppendLine("reusable skill improvement is warranted - and if so, make exactly one focused change.");
        sb.AppendLine();

        sb.AppendLine("### Why this period qualified");
        sb.AppendLine();
        foreach (var reason in DescribeSignals(signals))
            sb.AppendLine($"- {reason}");
        sb.AppendLine();

        sb.AppendLine("### Restricted toolset");
        sb.AppendLine();
        sb.AppendLine("This is a **restricted** review pass. You may ONLY use the skill-management surface");
        sb.AppendLine("and read/inspection tools:");
        sb.AppendLine();
        sb.AppendLine("- `skill_manage` (create / edit / patch / write_file / remove_file / delete)");
        sb.AppendLine("- `skills`, `skills_list`, `skill_view` (inspect existing skills)");
        sb.AppendLine("- `read`, `grep`, `glob`, `ls` (inspect files)");
        sb.AppendLine();
        sb.AppendLine("You must NOT run arbitrary `exec`, `shell`, `write`, or `edit` outside the skill surface.");
        sb.AppendLine();

        sb.AppendLine("### Reviewer preference order (choose the FIRST that fits)");
        sb.AppendLine();
        sb.AppendLine("1. **Patch a currently loaded skill** - if a skill in play was stale, wrong, or missing a pitfall.");
        sb.AppendLine("2. **Patch an existing umbrella skill** - fold the learning into the closest class-level skill.");
        sb.AppendLine("3. **Add a supporting file** under an existing umbrella skill (references/, templates/, scripts/, assets/).");
        sb.AppendLine("4. **Create a new** class-level umbrella skill ONLY if nothing existing fits.");
        sb.AppendLine();
        sb.AppendLine("Prefer `patch` over a full `edit`. Keep the change small and reviewable.");
        sb.AppendLine();

        sb.AppendLine("### Avoid (do NOT encode these as skills)");
        sb.AppendLine();
        sb.AppendLine("- **One-off task narratives** - a specific task's play-by-play is not a durable skill.");
        sb.AppendLine("- **PR or issue numbers as skill names** - e.g. never name a skill `pr-1832` or embed an issue number as the skill identity.");
        sb.AppendLine("- **Transient environment/setup failures as durable constraints** - a flaky clone timeout, a one-time missing dependency, or a machine-specific setup hiccup is transient, not a rule.");
        sb.AppendLine("- **Negative \"tool X is broken\" claims** - do not record that a tool is broken; capture the correct working procedure instead.");
        sb.AppendLine();
        sb.AppendLine("A good skill has trigger conditions, numbered steps, exact commands, pitfalls, and verification steps.");
        sb.AppendLine("If nothing durable and reusable is worth capturing, do nothing - that is a valid outcome.");
        sb.AppendLine();

        return sb.ToString();
    }

    private static IEnumerable<string> DescribeSignals(SkillReviewSignals signals)
    {
        if (signals.SessionCount > 0)
            yield return $"{signals.SessionCount} session(s) were active in the window.";
        if (signals.ToolCallCount > 0)
            yield return $"The period made {signals.ToolCallCount} tool call(s) in total.";
        if (signals.SkillWasLoaded)
            yield return "A skill was loaded during the period.";
        if (signals.UserCorrectedOrFrustrated)
            yield return "The user corrected the agent or showed frustration.";
        if (signals.DiscoveredReusableWorkflow)
            yield return "A reusable workflow or non-trivial procedure was discovered.";
        if (signals.SkillManageFailed)
            yield return "A `skill_manage` operation failed and may need fixing.";
        if (signals.LoadedSkillFoundStale)
            yield return "A loaded skill was found to be stale or incorrect.";
    }
}
