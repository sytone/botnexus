using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;

namespace BotNexus.Cron.Actions;

#pragma warning disable CS1591

/// <summary>
/// Executes periodic memory consolidation ("dreaming") for an agent. Reads recent
/// daily memory notes from the agent's workspace, builds a consolidation prompt, and
/// dispatches a sub-agent session that updates MEMORY.md with insights.
/// </summary>
/// <remarks>
/// <para>
/// Configuration via <see cref="CronJob.Metadata"/>:
/// <list type="bullet">
/// <item><c>lookbackDays</c> (int, default 14) — how many days of daily notes to read</item>
/// <item><c>maxContentChars</c> (int, default 50000) — cap on source material size</item>
/// </list>
/// </para>
/// </remarks>
public sealed class MemoryDreamingCronAction : ICronAction
{
    /// <summary>The action type identifier used in cron job configuration.</summary>
    public const string TypeName = "memory-dreaming";

    /// <inheritdoc/>
    public string ActionType => TypeName;

    /// <inheritdoc/>
    public async Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var agentId = context.Job.AgentId
            ?? throw new InvalidOperationException("Cron job must define an agent id for memory-dreaming actions.");

        var logger = context.Services.GetService<ILogger<MemoryDreamingCronAction>>();

        var registry = context.Services.GetService<IAgentRegistry>()
            ?? throw new InvalidOperationException("Agent registry is not available.");

        var descriptor = registry.Get(agentId);
        if (descriptor is null)
        {
            logger?.LogWarning("Memory dreaming skipped: agent '{AgentId}' not found in registry", agentId.Value);
            return;
        }

