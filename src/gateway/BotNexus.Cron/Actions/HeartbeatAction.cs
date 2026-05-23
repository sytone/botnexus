using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace BotNexus.Cron.Actions;

#pragma warning disable CS1591 // Public members implement framework contracts

/// <summary>
/// Dedicated cron action for system heartbeat jobs (actionType = "heartbeat").
/// Handles quiet-hours gating, HEARTBEAT.md emptiness pre-check, and heartbeat-specific trigger routing.
/// </summary>
public sealed class HeartbeatAction : ICronAction
{
    /// <inheritdoc/>
    public string ActionType => "heartbeat";

    /// <inheritdoc/>
    public async Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var agentId = context.Job.AgentId;
        if (string.IsNullOrWhiteSpace(agentId))
            throw new InvalidOperationException("Heartbeat cron job must define an agent id.");

        var logger = context.Services.GetService<ILogger<HeartbeatAction>>();
        var registry = context.Services.GetService<IAgentRegistry>();
        var descriptor = registry?.Get(AgentId.From(agentId));

        // Pre-flight: quiet hours
        var quietHours = descriptor?.Heartbeat?.QuietHours;
        var timezoneFallback = descriptor?.Soul?.Timezone ?? "UTC";
        if (quietHours is { Enabled: true }
            && IsInQuietHours(quietHours, quietHours.Timezone ?? timezoneFallback))
        {
            logger?.LogDebug("Skipping heartbeat for agent '{AgentId}' — quiet hours active.", agentId);
            return;
        }

        // Pre-flight: HEARTBEAT.md emptiness check
        var workspaceManager = context.Services.GetService<IAgentWorkspaceManager>();
        if (workspaceManager is not null)
        {
            var heartbeatContent = await ReadHeartbeatFileAsync(workspaceManager, agentId, cancellationToken);
            if (IsEffectivelyEmpty(heartbeatContent))
            {
                logger?.LogDebug(
                    "Skipping heartbeat for agent '{AgentId}' — HEARTBEAT.md is absent or effectively empty.",
                    agentId);
                return;
            }
        }

        var prompt = context.Job.Message;
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("Heartbeat cron job must define a message prompt.");

        var trigger = ResolveHeartbeatTrigger(context.Services, descriptor);

        var sessionId = await trigger
            .CreateSessionAsync(
                AgentId.From(agentId),
                prompt,
                cancellationToken,
                new InternalTriggerRequest
                {
                    CronJobId = context.Job.Id,
                    ModelOverride = context.Job.Model,
                    ConversationId = context.Job.ConversationId
                })
            .ConfigureAwait(false);

        context.RecordSessionId(sessionId.Value);
    }

    /// <summary>
    /// Reads the contents of HEARTBEAT.md from the agent's workspace.
    /// Returns null if the file does not exist or the workspace manager cannot locate the file.
    /// </summary>
    public static async Task<string?> ReadHeartbeatFileAsync(
        IAgentWorkspaceManager workspaceManager,
        string agentId,
        CancellationToken cancellationToken)
    {
        try
        {
            var workspacePath = workspaceManager.GetWorkspacePath(agentId);
            var heartbeatPath = Path.Combine(workspacePath, "HEARTBEAT.md");
            if (!File.Exists(heartbeatPath))
                return null;

            return await File.ReadAllTextAsync(heartbeatPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // If we can't read the file, treat it as present (run the heartbeat — safer to fire than skip)
            return "unreadable";
        }
    }

    /// <summary>
    /// Returns true when the HEARTBEAT.md content has no actionable tasks.
    /// A file is considered effectively empty when it is null/whitespace or contains only:
    /// <list type="bullet">
    ///   <item>Blank lines</item>
    ///   <item>Markdown headings (lines starting with #)</item>
    ///   <item>Horizontal rules (--- or ***)</item>
    ///   <item>Empty or unchecked checkboxes (- [ ] ...)</item>
    ///   <item>HTML/markdown comments (&lt;!-- ... --&gt;)</item>
    /// </list>
    /// </summary>
    public static bool IsEffectivelyEmpty(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return true;

        // Strip HTML/markdown comments
        var stripped = Regex.Replace(content, @"<!--.*?-->", string.Empty, RegexOptions.Singleline);

        foreach (var rawLine in stripped.Split('\n'))
        {
            var line = rawLine.Trim();

            if (line.Length == 0) continue;                              // blank
            if (line.StartsWith('#')) continue;                          // heading
            if (Regex.IsMatch(line, @"^[-*_]{3}$")) continue;          // horizontal rule
            if (Regex.IsMatch(line, @"^-\s*\[\s*\]\s*$")) continue;     // empty/blank checkbox (no task text)

            // Any other non-trivial line = has content
            return false;
        }

        return true;
    }

    /// <summary>
    /// Resolves the best available trigger for a heartbeat run.
    /// Priority: HeartbeatTrigger > SoulTrigger (soul agents) > CronTrigger.
    /// </summary>
    private static IInternalTrigger ResolveHeartbeatTrigger(IServiceProvider services, AgentDescriptor? descriptor)
    {
        var all = services.GetServices<IInternalTrigger>().ToList();

        var heartbeatTrigger = all.FirstOrDefault(t => t.Type.Equals(TriggerType.Heartbeat));
        if (heartbeatTrigger is not null)
            return heartbeatTrigger;

        if (descriptor?.Soul?.Enabled == true)
        {
            var soulTrigger = all.FirstOrDefault(t => t.Type.Equals(TriggerType.Soul));
            if (soulTrigger is not null)
                return soulTrigger;
        }

        return all.FirstOrDefault(t => t.Type.Equals(TriggerType.Cron))
            ?? throw new InvalidOperationException(
                "No suitable internal trigger found for heartbeat action (heartbeat, soul, or cron).");
    }

    private static bool IsInQuietHours(QuietHoursConfig config, string timezoneId)
    {
        var tz = TimeZoneHelper.Resolve(timezoneId);
        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
        var currentTime = localNow.TimeOfDay;

        if (!TimeSpan.TryParse(config.Start, out var start) ||
            !TimeSpan.TryParse(config.End, out var end))
            return false;

        if (start > end)
            return currentTime >= start || currentTime < end;

        return currentTime >= start && currentTime < end;
    }
}
