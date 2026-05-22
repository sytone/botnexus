using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Provides the six built-in platform agents (researcher, coder, planner, reviewer, writer, analyst).
/// These agents are always available on every BotNexus instance and cannot be overridden by config.
/// </summary>
/// <remarks>
/// Built-in agents are registered before file/platform config sources so that
/// <see cref="AgentConfigurationHostedService"/> will skip any config-file agent with the same ID
/// (the code-based guard fires first).
/// Their <c>metadata.role</c> value matches the agent ID, making them discoverable via
/// <see cref="AgentDescriptor.SubAgentRoles"/> role-based grants.
/// </remarks>
public sealed class BuiltInAgentConfigurationSource : IAgentConfigurationSource
{
    /// <summary>
    /// Built-in agent IDs — a caller may check these to avoid accidentally shadowing them.
    /// </summary>
    public static readonly IReadOnlyList<string> AgentIds =
    [
        "researcher",
        "coder",
        "planner",
        "reviewer",
        "writer",
        "analyst"
    ];

    private static readonly IReadOnlyList<AgentDescriptor> _agents = BuildAgents();

    /// <inheritdoc/>
    public Task<IReadOnlyList<AgentDescriptor>> LoadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_agents);

    /// <inheritdoc/>
    public IDisposable? Watch(Action<IReadOnlyList<AgentDescriptor>> onChanged) => null;

    private static IReadOnlyList<AgentDescriptor> BuildAgents()
    {
        return
        [
            new AgentDescriptor
            {
                AgentId = AgentId.From("researcher"),
                DisplayName = "Researcher",
                Emoji = "🔍",
                Description = "Web research, URL fetch, and summarization. Read-only — no code execution or file writes.",
                ModelId = "gpt-4.1",
                ApiProvider = "copilot",
                IsolationStrategy = "in-process",
                SystemPrompt = "You are Researcher 🔍, a focused information-gathering agent. Your job is to find, fetch, and summarize information from the web and memory. You do not write or execute code. Return your findings clearly and concisely.",
                ToolIds =
                [
                    "web_search",
                    "web_fetch",
                    "memory_search",
                    "memory_get",
                    "read",
                    "glob",
                    "grep"
                ],
                Metadata = new Dictionary<string, object?> { ["role"] = "researcher" }
            },

            new AgentDescriptor
            {
                AgentId = AgentId.From("coder"),
                DisplayName = "Coder",
                Emoji = "💻",
                Description = "Code writing, editing, building, and testing. Full file and shell access.",
                ModelId = "gpt-4.1",
                ApiProvider = "copilot",
                IsolationStrategy = "in-process",
                SystemPrompt = "You are Coder 💻, a focused software-engineering agent. Your job is to write, edit, build, and test code. Work precisely and methodically. Always verify your changes compile and tests pass before finishing.",
                ToolIds =
                [
                    "read",
                    "write",
                    "edit",
                    "glob",
                    "grep",
                    "shell",
                    "exec",
                    "process",
                    "watch_file"
                ],
                Metadata = new Dictionary<string, object?> { ["role"] = "coder" }
            },

            new AgentDescriptor
            {
                AgentId = AgentId.From("planner"),
                DisplayName = "Planner",
                Emoji = "📋",
                Description = "Issue decomposition, spec writing, and task breakdown. Memory and GitHub access.",
                ModelId = "gpt-4.1",
                ApiProvider = "copilot",
                IsolationStrategy = "in-process",
                SystemPrompt = "You are Planner 📋, a focused planning and decomposition agent. Your job is to break down complex problems into clear, actionable sub-tasks, write specs, and track progress. Be thorough and precise.",
                ToolIds =
                [
                    "memory_search",
                    "memory_save",
                    "memory_get",
                    "web_search",
                    "read",
                    "write"
                ],
                Metadata = new Dictionary<string, object?> { ["role"] = "planner" }
            },

            new AgentDescriptor
            {
                AgentId = AgentId.From("reviewer"),
                DisplayName = "Reviewer",
                Emoji = "🔎",
                Description = "Code review, PR analysis, and quality checks. Read-only file and shell access.",
                ModelId = "gpt-4.1",
                ApiProvider = "copilot",
                IsolationStrategy = "in-process",
                SystemPrompt = "You are Reviewer 🔎, a focused code-review agent. Your job is to analyse code, identify issues, and provide clear actionable feedback. You are read-only — you do not modify files directly.",
                ToolIds =
                [
                    "read",
                    "glob",
                    "grep",
                    "shell",
                    "web_fetch",
                    "memory_search"
                ],
                Metadata = new Dictionary<string, object?> { ["role"] = "reviewer" }
            },

            new AgentDescriptor
            {
                AgentId = AgentId.From("writer"),
                DisplayName = "Writer",
                Emoji = "✍️",
                Description = "Documentation, changelogs, summaries, and content. File write access.",
                ModelId = "gpt-4.1",
                ApiProvider = "copilot",
                IsolationStrategy = "in-process",
                SystemPrompt = "You are Writer ✍️, a focused documentation and content agent. Your job is to produce clear, well-structured written content: docs, changelogs, summaries, and guides. Write for your audience.",
                ToolIds =
                [
                    "read",
                    "write",
                    "edit",
                    "glob",
                    "grep",
                    "web_search",
                    "web_fetch",
                    "memory_search"
                ],
                Metadata = new Dictionary<string, object?> { ["role"] = "writer" }
            },

            new AgentDescriptor
            {
                AgentId = AgentId.From("analyst"),
                DisplayName = "Analyst",
                Emoji = "📊",
                Description = "Data analysis, log triage, and metrics. Read and shell access.",
                ModelId = "gpt-4.1",
                ApiProvider = "copilot",
                IsolationStrategy = "in-process",
                SystemPrompt = "You are Analyst 📊, a focused data and log analysis agent. Your job is to triage logs, analyse data, surface metrics, and summarise findings. Be systematic and data-driven.",
                ToolIds =
                [
                    "read",
                    "glob",
                    "grep",
                    "shell",
                    "exec",
                    "web_fetch"
                ],
                Metadata = new Dictionary<string, object?> { ["role"] = "analyst" }
            }
        ];
    }
}
