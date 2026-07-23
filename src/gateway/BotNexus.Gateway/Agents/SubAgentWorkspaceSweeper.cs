using System.IO.Abstractions;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// The outcome of a single sub-agent workspace sweep pass.
/// </summary>
/// <param name="Removed">Number of sub-agent workspace directories deleted.</param>
/// <param name="BytesReclaimed">Total bytes freed by the deleted directories (best-effort).</param>
/// <param name="SkippedRecent">Directories skipped because they were modified within the grace window or not yet expired.</param>
public readonly record struct SubAgentWorkspaceSweepResult(int Removed, long BytesReclaimed, int SkippedRecent);

/// <summary>
/// Pure, filesystem-abstracted engine that performs the age-based sweep of completed sub-agent
/// workspace directories under the resolved persistent agents root (issue #2237).
/// <para>
/// It is deliberately conservative:
/// <list type="bullet">
///   <item>Only directories whose name contains the <c>--subagent--</c> marker are ever considered,
///   so top-level registered agent workspaces (the domain of #2039) are never touched.</item>
///   <item>Directories modified within the grace window are always skipped, so a live / in-flight
///   worker is never yanked.</item>
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

    /// <summary>Creates a sweeper over the given filesystem abstraction.</summary>
    public SubAgentWorkspaceSweeper(IFileSystem fileSystem, ILogger logger)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

            // Never yank a live / in-flight worker: anything touched within the grace window is safe.
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
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(
                    ex,
                    "Sub-agent workspace sweep could not delete {Directory}; it may be held by a live worker and will be retried next pass.",
                    fullPath);
            }
        }

        return new SubAgentWorkspaceSweepResult(removed, bytesReclaimed, skippedRecent);
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
