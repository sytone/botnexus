namespace BotNexus.Agent.Core.Tools;

/// <summary>
/// Turns a vanished sub-agent working directory into a self-describing diagnostic instead of a raw
/// OS path error (issue #3569 AC5).
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
/// plainly that the platform removed its workspace and that retrying cannot help converts an entire
/// wasted run into one honest early failure.
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
    /// Produces the workspace-reclaimed diagnostic for <paramref name="workingDirectory"/>, or
    /// <c>null</c> when the situation is not a reclaimed sub-agent workspace.
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

        return $"Your workspace was reclaimed while this run was still in progress. The working "
            + $"directory for sub-agent '{subAgentId}' no longer exists on disk:{Environment.NewLine}"
            + $"  {workingDirectory}{Environment.NewLine}"
            + "This is a platform-side reclamation (issue #3569), not a mistake in the arguments you "
            + "passed - the directory was created and used successfully earlier in this run. No path "
            + "you supply can succeed, so retrying this or any other file operation will fail the "
            + "same way. Stop file work now and report this condition to your parent so the run is "
            + "not recorded as a normal completion.";
    }

    /// <summary>
    /// Enforcement seam used by the shell / exec / glob tools: raises the diagnostic from
    /// <see cref="Describe"/> as an exception so it reaches the agent as the tool's error text.
    /// Silent when the workspace is intact or the situation is not a reclaimed sub-agent workspace.
    /// </summary>
    /// <param name="workingDirectory">The working directory the tool was about to use.</param>
    /// <param name="directoryExists">Existence probe for a directory path.</param>
    /// <exception cref="DirectoryNotFoundException">The sub-agent's workspace was reclaimed.</exception>
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
