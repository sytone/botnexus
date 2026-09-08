using System.IO.Abstractions;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// The outcome of a single sub-agent workspace sweep pass.
/// </summary>
/// <param name="Removed">Number of sub-agent workspace directories deleted.</param>
/// <param name="BytesReclaimed">Total bytes freed by the deleted directories (best-effort).</param>
/// <param name="SkippedRecent">Directories skipped because they were modified within the grace window or not yet expired.</param>
/// <param name="SkippedLive">
/// Directories that were age-eligible for removal but retained because their sub-agent is still
/// running (or its liveness could not be established). This counter is deliberately separate from
/// <paramref name="SkippedRecent"/>: a non-zero value here is the operator-visible evidence that
/// the age heuristic alone would have deleted a live run's workspace (issue #3569).
/// </param>
public readonly record struct SubAgentWorkspaceSweepResult(
    int Removed,
    long BytesReclaimed,
    int SkippedRecent,
    int SkippedLive = 0);

/// <summary>
/// Pure, filesystem-abstracted engine that performs the age-based sweep of completed sub-agent
/// workspace directories under the resolved persistent agents root (issue #2237).
/// <para>
/// It is deliberately conservative:
/// <list type="bullet">
///   <item>Only directories whose name contains the <c>--subagent--</c> marker are ever considered,
///   so top-level registered agent workspaces (the domain of #2039) are never touched.</item>
///   <item>Directories modified within the grace window are always skipped.</item>
///   <item>Age-eligible directories are removed only after an explicit liveness check against the
///   agent registry. Elapsed time is NOT a liveness signal: a live sub-agent waiting on a provider
///   writes nothing to its workspace, so its directory ages past the TTL while the run is healthy.
///   That is exactly how the pre-#3569 sweep deleted the working directory out from under 37 live
///   sub-agents in one week. Anything not positively known to be dead is retained.</item>
///   <item>Deletion is confined to the resolved agents root and reparse points (symlinks / junctions)
///   are never followed or deleted through, so a sweep can never escape the agents root.</item>
/// </list>
/// </para>
/// </summary>
public sealed class SubAgentWorkspaceSweeper
{
    internal const string SubAgentMarker = "--subagent--";

    private readonly IFileSystem _fileSystem;
    private readonly ILogger _logger;
    private readonly ISubAgentWorkspaceLivenessProbe _livenessProbe;

