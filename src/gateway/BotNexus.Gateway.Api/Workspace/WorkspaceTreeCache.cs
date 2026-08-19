using System.Collections.Concurrent;
using System.IO.Abstractions;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Api.Models;

namespace BotNexus.Gateway.Api.Workspace;

/// <summary>
/// A single filesystem observation captured while the workspace tree was walked.
/// The stamp set is the cache validator: re-stat'ing these entries is materially cheaper
/// than rebuilding the tree (no symlink resolution, no path validation, no DTO allocation,
/// no directory enumeration), yet it detects every change that could alter the response.
/// </summary>
/// <param name="Path">Absolute path of the observed entry.</param>
/// <param name="IsDirectory">True when the entry is a directory.</param>
/// <param name="Length">File length in bytes; zero for directories.</param>
/// <param name="LastWriteUtc">Last write timestamp observed at walk time.</param>
public readonly record struct WorkspaceTreeStamp(string Path, bool IsDirectory, long Length, DateTime LastWriteUtc);

/// <summary>
/// Caches depth-limited workspace trees so a repeated poll against an unchanged workspace
/// does not repeat the full walk (issue #3357: p95 3.5s, ~2600 entries per call).
/// </summary>
/// <remarks>
/// Correctness over cheapness: this is NOT a TTL cache. Every hit is revalidated against the
/// filesystem via the stamp set captured during the walk, so an added, removed or modified
/// entry inside the depth limit is reflected in the very next response. Directory stamps catch
/// additions, removals and renames (they bump the parent directory mtime); file stamps catch
/// in-place modification, which does not.
/// The cache stores only already-validated output: entries excluded by
/// <c>DefaultPathValidator</c> never enter it, so the security posture is unchanged.
/// </remarks>
public sealed class WorkspaceTreeCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly bool _lookupEnabled;
    private readonly bool _invalidationEnabled;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkspaceTreeCache"/> class.
    /// </summary>
    /// <param name="lookupEnabled">
    /// When false the cache never serves a hit. Test-only seam used to prove the caching
    /// assertion is non-vacuous (issue #3357 AC5); production always leaves this true.
    /// </param>
    /// <param name="invalidationEnabled">
    /// When false a hit is served without revalidating its stamps. Test-only seam used to prove
    /// the freshness assertion is non-vacuous; production always leaves this true.
    /// </param>
    public WorkspaceTreeCache(bool lookupEnabled = true, bool invalidationEnabled = true)
    {
        _lookupEnabled = lookupEnabled;
        _invalidationEnabled = invalidationEnabled;
    }

    /// <summary>
    /// Attempts to serve a still-valid cached tree for an agent at a specific depth.
    /// </summary>
    /// <param name="fileSystem">Filesystem used to revalidate the stamp set.</param>
    /// <param name="agentId">Agent identifier the tree belongs to.</param>
    /// <param name="depth">Requested depth limit; part of the cache key.</param>
    /// <param name="workspaceRoot">Resolved workspace root; part of the cache key.</param>
    /// <returns>The cached entries when present and still fresh; otherwise <c>null</c>.</returns>
    public IReadOnlyList<WorkspaceEntryDto>? TryGet(IFileSystem fileSystem, AgentId agentId, int depth, string workspaceRoot)
    {
        if (!_lookupEnabled)
            return null;

        if (!_entries.TryGetValue(BuildKey(agentId, depth, workspaceRoot), out var entry))
            return null;

        if (_invalidationEnabled && !IsStillValid(fileSystem, entry.Stamps))
            return null;

        return entry.Entries;
    }

    /// <summary>
    /// Stores a freshly built tree together with the stamps observed while building it.
    /// </summary>
    /// <param name="agentId">Agent identifier the tree belongs to.</param>
    /// <param name="depth">Requested depth limit; part of the cache key.</param>
    /// <param name="workspaceRoot">Resolved workspace root; part of the cache key.</param>
    /// <param name="entries">The built tree.</param>
    /// <param name="stamps">Filesystem observations that validate the tree.</param>
    public void Set(
        AgentId agentId,
        int depth,
        string workspaceRoot,
        IReadOnlyList<WorkspaceEntryDto> entries,
        IReadOnlyList<WorkspaceTreeStamp> stamps)
    {
        _entries[BuildKey(agentId, depth, workspaceRoot)] = new CacheEntry(entries, stamps);
    }

    private static string BuildKey(AgentId agentId, int depth, string workspaceRoot) =>
        string.Concat(agentId.Value, "\u0000", depth.ToString(System.Globalization.CultureInfo.InvariantCulture), "\u0000", workspaceRoot);

    private static bool IsStillValid(IFileSystem fileSystem, IReadOnlyList<WorkspaceTreeStamp> stamps)
    {
        foreach (var stamp in stamps)
        {
            try
            {
                if (stamp.IsDirectory)
                {
                    var directory = fileSystem.DirectoryInfo.New(stamp.Path);
                    if (!directory.Exists || directory.LastWriteTimeUtc != stamp.LastWriteUtc)
                        return false;

                    continue;
                }

                var file = fileSystem.FileInfo.New(stamp.Path);
                if (!file.Exists || file.Length != stamp.Length || file.LastWriteTimeUtc != stamp.LastWriteUtc)
                    return false;
            }
            catch (IOException)
            {
                // A vanished or briefly locked entry means the cached answer can no longer be trusted.
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        return true;
    }

    private sealed record CacheEntry(IReadOnlyList<WorkspaceEntryDto> Entries, IReadOnlyList<WorkspaceTreeStamp> Stamps);
}
