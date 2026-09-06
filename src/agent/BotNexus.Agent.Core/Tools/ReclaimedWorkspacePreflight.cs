namespace BotNexus.Agent.Core.Tools;

/// <summary>
/// Describes an unavailable sub-agent working directory without inferring its history from absence
/// (issues #3569 AC5 and #3928).
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect.</b> When a sub-agent's workspace was reclaimed mid-run, every subsequent
/// filesystem-touching tool call failed with an OS-level message that named only a path:
/// <c>"An error occurred trying to start process 'pwsh' with working directory '...'. The directory
/// name is invalid."</c> from <c>shell</c>/<c>exec</c>, and <c>"Base directory '...' does not
/// exist."</c> from <c>glob</c>. 66 such failures across 37 distinct sub-agents in one 7-day window.
/// </para>
/// <para>
/// <b>Why the message matters as much as the reclamation fix.</b> Those messages read as caller
/// mistakes, so the affected sub-agent concluded it had passed a bad path and retried - variant
/// after variant - until its turn budget was gone, then reported a confident-sounding but wrong
/// completion to its parent. An agent cannot recover from a condition it cannot name. Telling it
/// plainly that the working directory is absent avoids repeated cwd-dependent failures. Absence
/// alone does not prove that the platform removed it; provisioning may never have occurred.
/// </para>
/// <para>
/// <b>Scope is deliberately narrow.</b> Only paths carrying the <c>--subagent--</c> marker are
/// diagnosed. A missing top-level registered agent workspace is a genuine configuration fault and
/// keeps its ordinary error - claiming "reclaimed mid-run" there would be a confident false
/// diagnosis, which is strictly worse than the generic message it replaced.
/// </para>
/// <para>
/// <b>Dependency posture.</b> The existence probe is an injected delegate rather than an
/// <c>IFileSystem</c>, matching <see cref="SkillScriptPreflight"/>: this type is consumed by
/// <c>BotNexus.Extensions.ExecTool</c>, which loads into an isolated <c>AssemblyLoadContext</c> and
/// must ship its whole managed closure (issue #2184).
/// </para>
/// </remarks>
public static class ReclaimedWorkspacePreflight
{
    /// <summary>
    /// Marker embedded in every sub-agent's child agent id, and therefore in its workspace path.
    /// Kept in lock-step with <c>FileAgentWorkspaceManager.SubAgentMarker</c>.
    /// </summary>
    public const string SubAgentMarker = "--subagent--";

    /// <summary>
    /// Produces a factual missing-workspace diagnostic for <paramref name="workingDirectory"/>, or
    /// <c>null</c> when the probe does not establish an absent sub-agent directory.
    /// </summary>
    /// <param name="workingDirectory">The working directory the tool was about to use.</param>
    /// <param name="directoryExists">Existence probe for a directory path.</param>
    /// <returns>The diagnostic message, or <c>null</c> when no diagnosis applies.</returns>
    /// <remarks>
    /// Deliberately <c>internal</c>: the supported public API is <see cref="ThrowIfReclaimed(string?)"/>.
    /// Exposing a <c>public static string(string)</c> helper here would also violate the #2925
    /// string-transformation fence, whose baseline is shrink-only and must not gain an entry.
    /// </remarks>
    internal static string? Describe(string? workingDirectory, Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(directoryExists);

        if (string.IsNullOrWhiteSpace(workingDirectory))
            return null;

        // Narrow scope: only sub-agent workspaces are subject to reclamation.
        if (!workingDirectory.Contains(SubAgentMarker, StringComparison.OrdinalIgnoreCase))
            return null;

        bool exists;
        try
        {
            exists = directoryExists(workingDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The diagnostic path must never itself become the failure. Stay silent and let the
            // tool's own error surface unchanged.
            return null;
        }

        if (exists)
            return null;

        var subAgentId = ExtractSubAgentId(workingDirectory);

        return $"The working directory for sub-agent '{subAgentId}' does not exist on disk:{Environment.NewLine}"
            + $"  {workingDirectory}{Environment.NewLine}"
            + "This check cannot determine whether the workspace was never provisioned, was reclaimed, "
            + "or became unavailable for another reason (issues #3928 and #3569). It does not establish "
            + "prior creation, use, or deletion. Stop operations that require this working directory "
            + "and report the unavailable workspace to your parent. Retrying with the same missing cwd "
            + "will not help; this finding does not establish that separately granted paths are unavailable.";
    }

    /// <summary>
    /// Enforcement seam used by the shell / exec / glob tools: raises the diagnostic from
    /// <see cref="Describe"/> as an exception so it reaches the agent as the tool's error text.
    /// Silent when the workspace is intact or the situation is outside the sub-agent path scope.
    /// </summary>
    /// <param name="workingDirectory">The working directory the tool was about to use.</param>
    /// <param name="directoryExists">Existence probe for a directory path.</param>
    /// <exception cref="DirectoryNotFoundException">The sub-agent's working directory is absent; its history is unknown.</exception>
    public static void ThrowIfReclaimed(string? workingDirectory, Func<string, bool> directoryExists)
    {
        var message = Describe(workingDirectory, directoryExists);
        if (message is not null)
            throw new DirectoryNotFoundException(message);
    }

    /// <summary>
    /// Filesystem-backed overload for callers that have no reason to inject a probe.
    /// </summary>
    /// <param name="workingDirectory">The working directory the tool was about to use.</param>
    public static void ThrowIfReclaimed(string? workingDirectory)
        => ThrowIfReclaimed(workingDirectory, Directory.Exists);

    /// <summary>
    /// Recovers the child agent id from a workspace path by taking the last path segment that
    /// carries the sub-agent marker. Handles both the <c>&lt;id&gt;/workspace</c> form the tools use
    /// and the bare <c>&lt;id&gt;</c> directory form.
    /// </summary>
    private static string ExtractSubAgentId(string workingDirectory)
    {
        var segments = workingDirectory.Split(
            ['/', '\\'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var i = segments.Length - 1; i >= 0; i--)
        {
            if (segments[i].Contains(SubAgentMarker, StringComparison.OrdinalIgnoreCase))
                return segments[i];
        }

        // Unreachable in practice - Describe only calls this once the marker has been matched -
        // but returning the whole path is strictly better than throwing from a diagnostic.
        return workingDirectory;
    }
}