    /// <summary>
    /// Creates a sweeper over the given filesystem abstraction.
    /// </summary>
    /// <param name="fileSystem">Filesystem abstraction the sweep reads and deletes through.</param>
    /// <param name="logger">Logger receiving the per-removal audit line.</param>
    /// <param name="livenessProbe">
    /// Authority consulted before any deletion. Required, and deliberately so: an optional probe
    /// would let a misconfigured DI graph silently fall back to the time-only deletion that caused
    /// #3569, and that failure would be invisible until it destroyed another live run.
    /// </param>
    public SubAgentWorkspaceSweeper(
        IFileSystem fileSystem,
        ILogger logger,
        ISubAgentWorkspaceLivenessProbe livenessProbe)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _livenessProbe = livenessProbe ?? throw new ArgumentNullException(nameof(livenessProbe));
    }

    /// <summary>
    /// Runs one sweep pass over <paramref name="agentsRoot"/>, removing <c>*--subagent--*</c>
    /// directories whose last-write time exceeds <paramref name="retention"/> while never touching
    /// directories modified within <paramref name="grace"/>. A non-existent root is a no-op.
    /// </summary>
    /// <param name="agentsRoot">The resolved persistent agents root to scan.</param>
    /// <param name="retention">Idle TTL after which a directory is eligible for removal. Non-positive disables removal.</param>
    /// <param name="grace">Safety window; directories modified within it are always skipped.</param>
    /// <param name="nowUtc">The reference "now" (UTC) used for age comparisons.</param>
    /// <returns>Counts of removed / bytes reclaimed / skipped-recent directories.</returns>
    public SubAgentWorkspaceSweepResult Sweep(string agentsRoot, TimeSpan retention, TimeSpan grace, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentsRoot);

        if (retention <= TimeSpan.Zero)
            return default;

        var fullRoot = _fileSystem.Path.GetFullPath(agentsRoot);
        if (!_fileSystem.Directory.Exists(fullRoot))
            return default;

        var removed = 0;
        long bytesReclaimed = 0;
        var skippedRecent = 0;
        var skippedLive = 0;

        foreach (var directory in _fileSystem.Directory.EnumerateDirectories(fullRoot))
        {
            var name = _fileSystem.Path.GetFileName(
                directory.TrimEnd(_fileSystem.Path.DirectorySeparatorChar, _fileSystem.Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name))
                continue;

            // Scope strictly to sub-agent husks. Top-level registered agents (no marker) are #2039's
            // domain and must never be affected by this sweep.
            if (!name.Contains(SubAgentMarker, StringComparison.OrdinalIgnoreCase))
                continue;

            var directoryInfo = _fileSystem.DirectoryInfo.New(directory);

            // Defensive: never follow or delete through a reparse point (symlink / junction). Deleting
            // recursively through one could escape the agents root. Skip it entirely.
            if (directoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;

            var fullPath = _fileSystem.Path.GetFullPath(directory);

            // Path-safety: the resolved target must remain strictly within the agents root.
            if (!IsStrictlyWithinRoot(fullRoot, fullPath))
                continue;

            var lastWrite = directoryInfo.LastWriteTimeUtc;
            var age = nowUtc - lastWrite;

            // Cheap age filters first, so a recently-touched directory never even reaches the probe.
            if (age < grace)
            {
                skippedRecent++;
                continue;
            }

            if (age < retention)
            {
                skippedRecent++;
                continue;
            }

            // Age-eligible is NOT the same as dead (#3569). Ask the authority, and treat a probe
            // failure as "live". The costs are asymmetric: retaining a dead workspace wastes disk
            // for one interval; deleting a live one destroys the whole run and returns a wrong
            // summary to the parent.
            if (IsLive(name))
            {
                skippedLive++;
                continue;
            }

            long size = 0;
            try
            {
                size = ComputeSize(directoryInfo);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Size is best-effort; proceed with deletion regardless.
            }

            try
            {
                directoryInfo.Delete(recursive: true);
                removed++;
                bytesReclaimed += size;

                // AC4: log EACH removal, not just an aggregate count. The original defect left no
                // trace naming what removed a live sub-agent's workspace, which is why the cause
                // stayed unconfirmed across 66 failures.
                //
                // #3670: the wording now comes from the shared reclamation vocabulary, so this
                // backstop line and the lifecycle line emitted at terminal transition share one
                // greppable prefix and differ only in their route/reason suffix.
                _logger.LogInformation(
                    SubAgentWorkspaceReclamationAudit.BackstopTemplate,
                    name,
                    size,
                    age.TotalHours,
                    retention.TotalHours);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(
                    ex,
                    "Sub-agent workspace sweep could not delete {Directory}; it may be held by a live worker and will be retried next pass.",
                    fullPath);
            }
        }

        return new SubAgentWorkspaceSweepResult(removed, bytesReclaimed, skippedRecent, skippedLive);
    }

    /// <summary>
    /// Whether the sub-agent owning <paramref name="directoryName"/> must be presumed live. Returns
    /// <c>true</c> when the probe throws, so uncertainty always retains.
    /// </summary>
    private bool IsLive(string directoryName)
    {
        try
        {
            return _livenessProbe.IsLive(directoryName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Sub-agent workspace liveness probe failed for '{Directory}'; retaining it rather than risking deletion of a live run's workspace.",
                directoryName);
            return true;
        }
    }

    private bool IsStrictlyWithinRoot(string root, string path)
    {
        var prefix = root.TrimEnd(_fileSystem.Path.DirectorySeparatorChar, _fileSystem.Path.AltDirectorySeparatorChar)
            + _fileSystem.Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && !path.Equals(root, StringComparison.OrdinalIgnoreCase);
    }

    private long ComputeSize(IDirectoryInfo directoryInfo)
    {
        long total = 0;
        foreach (var file in directoryInfo.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            // Do not count bytes reached through a reparse point.
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;
            total += file.Length;
        }

        return total;
    }
}
