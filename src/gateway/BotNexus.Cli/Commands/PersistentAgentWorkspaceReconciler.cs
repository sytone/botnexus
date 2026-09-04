using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Cli.Commands;

/// <summary>
/// A single physical directory found directly under the configured agents root, classified against
/// the set of declared agents. <paramref name="IsOrphaned"/> is true when no agent declared in
/// config.json claims the directory name - a *disabled* agent is still declared and is never
/// orphaned (issue #3700); <paramref name="IsUnsafeLink"/> is true when the directory is a
/// symlink/reparse point and must never be followed for deletion.
/// <para>
/// #3845: <paramref name="SizeBytes"/> and <paramref name="NewestContentUtc"/> exist so the report
/// answers the only two questions an operator actually has before approving an irreversible delete -
/// how much disk this is holding, and how recently anything wrote to it. A bare list of names forces
/// them to shell out and measure by hand, which is how 42 orphans accumulated unexamined.
/// </para>
/// </summary>
/// <param name="SizeBytes">Total bytes of files in the tree, best-effort; 0 when unreadable.</param>
/// <param name="NewestContentUtc">Newest file last-write time in the tree, or null when empty/unreadable.</param>
internal sealed record PersistentAgentWorkspaceEntry(
    string DirectoryName,
    string FullPath,
    bool IsOrphaned,
    bool IsUnsafeLink,
    long SizeBytes = 0,
    DateTime? NewestContentUtc = null);

/// <summary>
/// Reconciles persistent top-level agent workspaces with the agents declared in config while
/// keeping every deletion constrained to the configured agents root. This is the destructive
/// counterpart to the read-only <c>PersistentAgentFolderCheck</c>: it produces a reviewable plan and
/// deletes only orphaned directories that pass strict containment and reparse-point safety checks.
/// </summary>
internal sealed class PersistentAgentWorkspaceReconciler
{
    /// <summary>
    /// Resolves the effective agents root from the BotNexus home and the optional
    /// <c>gateway.agentsDirectory</c> override, falling back to <c>&lt;home&gt;/agents</c>. A relative
    /// configured directory is resolved against the home so <c>--target</c> is honored consistently.
    /// </summary>
    public static string ResolveAgentsRoot(string botNexusHome, string? configuredDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botNexusHome);
        var home = Path.GetFullPath(botNexusHome);
        if (string.IsNullOrWhiteSpace(configuredDirectory))
            return Path.Combine(home, "agents");

