using BotNexus.Domain.Primitives;
using BotNexus.Domain.Text;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Gateway.Configuration;
using BotNexus.Memory;
using BotNexus.Memory.Learning;
using BotNexus.Memory.Models;
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

        // #2373: classify an unresolvable model override before dispatch so the run records the
        // real reason instead of an opaque provider error raised deep inside the agent turn.
        CronModelPreflight.EnsureResolvable(
            context.Services.GetService<BotNexus.Agent.Providers.Core.Registry.ModelRegistry>(),
            context.Job.Model);

        // Dispatch via internal trigger (same pattern as agent-prompt)
        var trigger = context.Services.GetServices<IInternalTrigger>()
            .FirstOrDefault(t => t.Type.Equals(TriggerType.Cron))
            ?? throw new InvalidOperationException("Cron internal trigger is not registered.");

        var triggerRequest = new InternalTriggerRequest
        {
            CronJobId = context.Job.Id,
            JobName = ExternalText.Sanitize(context.Job.Name, ExternalText.DefaultDisplayLength),
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

        // Phase 2: Promote insights to shared stores (runs independently of consolidation)
        await PromoteToSharedStoresAsync(context, agentId, lookbackDays, logger, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the learning extraction pipeline on the agent's memory store and promotes
    /// routed knowledge items to shared stores.
    /// </summary>
    private static async Task PromoteToSharedStoresAsync(
        CronExecutionContext context,
        AgentId agentId,
        int lookbackDays,
        ILogger? logger,
        CancellationToken ct)
    {
        var sharedRegistry = context.Services.GetService<ISharedMemoryStoreRegistry>();
        if (sharedRegistry is null)
        {
            logger?.LogDebug("Shared memory promotion skipped: no ISharedMemoryStoreRegistry registered");
            return;
        }

        var writableStores = sharedRegistry.GetWritableStores(agentId.Value);
        if (writableStores.Count == 0)
        {
            logger?.LogDebug("Shared memory promotion skipped: agent '{AgentId}' has no writable shared stores", agentId.Value);
            return;
        }

        var memoryFactory = context.Services.GetService<IMemoryStoreFactory>();
        if (memoryFactory is null)
        {
            logger?.LogDebug("Shared memory promotion skipped: no IMemoryStoreFactory registered");
            return;
        }

        var agentStore = memoryFactory.Create(agentId);
        await agentStore.InitializeAsync(ct).ConfigureAwait(false);

        try
        {
            // Get recent entries from the agent's private store
            var cutoffDate = DateTimeOffset.UtcNow.AddDays(-lookbackDays);
            var recentEntries = await agentStore.SearchAsync(
                "*", topK: 200, filter: new MemorySearchFilter { AfterDate = cutoffDate }, ct: ct)
                .ConfigureAwait(false);

            if (recentEntries.Count == 0)
            {
                logger?.LogDebug("Shared memory promotion skipped: no recent entries for agent '{AgentId}'", agentId.Value);
                return;
            }

            // Build routing rules from config (route all categories to writable stores)
            var rules = BuildRoutingRules(sharedRegistry, agentId.Value);
            if (rules.Count == 0)
            {
                logger?.LogDebug("Shared memory promotion skipped: no routing rules generated for agent '{AgentId}'", agentId.Value);
                return;
            }

            var pipeline = new LearningExtractionPipeline(
                rules,
                logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

            var extracted = await pipeline.ExtractAsync(recentEntries, ct).ConfigureAwait(false);

            var promotable = extracted.Where(e => e.TargetStore is not null).ToList();
            if (promotable.Count == 0)
            {
                logger?.LogInformation("Shared memory promotion: no promotable items found for agent '{AgentId}'", agentId.Value);
                return;
            }

            var promoter = new SharedMemoryPromoter(
                sharedRegistry,
                logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

            var promoted = await promoter.PromoteAsync(agentId.Value, promotable, ct).ConfigureAwait(false);
            logger?.LogInformation(
                "Shared memory promotion complete for agent '{AgentId}': {Promoted} items promoted",
                agentId.Value, promoted);
        }
        finally
        {
            await agentStore.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds routing rules that send high-confidence knowledge to the agent's writable shared stores.
    /// Uses a default confidence threshold of 0.7 for all categories.
    /// </summary>
    internal static IReadOnlyList<KnowledgeRoutingRule> BuildRoutingRules(
        ISharedMemoryStoreRegistry registry, string agentId)
    {
        var writableStores = registry.GetWritableStores(agentId);
        if (writableStores.Count == 0)
            return [];

        // Route to the first writable store by default
        // Future: allow per-category store mapping via metadata
        var targetStore = writableStores[0];

        return
        [
            new KnowledgeRoutingRule { Category = null, MinConfidence = 0.7, TargetStore = targetStore }
        ];
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
                content = TextTruncation.SafeTruncate(content, remaining, "\n[...truncated]")!;

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
            var truncatedMemory = TextTruncation.SafeTruncate(existingMemory, 10_000, "\n[...truncated]")!;
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
    /// <remarks>
    /// Internal rather than private so #2819 regression coverage can assert the resolved path
    /// directly. Dreaming otherwise only reveals its target home by rewriting a real MEMORY.md,
    /// which is not something a test may do to the developer's live workspace.
    /// </remarks>
    internal static string? ResolveWorkspacePath(IServiceProvider services, AgentId agentId)
    {
        // #2819: bind BotNexusHome BY TYPE. This was a Type.GetType("..., BotNexus.Gateway")
        // string, which is exactly the second instance of the defect that made the cron store
        // open the shared live home: #2765/#2777 moved the type into BotNexus.Gateway.Configuration,
        // the lookup began returning null at runtime with nothing failing at compile time, and
        // every caller silently fell through to the user-profile default below. Here that meant a
        // gateway running against an isolated --target home dreamt over the DEVELOPER'S real agent
        // workspace. A direct reference turns a future move into a build failure.
        var home = services.GetService<BotNexusHome>();
        var rootPath = home?.RootPath;

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            rootPath = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".botnexus"));

            services.GetService<ILogger<MemoryDreamingCronAction>>()?.LogWarning(
                "No {HomeType} was registered, so the memory-dreaming workspace path fell back to the shared " +
                "user-profile root {FallbackPath}. Any isolated home supplied by the host is being IGNORED " +
                "and this process will read and write the live agent workspace (#2819).",
                nameof(BotNexusHome),
                rootPath);
        }

        return Path.Combine(rootPath, "agents", agentId.Value, "workspace");
    }
}
