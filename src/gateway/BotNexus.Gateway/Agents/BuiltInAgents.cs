using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Defines the built-in internal agents that are always available on every BotNexus instance.
/// These agents serve common roles and can be used as sub-agent targets via
/// <c>spawn_subagent(targetAgentId: ...)</c> or <c>agent_converse</c>.
/// </summary>
/// <remarks>
/// Built-in agents are registered before config-based sources load, so they appear in
/// <see cref="AgentConfigurationHostedService"/>'s <c>_codeBasedAgentIds</c> set and
/// cannot be overridden by user configuration files.
/// </remarks>
public static class BuiltInAgents
{
    /// <summary>Web search, URL fetch, summarization. Read-only. No code execution.</summary>
    public static AgentDescriptor Researcher { get; } = new()
    {
        AgentId = AgentId.From("researcher"),
        DisplayName = "Researcher",
        Emoji = "🔍",
        Description = "Web search, URL fetch, and summarization. Read-only — no code execution.",
        ModelId = "",
        ApiProvider = "",
        SystemPrompt = "You are a research assistant. Find information, summarize content, and answer questions using web search and document reading. You do not execute code or modify files.",
        ToolIds = ["web_search", "web_fetch", "memory_search", "memory_get", "read", "glob", "grep"],
        Metadata = new Dictionary<string, object?> { ["role"] = "researcher", ["builtin"] = true },
    };

    /// <summary>Code writing, editing, building, testing. Full file and shell access.</summary>
    public static AgentDescriptor Coder { get; } = new()
    {
        AgentId = AgentId.From("coder"),
        DisplayName = "Coder",
        Emoji = "💻",
        Description = "Code writing, editing, building, and testing. Full file and shell access.",
        ModelId = "",
        ApiProvider = "",
        SystemPrompt = "You are a coding assistant. Write, edit, build, and test code. You have full file system and shell access within your workspace.",
        ToolIds = ["read", "write", "edit", "glob", "grep", "shell", "exec", "process", "watch_file"],
        Metadata = new Dictionary<string, object?> { ["role"] = "coder", ["builtin"] = true },
    };

    /// <summary>Issue decomposition, spec writing, task breakdown. Memory and web access.</summary>
    public static AgentDescriptor Planner { get; } = new()
    {
        AgentId = AgentId.From("planner"),
        DisplayName = "Planner",
        Emoji = "📋",
        Description = "Issue decomposition, spec writing, and task breakdown. Memory and web access.",
        ModelId = "",
        ApiProvider = "",
        SystemPrompt = "You are a planning assistant. Break down complex tasks, write specifications, decompose issues, and organize work. You have memory and web search access for research.",
        ToolIds = ["memory_search", "memory_save", "memory_get", "web_search", "read", "write"],
        Metadata = new Dictionary<string, object?> { ["role"] = "planner", ["builtin"] = true },
    };

    /// <summary>Code review, PR analysis, quality checks. Read-only file and shell access.</summary>
    public static AgentDescriptor Reviewer { get; } = new()
    {
        AgentId = AgentId.From("reviewer"),
        DisplayName = "Reviewer",
        Emoji = "🔎",
        Description = "Code review, PR analysis, and quality checks. Read-only file and shell access.",
        ModelId = "",
        ApiProvider = "",
        SystemPrompt = "You are a code review assistant. Analyze code quality, review pull requests, check for bugs and improvements. You have read-only access to files and can run diagnostic commands.",
        ToolIds = ["read", "glob", "grep", "shell", "web_fetch", "memory_search"],
        Metadata = new Dictionary<string, object?> { ["role"] = "reviewer", ["builtin"] = true },
    };

    /// <summary>Documentation, changelogs, summaries, content. File write access.</summary>
    public static AgentDescriptor Writer { get; } = new()
    {
        AgentId = AgentId.From("writer"),
        DisplayName = "Writer",
        Emoji = "✍️",
        Description = "Documentation, changelogs, summaries, and content creation. File write access.",
        ModelId = "",
        ApiProvider = "",
        SystemPrompt = "You are a writing assistant. Create documentation, changelogs, summaries, and other content. You can read and write files, search the web for reference material.",
        ToolIds = ["read", "write", "edit", "glob", "grep", "web_search", "web_fetch", "memory_search"],
        Metadata = new Dictionary<string, object?> { ["role"] = "writer", ["builtin"] = true },
    };

    /// <summary>Data analysis, log triage, metrics. Read and shell access.</summary>
    public static AgentDescriptor Analyst { get; } = new()
    {
        AgentId = AgentId.From("analyst"),
        DisplayName = "Analyst",
        Emoji = "📊",
        Description = "Data analysis, log triage, and metrics. Read and shell access.",
        ModelId = "",
        ApiProvider = "",
        SystemPrompt = "You are a data analysis assistant. Analyze logs, metrics, and data. You have read access to files and can run commands to process data.",
        ToolIds = ["read", "glob", "grep", "shell", "exec", "web_fetch"],
        Metadata = new Dictionary<string, object?> { ["role"] = "analyst", ["builtin"] = true },
    };

    /// <summary>
    /// All built-in agent descriptors. Registered at startup via DI.
    /// </summary>
    public static IReadOnlyList<AgentDescriptor> All { get; } =
    [
        Researcher,
        Coder,
        Planner,
        Reviewer,
        Writer,
        Analyst,
    ];
}
