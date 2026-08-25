using System.Text.Json.Nodes;

namespace BotNexus.Gateway.Configuration.Store;

/// <summary>
/// Computes the keys that differ between two configuration documents (#3532).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a document differ as well as <see cref="Writers.ConfigDtoDiffer"/>.</b> They serve different
/// callers. The DTO differ answers "here is an updated object for this subtree" and derives the key
/// names reflectively from the CLR type - that is the API-facing contract. This one answers "here is
/// the document before and after a mutation", which is what <c>PlatformConfigWriter</c> already holds:
/// its mutation lambdas edit a <see cref="JsonObject"/> in place against a pristine snapshot.
/// </para>
/// <para>
/// <b>Routing the existing writer through the DTO differ instead would be a downgrade.</b> The mutated
/// document is already the desired state, so projecting a CLR type over it would add a lossy step: 33
/// of the 34 configuration classes carry no <c>[JsonExtensionData]</c>, so every unmodelled key would
/// be dropped on the way through. Comparing documents keeps unmodelled keys in scope and still reduces
/// the write to the changed keys.
/// </para>
/// <para>
/// The result carries an empty <see cref="ConfigChangeSet.PathPrefix"/> because a document-wide diff
/// genuinely speaks for the whole tree - but unlike a whole-document write, it only names the keys that
/// actually moved.
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

        return new ConfigChangeSet(string.Empty, upserts, removals);
    }
}
