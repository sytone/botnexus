using System.Text.Json.Nodes;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Tracks the JSON paths the operator actually edited on a settings form and turns them into the
/// minimal atomic patch to save (issue #2059).
/// </summary>
/// <remarks>
/// <para>
/// Both the desktop <c>Configuration</c> page and the mobile <c>Settings</c> page previously
/// carried a byte-for-byte copy of the same save loop, which PUT every materialised top-level
/// section of the snapshot it had loaded. Fixing that in two places would have left the two
/// surfaces free to drift apart again, so the dirty-path bookkeeping lives here once and both
/// pages consume it. (Seventh instance of the "duplicated implementation, one copy drifted" shape
/// in this codebase; the fix is a shared seam, not two parallel edits.)
/// </para>
/// <para>
/// <b>Path normalisation is the load-bearing part.</b> Editing <c>gateway.port</c> and then
/// <c>gateway.host</c> must produce two operations, not one write of the whole <c>gateway</c>
/// object - otherwise the patch reintroduces exactly the clobbering it exists to prevent, just at
/// a lower level. But a <em>collection</em> change (add/remove/rename/reorder) genuinely is a
/// change to the container, so the container path is recorded and the whole collection is sent.
/// The form reports the correct granularity for each case; this type does not second-guess it.
/// </para>
/// </remarks>
public sealed class ConfigDirtyPathTracker
{
    private readonly HashSet<string> _paths = new(StringComparer.Ordinal);

    /// <summary>Whether anything has been edited since the last <see cref="Reset"/>.</summary>
    public bool IsDirty => _paths.Count > 0;

    /// <summary>The edited paths, ordered for stable, reproducible patch batches.</summary>
    public IReadOnlyList<string> Paths => [.. _paths.Order(StringComparer.Ordinal)];

    /// <summary>Records that the node at <paramref name="path"/> was edited.</summary>
    public void Mark(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            _paths.Add(path);
    }

    /// <summary>Clears the tracked paths, e.g. after a successful save or a reload.</summary>
    public void Reset() => _paths.Clear();

    /// <summary>
    /// Builds the patch operations for the tracked paths against the current form state.
    /// </summary>
    /// <remarks>
    /// A tracked path that no longer resolves is emitted as a <c>Remove</c>: the operator deleted
    /// it, and omitting the operation would silently keep the old value on disk - a save that
    /// reports success while discarding the change is the failure mode this whole issue is about.
    /// A path that is redundant because an ancestor is also dirty is dropped, so a container write
    /// and a member write cannot fight each other inside one batch.
    /// </remarks>
    public IReadOnlyList<ConfigPatchOperationDto> BuildOperations(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var paths = Paths;
        var operations = new List<ConfigPatchOperationDto>(paths.Count);

        foreach (var path in paths)
        {
            if (paths.Any(other => other.Length < path.Length && IsAncestor(other, path)))
                continue;

            var node = Resolve(config, path);
            operations.Add(node is null
                ? new ConfigPatchOperationDto(path, null, Remove: true)
                : new ConfigPatchOperationDto(path, node.DeepClone()));
        }

        return operations;
    }

    // "gateway" is an ancestor of "gateway.port" and of "gateway[0]", but NOT of "gatewayExtra".
    private static bool IsAncestor(string candidate, string path)
        => path.StartsWith(candidate, StringComparison.Ordinal) &&
           path.Length > candidate.Length &&
           (path[candidate.Length] == '.' || path[candidate.Length] == '[');

    private static JsonNode? Resolve(JsonObject root, string path)
    {
        JsonNode? current = root;
        foreach (var segment in Tokenize(path))
        {
            if (current is null)
                return null;

            if (segment.StartsWith('['))
            {
                if (current is not JsonArray array ||
                    !int.TryParse(segment.Trim('[', ']'), out var index) ||
                    index < 0 || index >= array.Count)
                    return null;
                current = array[index];
                continue;
            }

            if (current is not JsonObject obj || !obj.TryGetPropertyValue(segment, out var next))
                return null;
            current = next;
        }

        return current;
    }

    /// <summary>
    /// Splits a form path into object-member and array-index segments. Mirrors the renderer's own
    /// tokenizer and the server-side applier so one path means one node on all three sides.
    /// </summary>
    public static IEnumerable<string> Tokenize(string path)
    {
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bracket = part.IndexOf('[', StringComparison.Ordinal);
            if (bracket < 0)
            {
                yield return part;
                continue;
            }

            if (bracket > 0)
                yield return part[..bracket];

            var rest = part[bracket..];
            while (rest.Length > 0)
            {
                var close = rest.IndexOf(']', StringComparison.Ordinal);
                if (close < 0)
                    yield break;
                yield return rest[..(close + 1)];
                rest = rest[(close + 1)..];
            }
        }
    }
}