        var workspacePath = ResolveWorkspacePath(context.Services, agentId);
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            logger?.LogWarning("Memory dreaming skipped for agent '{AgentId}': workspace not found at '{Path}'",
                agentId.Value, workspacePath);
            return;
        }

        // Read configuration from job metadata
        var lookbackDays = GetMetadataInt(context.Job.Metadata, "lookbackDays", 14);
        var maxContentChars = GetMetadataInt(context.Job.Metadata, "maxContentChars", 50_000);

        // Gather daily memory files
        var memoryDir = Path.Combine(workspacePath, "memory");
        var dailyNotes = ReadDailyNotes(memoryDir, lookbackDays, maxContentChars);

        if (dailyNotes.Count == 0)
        {
            logger?.LogInformation("Memory dreaming skipped for agent '{AgentId}': no daily notes in last {Days} days",
                agentId.Value, lookbackDays);
            return;
        }

        // Read existing MEMORY.md for context
        var memoryMdPath = Path.Combine(workspacePath, "MEMORY.md");
        var existingMemory = File.Exists(memoryMdPath)
            ? await File.ReadAllTextAsync(memoryMdPath, cancellationToken).ConfigureAwait(false)
            : string.Empty;

        // Build the consolidation prompt
        var prompt = BuildConsolidationPrompt(agentId, dailyNotes, existingMemory, lookbackDays);

        logger?.LogInformation(
            "Memory dreaming for agent '{AgentId}': {NoteCount} daily notes, {PromptLength} char prompt",
            agentId.Value, dailyNotes.Count, prompt.Length);

        // Dispatch via internal trigger (same pattern as agent-prompt)
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
    /// Reads daily memory notes within the lookback window, newest first.
    /// Returns (date, content) pairs. Caps total content at maxContentChars.
    /// </summary>
    internal static IReadOnlyList<(string Date, string Content)> ReadDailyNotes(
        string memoryDir, int lookbackDays, int maxContentChars)
    {
        if (!Directory.Exists(memoryDir))
            return [];

        var today = DateTimeOffset.UtcNow.Date;
        var cutoff = today.AddDays(-lookbackDays);
        var results = new List<(string Date, string Content)>();
        var totalChars = 0;

        // Enumerate files matching YYYY-MM-DD.md pattern, sorted newest first
        var files = Directory.GetFiles(memoryDir, "????-??-??.md")
            .Select(f => (Path: f, Date: TryParseDate(Path.GetFileNameWithoutExtension(f))))
            .Where(f => f.Date.HasValue && f.Date.Value >= cutoff)
            .OrderByDescending(f => f.Date!.Value)
            .ToList();

        foreach (var (path, date) in files)
        {
            if (totalChars >= maxContentChars)
                break;

            var content = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(content))
                continue;

            // Truncate individual file if it would exceed the cap
            var remaining = maxContentChars - totalChars;
            if (content.Length > remaining)
                content = content[..remaining] + "\n[...truncated]";

            results.Add((date!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), content));
            totalChars += content.Length;
        }

        return results;
    }

    /// <summary>
    /// Builds the consolidation prompt that instructs the agent to update MEMORY.md.
    /// </summary>
    internal static string BuildConsolidationPrompt(
        AgentId agentId,
        IReadOnlyList<(string Date, string Content)> dailyNotes,
        string existingMemory,
        int lookbackDays)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## Memory Consolidation Task");
        sb.AppendLine();
        sb.AppendLine($"You are performing periodic memory consolidation (\"dreaming\") for agent `{agentId.Value}`.");
        sb.AppendLine($"Review the last {lookbackDays} days of daily memory notes below.");
        sb.AppendLine();
        sb.AppendLine("### Instructions");
        sb.AppendLine();
        sb.AppendLine("1. Identify patterns, recurring themes, important decisions, and frequently-referenced items");
        sb.AppendLine("2. Consolidate these into durable insights that belong in long-term memory");
        sb.AppendLine("3. Update MEMORY.md by appending a new `## Consolidated — YYYY-MM-DD` section at the end");
        sb.AppendLine("4. Do NOT remove existing content from MEMORY.md — only append");
        sb.AppendLine("5. Keep consolidated entries concise — bullet points, not full paragraphs");
        sb.AppendLine("6. Skip routine/transient information (CI status, merge counts, etc.)");
        sb.AppendLine("7. Focus on: architectural decisions, learned patterns, recurring issues, key relationships");
        sb.AppendLine();
        sb.AppendLine("Use the `memory_save` tool to write the consolidated section to MEMORY.md.");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(existingMemory))
        {
            sb.AppendLine("### Current MEMORY.md (for context — do not duplicate existing entries)");
            sb.AppendLine();
            sb.AppendLine("```markdown");
            // Truncate if very long — the agent only needs context, not the full file
            var truncatedMemory = existingMemory.Length > 10_000
                ? existingMemory[..10_000] + "\n[...truncated]"
                : existingMemory;
            sb.AppendLine(truncatedMemory);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        sb.AppendLine("### Daily Notes to Consolidate");
        sb.AppendLine();

        foreach (var (date, content) in dailyNotes)
        {
            sb.AppendLine($"#### {date}");
            sb.AppendLine();
            sb.AppendLine(content);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static DateTime? TryParseDate(string fileName)
        => DateTime.TryParseExact(fileName, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? date
            : null;

    private static int GetMetadataInt(IReadOnlyDictionary<string, object?>? metadata, string key, int defaultValue)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || value is null)
            return defaultValue;

        return value switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            string s when int.TryParse(s, out var parsed) => parsed,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number => je.GetInt32(),
            _ => defaultValue
        };
    }

    /// <summary>
    /// Resolves the agent workspace path from the BotNexus home directory.
    /// Pattern: <c>{home}/agents/{agentId}/workspace</c>
    /// </summary>
    private static string? ResolveWorkspacePath(IServiceProvider services, AgentId agentId)
    {
        // Resolve BotNexusHome.RootPath via reflection to avoid a hard project reference
        var homeType = Type.GetType("BotNexus.Gateway.Configuration.BotNexusHome, BotNexus.Gateway");
        var home = homeType is null ? null : services.GetService(homeType);
        var rootPath = homeType?.GetProperty("RootPath")?.GetValue(home) as string;

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            rootPath = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".botnexus"));
        }

        return Path.Combine(rootPath, "agents", agentId.Value, "workspace");
    }
}