        return Path.GetFullPath(Path.IsPathRooted(configuredDirectory)
            ? configuredDirectory
            : Path.Combine(home, configuredDirectory));
    }

    /// <summary>
    /// Enumerates the immediate directories under <paramref name="agentsRoot"/> and classifies each
    /// as declared or orphaned. Only the reserved <c>defaults</c> key is ignored: a disabled agent is
    /// a declared agent whose workspace must survive cleanup, because disabling is a reversible opt-out
    /// that preserves state (issue #3700). Keys are canonicalized through <see cref="AgentId"/> rather
    /// than a doctor-specific interpretation. Returns an empty list when the root does not exist.
    /// </summary>
    public IReadOnlyList<PersistentAgentWorkspaceEntry> BuildPlan(string agentsRoot, PlatformConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentsRoot);
        ArgumentNullException.ThrowIfNull(config);
        var root = Path.GetFullPath(agentsRoot);
        if (!Directory.Exists(root))
            return [];

        var declared = DeclaredIds(config);

        return Directory.EnumerateDirectories(root)
            .Select(path =>
            {
                var info = new DirectoryInfo(path);
                var isLink = (info.Attributes & FileAttributes.ReparsePoint) != 0;
                // Never walk through a reparse point to measure it: the tree on the other side is
                // not this directory's disk usage, and following it is exactly the escape the
                // deletion guards refuse.
                var (size, newest) = isLink ? (0L, (DateTime?)null) : Measure(info);
                return new PersistentAgentWorkspaceEntry(
                    info.Name,
                    info.FullName,
                    !declared.Contains(info.Name.Trim()),
                    isLink,
                    size,
                    newest);
            })
            .OrderBy(entry => entry.DirectoryName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Deletes every orphaned directory in <paramref name="plan"/> that is a direct, non-reparse-point
    /// child of the resolved agents root. The whole batch is validated before any deletion so a later
    /// unsafe entry cannot leave the workspace set half-reconciled. Throws
    /// <see cref="InvalidOperationException"/> if any candidate escapes the root or contains a reparse
    /// point. Returns the number of directories deleted.
    /// </summary>
    /// <param name="agentsRoot">The resolved agents root every candidate must be a direct child of.</param>
    /// <param name="plan">The classified plan produced by <see cref="BuildPlan"/>.</param>
    /// <param name="config">
    /// #3845: when supplied, registration is re-derived from config here rather than trusting the
    /// caller-supplied <see cref="PersistentAgentWorkspaceEntry.IsOrphaned"/> flag. The flag travels
    /// through a print/prompt/approve round trip during which config can be edited, and a plan can be
    /// hand-built by a caller that classified it differently. Deleting a live agent's memory store is
    /// irreversible, so the last word before deletion belongs to the registry, not to a stale bool.
    /// </param>
    public int DeleteOrphans(
        string agentsRoot,
        IReadOnlyList<PersistentAgentWorkspaceEntry> plan,
        PlatformConfig? config = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentsRoot);
        ArgumentNullException.ThrowIfNull(plan);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(agentsRoot));
        var declared = config is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : DeclaredIds(config);
        var candidates = plan
            .Where(item => item.IsOrphaned)
            .Select(entry => declared.Contains(entry.DirectoryName.Trim())
                ? throw new InvalidOperationException(
                    $"Refusing to delete '{entry.DirectoryName}': it is a declared agent, not an orphan.")
                : entry)
            .Select(entry => ValidateDeletionCandidate(root, entry))
            .Where(Directory.Exists)
            .ToArray();

        // Validate the complete batch before deleting anything. A later unsafe item must not
        // leave the user's workspace set half-reconciled.
        foreach (var candidate in candidates)
            EnsureTreeContainsNoReparsePoints(candidate);

        foreach (var candidate in candidates)
            Directory.Delete(candidate, recursive: true);

        return candidates.Length;
    }

    /// <summary>
    /// The canonical set of declared agent ids for <paramref name="config"/>. Only the
    /// <c>defaults</c> reserved key is ignored; disabled agents remain declared because disabling is
    /// reversible and must not authorize deletion of their persistent state (issue #3700). Remaining
    /// keys are canonicalized through <see cref="AgentId"/> rather than a doctor-specific
    /// interpretation. Shared by classification and by the pre-deletion refusal so the two can never
    /// disagree about what "declared" means.
    /// </summary>
    private static HashSet<string> DeclaredIds(PlatformConfig config)
    {
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in config.Agents ?? [])
        {
            if (pair.Key.Equals("defaults", StringComparison.OrdinalIgnoreCase))
                continue;

            var maybeId = AgentId.TryFrom(pair.Key, out var id) ? id.Value : null;
            if (maybeId is not null)
                declared.Add(maybeId);
        }

        return declared;
    }

    /// <summary>
    /// Best-effort total byte count and newest last-write time across the tree. Failures are absorbed
    /// because this feeds a report: an unreadable subtree must degrade the numbers, never abort the
    /// enumeration an operator is relying on to see the orphans at all.
    /// </summary>
    private static (long SizeBytes, DateTime? NewestContentUtc) Measure(DirectoryInfo directory)
    {
        long size = 0;
        DateTime? newest = null;
        try
        {
            foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;
                    size += file.Length;
                    var written = file.LastWriteTimeUtc;
                    if (newest is null || written > newest)
                        newest = written;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Individual file vanished or is locked; keep measuring the rest.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable subtree: report what was counted so far.
        }

        return (size, newest);
    }

    private static string ValidateDeletionCandidate(string root, PersistentAgentWorkspaceEntry entry)
    {
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.FullPath));
        var parent = Directory.GetParent(candidate)?.FullName;
        if (entry.IsUnsafeLink
            || parent is null
            || !parent.Equals(root, PathComparison)
            || !Path.GetFileName(candidate).Equals(entry.DirectoryName, PathComparison))
        {
            throw new InvalidOperationException($"Refusing to delete unsafe agent workspace '{entry.FullPath}'.");
        }

        return candidate;
    }

    private static void EnsureTreeContainsNoReparsePoints(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            var attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Refusing to delete agent workspace containing reparse point '{directory}'.");

            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException($"Refusing to delete agent workspace containing reparse point '{child}'.");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(child);
            }
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
