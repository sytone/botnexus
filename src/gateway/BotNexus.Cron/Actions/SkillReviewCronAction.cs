using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text;

namespace BotNexus.Cron.Actions;


/// <summary>
/// Optional post-turn skill-review loop (PBI5 of epic #1827). A configurable background pass -
/// sibling to the memory-dreaming/self-improvement loop (<see cref="MemoryDreamingCronAction"/>) -
/// that, after a qualifying turn, dispatches a sub-agent with a RESTRICTED toolset to patch or
/// create skills. The reviewer may only touch the skill surface (<c>skill_manage</c> plus
/// read/inspection tools); it may not run arbitrary <c>exec</c>/<c>write</c>.
/// </summary>
/// <remarks>
/// <para>
/// The pass is <b>config-gated and DEFAULT-OFF</b> so it is non-breaking. Enablement and the
/// tool-call threshold are read from <see cref="CronJob.Metadata"/> (see <see cref="SkillReviewConfig"/>).
/// </para>
/// <para>
/// Trigger conditions (any one, when enabled): 5+ tool calls in the turn, a skill was loaded, the
/// user corrected/frustrated the agent, a reusable workflow was discovered, <c>skill_manage</c>
/// failed, or a loaded skill was found stale. See <see cref="ShouldTriggerReview"/>.
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

        var signals = SkillReviewSignals.FromMetadata(context.Job.Metadata);
        if (!ShouldTriggerReview(signals, config))
        {
            logger?.LogInformation(
                "Skill review skipped for agent '{AgentId}': no qualifying trigger (toolCalls={ToolCalls}, skillLoaded={SkillLoaded})",
                agentId.Value, signals.ToolCallCount, signals.SkillWasLoaded);
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

        var prompt = BuildReviewPrompt(agentId, signals, config.SessionSummary);

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
    public static string BuildReviewPrompt(AgentId agentId, SkillReviewSignals signals, string? sessionSummary)
    {
        ArgumentNullException.ThrowIfNull(signals);

        var sb = new StringBuilder();
        sb.AppendLine("## Post-Turn Skill Review");
        sb.AppendLine();
        sb.AppendLine($"You are performing an optional post-turn skill review for agent `{agentId.Value}`.");
        sb.AppendLine("The turn just completed qualified for review. Decide whether a durable, reusable");
        sb.AppendLine("skill improvement is warranted - and if so, make exactly one focused change.");
        sb.AppendLine();

        sb.AppendLine("### Why this turn qualified");
        sb.AppendLine();
        foreach (var reason in DescribeSignals(signals))
            sb.AppendLine($"- {reason}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(sessionSummary))
        {
            sb.AppendLine("### Turn summary");
            sb.AppendLine();
            sb.AppendLine(sessionSummary.Trim());
            sb.AppendLine();
        }

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
        if (signals.ToolCallCount > 0)
            yield return $"The turn made {signals.ToolCallCount} tool call(s).";
        if (signals.SkillWasLoaded)
            yield return "A skill was loaded during the turn.";
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
