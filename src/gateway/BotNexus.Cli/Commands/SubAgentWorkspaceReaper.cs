using System.IO.Abstractions;

namespace BotNexus.Cli.Commands;

/// <summary>
/// Classifies how a physical sub-agent workspace directory relates to the persisted
/// sub-agent record that (may have) created it.
/// </summary>
internal enum SubAgentWorkspaceDisposition
{
    /// <summary>
    /// A <c>sub_agent_sessions</c> row exists for this directory and its status is a terminal
    /// state (completed/failed/killed/timed-out). The sub-agent is guaranteed not to be running,
    /// so the bulky workspace files can be reclaimed. Eligible for pruning.
    /// </summary>
    Terminal,

    /// <summary>
    /// A <c>sub_agent_sessions</c> row exists and its status is still active/running. The
    /// gateway may hold open handles under this directory - it must never be pruned.
    /// </summary>
    Running,

    /// <summary>
    /// No <c>sub_agent_sessions</c> row references this directory. The owning record has already
    /// been evicted from the store (or was written by a gateway generation that is no longer
    /// live), so the sub-agent cannot be running. Eligible for pruning.
    /// </summary>
    Orphan
}

/// <summary>
/// One physical sub-agent workspace directory paired with its computed disposition.
/// </summary>
/// <param name="AgentDirectoryName">The sanitized child-agent directory name under the workspace root.</param>
/// <param name="FullPath">The absolute path of the workspace directory that would be deleted.</param>
/// <param name="Disposition">Whether the directory is terminal, running, or an orphan.</param>
/// <param name="Status">The persisted status string that produced the disposition, or <c>null</c> for an orphan.</param>
internal sealed record SubAgentWorkspaceEntry(
    string AgentDirectoryName,
    string FullPath,
    SubAgentWorkspaceDisposition Disposition,
    string? Status)
{
    /// <summary>
    /// True when this directory is safe to reclaim: terminal or orphaned sub-agents only.
    /// A still-running sub-agent's workspace is never eligible.
    /// </summary>
    public bool IsPrunable => Disposition is SubAgentWorkspaceDisposition.Terminal or SubAgentWorkspaceDisposition.Orphan;
}

/// <summary>
/// Pure, filesystem-abstracted engine that reconciles the physical sub-agent workspace directories
/// on disk with the persisted sub-agent status records, and reclaims only the directories whose
/// sub-agent is in a terminal state (or whose record no longer exists). This is the safe seam for
/// issue #1942: a long-lived gateway accumulates sub-agent workspaces under the temp root and never
/// reclaims them, so this on-demand reaper lets an operator list and prune the dead ones without
/// ever touching a running sub-agent's live files.
/// </summary>
internal sealed class SubAgentWorkspaceReaper
{
    private static readonly HashSet<string> TerminalStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Completed",
        "Failed",
        "Killed",
        "TimedOut"
    };

    private readonly IFileSystem _fileSystem;

    public SubAgentWorkspaceReaper(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    /// <summary>
    /// Sanitizes a raw child-agent id into the on-disk directory segment exactly the way
    /// <c>FileAgentWorkspaceManager.GetWorkspacePath</c> does, so persisted <c>child_agent_id</c>
    /// values can be matched against the directory names found on disk. Kept in lock-step with the
    /// gateway's sanitization - any divergence would silently prevent a terminal record from
    /// matching its workspace and leave the directory unreaped.
    /// </summary>
    public static string SanitizeAgentDirectoryName(string childAgentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childAgentId);
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = childAgentId.Trim();
        foreach (var ch in invalid)
            sanitized = sanitized.Replace(ch, '_');
        return sanitized;
    }

    /// <summary>
    /// Enumerates the workspace directories physically present under <paramref name="workspaceRoot"/>
    /// and classifies each against <paramref name="statusesByAgentDirectory"/> (a map of sanitized
    /// child-agent directory name to persisted status). A non-existent root yields an empty plan -
    /// there is nothing to reap and that is not an error. The result is ordered by directory name for
    /// stable, testable output.
    /// </summary>
    /// <param name="workspaceRoot">The temp sub-agent workspace root (e.g. <c>%TEMP%/botnexus-subagent-workspaces</c>).</param>
    /// <param name="statusesByAgentDirectory">Sanitized child-agent directory name -> persisted status string.</param>
    /// <returns>One entry per directory found on disk, each tagged with its disposition.</returns>
    public IReadOnlyList<SubAgentWorkspaceEntry> BuildPlan(
        string workspaceRoot,
        IReadOnlyDictionary<string, string> statusesByAgentDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(statusesByAgentDirectory);

        var fullRoot = _fileSystem.Path.GetFullPath(workspaceRoot);
        if (!_fileSystem.Directory.Exists(fullRoot))
            return [];

        var entries = new List<SubAgentWorkspaceEntry>();
        foreach (var directory in _fileSystem.Directory.EnumerateDirectories(fullRoot))
        {
            var name = _fileSystem.Path.GetFileName(
                directory.TrimEnd(_fileSystem.Path.DirectorySeparatorChar, _fileSystem.Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (!statusesByAgentDirectory.TryGetValue(name, out var status))
            {
                entries.Add(new SubAgentWorkspaceEntry(name, directory, SubAgentWorkspaceDisposition.Orphan, Status: null));
                continue;
            }

            var disposition = TerminalStatuses.Contains(status)
                ? SubAgentWorkspaceDisposition.Terminal
                : SubAgentWorkspaceDisposition.Running;
            entries.Add(new SubAgentWorkspaceEntry(name, directory, disposition, status));
        }

        return entries
            .OrderBy(entry => entry.AgentDirectoryName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Deletes the workspace directories the plan marks as prunable (terminal or orphaned). When
    /// <paramref name="dryRun"/> is true nothing is deleted - the count of directories that
    /// <i>would</i> be removed is returned instead, so callers can preview the effect. Running
    /// sub-agents are never touched. Returns the number of directories actually deleted (or that
    /// would be deleted under a dry run).
    /// </summary>
    /// <param name="plan">The plan produced by <see cref="BuildPlan"/>.</param>
    /// <param name="dryRun">When true, report but do not delete.</param>
    /// <returns>Count of prunable directories deleted (or previewed under a dry run).</returns>
    public int Prune(IReadOnlyList<SubAgentWorkspaceEntry> plan, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var count = 0;
        foreach (var entry in plan)
        {
            if (!entry.IsPrunable)
                continue;

            count++;
            if (dryRun)
                continue;

            if (_fileSystem.Directory.Exists(entry.FullPath))
                _fileSystem.Directory.Delete(entry.FullPath, recursive: true);
        }

        return count;
    }
}
