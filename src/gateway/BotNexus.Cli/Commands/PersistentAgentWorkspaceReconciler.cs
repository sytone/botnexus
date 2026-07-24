using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Cli.Commands;

/// <summary>
/// A single physical directory found directly under the configured agents root, classified against
/// the set of registered agents. <paramref name="IsOrphaned"/> is true when no enabled config agent
/// claims the directory name; <paramref name="IsUnsafeLink"/> is true when the directory is a
/// symlink/reparse point and must never be followed for deletion.
/// </summary>
internal sealed record PersistentAgentWorkspaceEntry(
    string DirectoryName,
    string FullPath,
    bool IsOrphaned,
    bool IsUnsafeLink);

/// <summary>
/// Reconciles persistent top-level agent workspaces with enabled config-defined agents while
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
    /// as registered or orphaned. Registration is derived the same way the gateway interprets config:
    /// the <c>defaults</c> reserved key and disabled agents are ignored, and remaining keys are
    /// canonicalized through <see cref="AgentId"/> rather than a doctor-specific interpretation.
    /// Returns an empty list when the root does not exist.
    /// </summary>
    public IReadOnlyList<PersistentAgentWorkspaceEntry> BuildPlan(string agentsRoot, PlatformConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentsRoot);
        ArgumentNullException.ThrowIfNull(config);
        var root = Path.GetFullPath(agentsRoot);
        if (!Directory.Exists(root))
            return [];

        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in config.Agents ?? [])
        {
            if (pair.Key.Equals("defaults", StringComparison.OrdinalIgnoreCase) || !pair.Value.Enabled)
                continue;

            // Canonicalize through AgentId so the doctor uses the gateway's own ID rules; skip any key
            // AgentId rejects rather than inventing a second, laxer interpretation of registration.
            var maybeId = AgentId.TryFrom(pair.Key, out var id) ? id.Value : null;
            if (maybeId is not null)
                registered.Add(maybeId);
        }

        return Directory.EnumerateDirectories(root)
            .Select(path =>
            {
                var info = new DirectoryInfo(path);
                return new PersistentAgentWorkspaceEntry(
                    info.Name,
                    info.FullName,
                    !registered.Contains(info.Name.Trim()),
                    (info.Attributes & FileAttributes.ReparsePoint) != 0);
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
    public int DeleteOrphans(string agentsRoot, IReadOnlyList<PersistentAgentWorkspaceEntry> plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentsRoot);
        ArgumentNullException.ThrowIfNull(plan);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(agentsRoot));
        var candidates = plan
            .Where(item => item.IsOrphaned)
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
