using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using BotNexus.Agent.Tools;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Agent;

/// <summary>
/// Builds full agent context from workspace files, memory, tools, and runtime state.
/// </summary>
public sealed class AgentContextBuilder : IContextBuilder
{
    private const string SectionSeparator = "\n\n---\n\n";
    private const int ApproxCharsPerToken = 4;

    private static readonly string[] BootstrapFiles = ["SOUL.md", "IDENTITY.md", "USER.md"];

    private readonly IAgentWorkspace _workspace;
    private readonly IMemoryStore _memoryStore;
    private readonly ToolRegistry _toolRegistry;
    private readonly ISkillsLoader _skillsLoader;
    private readonly BotNexusConfig _config;
    private readonly ILogger<AgentContextBuilder> _logger;

    public AgentContextBuilder(
        IAgentWorkspace workspace,
        IMemoryStore memoryStore,
        ToolRegistry toolRegistry,
        ISkillsLoader skillsLoader,
        IOptions<BotNexusConfig> config,
        ILogger<AgentContextBuilder> logger)
    {
        _workspace = workspace;
        _memoryStore = memoryStore;
        _toolRegistry = toolRegistry;
        _skillsLoader = skillsLoader;
        _config = config.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<string> BuildSystemPromptAsync(string agentName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _workspace.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var maxChars = ResolveMaxContextFileChars(agentName);
        var parts = new List<string>
        {
            BuildIdentityBlock(agentName)
        };

        foreach (var fileName in BootstrapFiles)
        {
            var content = await _workspace.ReadFileAsync(fileName, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
                continue;

            parts.Add($"## {fileName}\n\n{Truncate(content, maxChars)}");
        }

        parts.Add(Truncate(GenerateAgentsMarkdown(), maxChars));
        parts.Add(Truncate(GenerateToolsMarkdown(), maxChars));

        var skillsSections = await BuildSkillsSectionsAsync(agentName, maxChars, cancellationToken).ConfigureAwait(false);
        parts.AddRange(skillsSections);

        var memorySections = await BuildMemorySectionsAsync(agentName, maxChars, cancellationToken).ConfigureAwait(false);
        parts.AddRange(memorySections);

        return string.Join(SectionSeparator, parts.Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    /// <inheritdoc/>
    public async Task<List<ChatMessage>> BuildMessagesAsync(
        string agentName,
        IReadOnlyList<ChatMessage> history,
        string currentMessage,
        string? channel = null,
        string? chatId = null,
        CancellationToken cancellationToken = default)
    {
        var systemPrompt = await BuildSystemPromptAsync(agentName, cancellationToken).ConfigureAwait(false);
        var runtimeMessage = BuildRuntimeMessage(currentMessage, channel, chatId);
        var maxChars = ResolveContextWindowChars();
        var historyBudget = Math.Max(0, maxChars - systemPrompt.Length - runtimeMessage.Length);

        var trimmedHistory = TrimHistoryToBudget(
            history.Where(static h => !string.Equals(h.Role, "system", StringComparison.OrdinalIgnoreCase)).ToList(),
            historyBudget,
            _logger);

        var messages = new List<ChatMessage>(trimmedHistory.Count + 2)
        {
            new("system", systemPrompt)
        };
        messages.AddRange(trimmedHistory);
        messages.Add(new("user", runtimeMessage));
        return messages;
    }

    internal static IReadOnlyList<ChatMessage> TrimHistoryToBudget(
        IReadOnlyList<ChatMessage> history,
        int budget,
        ILogger? logger = null)
    {
        var included = new List<ChatMessage>();
        foreach (var message in Enumerable.Reverse(history))
        {
            if (budget - message.Content.Length < 0)
            {
                logger?.LogDebug("Context window budget exhausted at {Count} history entries", included.Count);
                break;
            }

            budget -= message.Content.Length;
            included.Insert(0, message);
        }

        return included;
    }

    private async Task<IReadOnlyList<string>> BuildMemorySectionsAsync(
        string agentName,
        int maxChars,
        CancellationToken cancellationToken)
    {
        var sections = new List<string>();

        var longTermMemory = await _memoryStore.ReadAsync(agentName, "MEMORY", cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(longTermMemory))
            sections.Add($"## MEMORY.md\n\n{Truncate(longTermMemory, maxChars)}");

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var todayMemory = await _memoryStore.ReadAsync(agentName, $"daily/{today}", cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(todayMemory))
            sections.Add($"## memory/daily/{today}.md\n\n{Truncate(todayMemory, maxChars)}");

        var yesterday = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var yesterdayMemory = await _memoryStore.ReadAsync(agentName, $"daily/{yesterday}", cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(yesterdayMemory))
            sections.Add($"## memory/daily/{yesterday}.md\n\n{Truncate(yesterdayMemory, maxChars)}");

        return sections;
    }

    private async Task<IReadOnlyList<string>> BuildSkillsSectionsAsync(
        string agentName,
        int maxChars,
        CancellationToken cancellationToken)
    {
        var skills = await _skillsLoader.LoadSkillsAsync(agentName, cancellationToken).ConfigureAwait(false);
        if (skills.Count == 0)
            return [];

        var sections = new List<string>();
        var sb = new StringBuilder();
        
        sb.AppendLine("## SKILLS.md");
        sb.AppendLine();
        sb.AppendLine("Available skills loaded for this agent:");
        sb.AppendLine();

        foreach (var skill in skills)
        {
            sb.AppendLine($"### {skill.Name}");
            sb.AppendLine();
            sb.AppendLine($"**Description:** {skill.Description}");
            if (!string.IsNullOrWhiteSpace(skill.Version))
                sb.AppendLine($"**Version:** {skill.Version}");
            sb.AppendLine($"**Scope:** {skill.Scope}");
            sb.AppendLine();
            sb.AppendLine(skill.Content);
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        sections.Add(Truncate(sb.ToString().TrimEnd(), maxChars));
        return sections;
    }

    private string BuildIdentityBlock(string agentName)
    {
        var utcNow = DateTimeOffset.UtcNow;
        return
            $"""
            ## Identity

            - Agent: {agentName}
            - Platform: {RuntimeInformation.OSDescription}
            - Workspace: {_workspace.WorkspacePath}
            - Time (UTC): {utcNow:O}

            ### Guidelines
            - Follow workspace and memory instructions as source of truth.
            - Prefer concise, actionable responses.
            - Use tools deliberately and safely.
            
            ### Tool Use Instructions
            - You have access to tools to accomplish tasks. USE them proactively — do not just narrate what you would do.
            - When you need information or need to perform an action, call the appropriate tool immediately rather than describing it or asking the user.
            - Always use tools when they can help. Do not just describe what you would do — actually do it.
            - State your intent briefly, then make the tool call(s). Do not predict or claim results before receiving them.
            """;
    }

    private string BuildRuntimeMessage(string currentMessage, string? channel, string? chatId)
    {
        var utcNow = DateTimeOffset.UtcNow;
        var content = currentMessage ?? string.Empty;

        return
            $"""
            ## Runtime Context
            - Time (UTC): {utcNow:O}
            - Channel: {channel ?? "unknown"}
            - Chat ID: {chatId ?? "unknown"}

            ## User Message
            {content}
            """;
    }

    private string GenerateAgentsMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## AGENTS.md");
        sb.AppendLine();
        sb.AppendLine("Configured agents:");
        sb.AppendLine();

        sb.AppendLine("### default");
        sb.AppendLine($"- Model: {_config.Agents.Model}");
        sb.AppendLine("- Role: default agent");
        sb.AppendLine();

        foreach (var (name, agent) in _config.Agents.Named.OrderBy(static kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"### {name}");
            sb.AppendLine($"- Model: {agent.Model ?? _config.Agents.Model}");
            sb.AppendLine($"- Role: {ResolveRole(agent)}");
            if (!string.IsNullOrWhiteSpace(agent.Provider))
                sb.AppendLine($"- Provider: {agent.Provider}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private string GenerateToolsMarkdown()
    {
        var definitions = _toolRegistry.GetDefinitions()
            .OrderBy(static d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("## TOOLS.md");
        sb.AppendLine();

        if (definitions.Count == 0)
        {
            sb.AppendLine("No tools registered.");
            return sb.ToString().TrimEnd();
        }

        foreach (var definition in definitions)
        {
            sb.AppendLine($"- {definition.Name}: {definition.Description}");
        }

        return sb.ToString().TrimEnd();
    }

    private int ResolveMaxContextFileChars(string agentName)
    {
        var agentConfig = _config.Agents.Named.GetValueOrDefault(agentName);
        var configured = agentConfig?.MaxContextFileChars ?? 8000;
        return configured > 0 ? configured : 8000;
    }

    private int ResolveContextWindowChars()
    {
        var tokens = _config.Agents.ContextWindowTokens ?? 65536;
        return Math.Max(1, tokens) * ApproxCharsPerToken;
    }

    private static string ResolveRole(AgentConfig agent)
    {
        if (!string.IsNullOrWhiteSpace(agent.SystemPrompt))
            return ExtractSingleLine(agent.SystemPrompt);
        if (!string.IsNullOrWhiteSpace(agent.SystemPromptFile))
            return $"from {agent.SystemPromptFile}";
        return "unspecified";
    }

    private static string ExtractSingleLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(line))
            return "unspecified";
        return line.Length <= 120 ? line : $"{line[..120]}...";
    }

    private static string Truncate(string content, int maxChars)
    {
        if (content.Length <= maxChars)
            return content;

        return $"{content[..maxChars]}\n[truncated]";
    }
}
