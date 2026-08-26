using System.Text.Json.Nodes;

namespace BotNexus.Gateway.Configuration.Store;

/// <summary>
/// Computes the keys that differ between two configuration documents (#3532).
/// </summary>
/// <remarks>
/// <para>
/// Unlike a whole-document write it names only the keys that actually moved.
/// </para>
/// </remarks>
public static class ConfigDocumentDiffer
{
    /// <summary>
    /// Diffs <paramref name="before"/> against <paramref name="after"/>.
    /// </summary>
    /// <param name="before">The document as it was. <see langword="null"/> makes every key an insert.</param>
    /// <param name="after">The document as it should be.</param>
    public static ConfigChangeSet Diff(JsonObject? before, JsonObject after)
    {
        ArgumentNullException.ThrowIfNull(after);

        var previous = ConfigDocumentFlattener.Flatten(before);
        var next = ConfigDocumentFlattener.Flatten(after);

        var upserts = new List<ConfigEntry>();
        foreach (var (path, entry) in next)
        {
            // State before value: Unset and ExplicitNull share a null value but mean opposite things
            // (inherit versus suppress), so comparing values alone would treat a suppression as a no-op.
            if (previous.TryGetValue(path, out var was)
                && was.State == entry.State
                && string.Equals(was.Value, entry.Value, StringComparison.Ordinal))
            {
                continue;
            }

            upserts.Add(entry);
        }

        var removals = new List<string>();
        foreach (var path in previous.Keys)
        {
            if (!next.ContainsKey(path))
            {
                removals.Add(path);
            }
        }

        // Deterministic order so a change set renders identically across runs - a diff that reorders
        // itself is unreviewable in a log and unstable in a test.
        upserts.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));
        removals.Sort(StringComparer.Ordinal);

        return new ConfigChangeSet(upserts, removals);
    }
}
