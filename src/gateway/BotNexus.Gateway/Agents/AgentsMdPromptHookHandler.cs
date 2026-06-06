using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Hooks;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Hook handler that walks the directory tree from the agent workspace up to the
/// nearest git repository root and injects any <c>AGENTS.md</c> files found along
/// the path into the system prompt.
/// </summary>
/// <remarks>
/// <para>
/// This mirrors the convention used by Claude Code and similar agentic tooling:
/// repository maintainers place <c>AGENTS.md</c> files at the repo root and at
/// any subdirectory to provide context-aware instructions that are automatically
/// respected by agents working in that tree.
/// </para>
/// <para>
/// Files are injected root-first (most general → most specific). Already-present
/// workspace <c>AGENTS.md</c> content (loaded by <see cref="WorkspaceContextBuilder"/>
/// before hooks run) is not duplicated — this handler only injects files discovered
/// outside the workspace root via the git-tree walk.
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
    /// Priority is lower (later) than other hook handlers so workspace files
    /// are already loaded before AGENTS.md repo context is appended.
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
        var workspacePath = _workspaceManager.GetWorkspacePath(hookEvent.AgentId.Value);
        var repoFiles = CollectAgentsMdFiles(workspacePath);

        if (repoFiles.Count == 0)
            return Task.FromResult<BeforePromptBuildResult?>(null);

        var injected = new System.Text.StringBuilder();
        foreach (var (filePath, content) in repoFiles)
        {
            injected.AppendLine($"<!-- AGENTS.md: {filePath} -->");
            injected.AppendLine(content);
            injected.AppendLine();
        }

        return Task.FromResult<BeforePromptBuildResult?>(new BeforePromptBuildResult
        {
            AppendSystemContext = injected.ToString().TrimEnd()
        });
    }

    /// <summary>
    /// Walks from <paramref name="startPath"/> upward through parent directories to the
    /// nearest git repository root (directory containing a <c>.git</c> entry), collecting
    /// <c>AGENTS.md</c> files ordered from root to the starting directory.
    /// </summary>
    /// <param name="startPath">Directory to begin the walk from.</param>
    /// <returns>
    /// List of (absolutePath, content) tuples in root-first order.
    /// Empty when no repo root or no AGENTS.md files are found.
    /// </returns>
    internal List<(string Path, string Content)> CollectAgentsMdFiles(string startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath))
            return [];

        // Collect the chain from start → root until we find a .git directory.
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
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;

            current = parent;
        }

        if (repoRoot is null)
            return []; // Not inside a git repo.

        // Include the repo root itself in the chain, then reverse for root-first order.
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
                // Skip unreadable files — never crash the prompt build.
            }
        }

        return result;
    }
}
