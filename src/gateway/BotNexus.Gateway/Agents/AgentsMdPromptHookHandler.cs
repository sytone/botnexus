using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Hooks;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Hook handler that adds a lightweight, always-on nudge telling the agent that
/// <c>AGENTS.md</c> convention files may apply to the directories it works in and that it
/// can load them on demand with the <c>get_agent_files</c> tool.
/// </summary>
/// <remarks>
/// <para>
/// This mirrors the convention used by Claude Code and similar agentic tooling: repository
/// maintainers place <c>AGENTS.md</c> files at the repo root and in subdirectories to provide
/// context-aware instructions that agents should respect.
/// </para>
/// <para>
/// Rather than eagerly injecting every discoverable <c>AGENTS.md</c> into the system prompt on
/// every turn - which could embed hundreds of files across all granted paths and exhaust the
/// context window - discovery is <b>pull-based</b>. The agent calls <c>get_agent_files</c> with
/// the path it is working in and receives only the relevant root-to-directory chain of files.
/// This nudge is the always-on hint that makes the agent aware the tool exists and when to reach
/// for it; it costs a single line of prompt rather than the file contents. The agent may not
/// always call the tool, but a missed call is far cheaper than a blown context window.
/// </para>
/// <para>
/// The agent's own workspace <c>AGENTS.md</c> continues to be loaded eagerly by
/// <see cref="WorkspaceContextBuilder"/> (it is small and always relevant); this handler only
/// covers the broader, potentially large set of project directories the agent is granted access to.
/// </para>
/// </remarks>
public sealed class AgentsMdPromptHookHandler
    : IHookHandler<BeforePromptBuildEvent, BeforePromptBuildResult>
{
    private const string AgentsMdFileName = "AGENTS.md";
    private const string GitDirectoryName = ".git";

    private readonly IAgentWorkspaceManager _workspaceManager;
    private readonly IFileSystem _fileSystem;

    /// <summary>
    /// The always-on nudge appended to the system prompt. Kept to a couple of lines so it never
    /// meaningfully competes for context budget.
    /// </summary>
    private const string Nudge =
        "<!-- AGENTS.md conventions -->\n"
        + "Repositories may contain AGENTS.md files (at the repo root and in subdirectories) that "
        + "define conventions you must follow when working in that tree. Before creating or editing "
        + "files in a directory, call the `get_agent_files` tool with that path to load the AGENTS.md "
        + "files that apply there (it walks the path up to the repository root). Do not assume none "
        + "exist - check with the tool.";

    /// <summary>
    /// Priority is lower (later) than other hook handlers so workspace files are already loaded
    /// before this nudge is appended.
    /// </summary>
    public int Priority => 200;

    /// <summary>
    /// Initializes a new instance of <see cref="AgentsMdPromptHookHandler"/>.
    /// </summary>
    public AgentsMdPromptHookHandler(
        IAgentWorkspaceManager workspaceManager,
        IFileSystem fileSystem)
    {
        _workspaceManager = workspaceManager;
        _fileSystem = fileSystem;
    }

    /// <inheritdoc />
    public Task<BeforePromptBuildResult?> HandleAsync(
        BeforePromptBuildEvent hookEvent,
        CancellationToken ct = default)
    {
        // The nudge is unconditional and cheap: it makes the agent aware of get_agent_files
        // regardless of where it later chooses to work. Pull-based loading keeps context bounded.
        return Task.FromResult<BeforePromptBuildResult?>(new BeforePromptBuildResult
        {
            AppendSystemContext = Nudge
        });
    }

    /// <summary>
    /// Walks from <paramref name="startPath"/> upward through parent directories to the nearest
    /// git repository root (a directory containing a <c>.git</c> entry), collecting
    /// <c>AGENTS.md</c> files ordered root-first. Retained for reuse and test coverage; the
    /// on-demand equivalent lives in <c>AgentFilesTool</c>.
    /// </summary>
    /// <param name="startPath">Directory to begin the walk from.</param>
    /// <returns>
    /// List of (absolutePath, content) tuples in root-first order. Empty when no repo root or no
    /// <c>AGENTS.md</c> files are found.
    /// </returns>
    internal List<(string Path, string Content)> CollectAgentsMdFiles(string startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath))
            return [];

        var chain = new List<string>();
        string? repoRoot = null;
        var current = startPath;
        while (!string.IsNullOrEmpty(current))
        {
            var gitDir = _fileSystem.Path.Combine(current, GitDirectoryName);
            if (_fileSystem.Directory.Exists(gitDir) || _fileSystem.File.Exists(gitDir))
            {
                repoRoot = current;
                break;
            }

            chain.Add(current);
            var parent = _fileSystem.Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) ||
                string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;
            current = parent;
        }

        if (repoRoot is null)
            return [];

        chain.Add(repoRoot);
        chain.Reverse();

        var result = new List<(string, string)>();
        foreach (var dir in chain)
        {
            var agentsMdPath = _fileSystem.Path.Combine(dir, AgentsMdFileName);
            if (!_fileSystem.File.Exists(agentsMdPath))
                continue;

            try
            {
                var content = _fileSystem.File.ReadAllText(agentsMdPath);
                if (!string.IsNullOrWhiteSpace(content))
                    result.Add((agentsMdPath, content.Trim()));
            }
            catch
            {
                // Skip unreadable files - never crash the prompt build.
            }
        }

        return result;
    }
}
