using System.Text.Json.Nodes;

namespace BotNexus.Gateway.Configuration.Store;

/// <summary>
/// Applies a <see cref="ConfigChangeSet"/> to a live configuration document in place (#3532).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why patch the document rather than replace it with a serialised DTO.</b> 33 of the 34
/// configuration classes carry no <c>[JsonExtensionData]</c>, so any key the CLR type does not model
/// does not survive a typed round-trip - it deserialises to nothing and re-serialises as gone. Patching
/// visits only the paths the change set names, so an unmodelled sibling is never touched and therefore
/// cannot be lost. That is the structural difference between this and the whole-document write it
/// replaces, and it is the #2816 fix.
/// </para>
/// <para>
/// <b>Removals prune empty parents, but only ones they emptied.</b> Deleting the last key under
/// <c>agents.retired</c> leaves <c>"retired": {}</c> behind, which reads back as a configured-but-empty
/// agent rather than an absent one. Pruning stops as soon as a parent still has children, so a removal
/// can never cascade into a sibling's subtree.
/// </para>
/// </remarks>
public static class ConfigDocumentPatcher
{
    /// <summary>
    /// Applies <paramref name="changes"/> to <paramref name="document"/>, mutating it in place.
    /// </summary>
    /// <param name="document">The document to patch. Intermediate objects are created as needed.</param>
    /// <param name="changes">The keys to upsert and remove.</param>
    public static void Apply(JsonObject document, ConfigChangeSet changes)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(changes);

        foreach (var entry in changes.Upserts)
        {
            var segments = entry.Path.Split('.');
            var parent = EnsureParent(document, segments);
            var leaf = segments[^1];

            // ExplicitNull must land as a JSON null rather than as a missing key: absent means
            // "inherit", null means "suppress the inherited value", and collapsing them hands a world
            // default back to an agent that deliberately declined it.
            parent[leaf] = entry.State == ConfigValueState.ExplicitNull || entry.Value is null
                ? null
                : JsonNode.Parse(entry.Value);
        }

        foreach (var path in changes.Removals)
        {
            RemovePath(document, path.Split('.'));
        }
    }

    /// <summary>
    /// Walks to the object that should hold the final segment, creating missing levels.
    /// </summary>
    /// <remarks>
    /// A non-object encountered mid-path is replaced. That happens when a scalar becomes a section - a
    /// legitimate shape change - and refusing it would make the new shape unwritable.
    /// </remarks>
    private static JsonObject EnsureParent(JsonObject root, string[] segments)
    {
        var current = root;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];

            if (current[segment] is JsonObject existing)
            {
                current = existing;
                continue;
            }

            var created = new JsonObject();
            current[segment] = created;
            current = created;
        }

        return current;
    }

    /// <summary>
    /// Removes one leaf and any parents its removal left empty.
    /// </summary>
    private static void RemovePath(JsonObject root, string[] segments)
    {
        // Record the chain on the way down so emptied parents can be pruned on the way back up;
        // JsonNode exposes no parent pointer that survives detachment.
        var chain = new List<JsonObject>(segments.Length) { root };
        var current = root;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (current[segments[i]] is not JsonObject next)
            {
                // The path does not exist. A removal for a key that is already absent is a no-op, not an
                // error: two writers racing to remove the same key must both succeed.
                return;
            }

            current = next;
            chain.Add(current);
        }

        current.Remove(segments[^1]);

        for (var i = chain.Count - 1; i > 0; i--)
        {
            if (chain[i].Count > 0)
            {
                // This parent still holds other keys, so it was not emptied by this removal. Stop:
                // continuing would be pruning something the change set never touched.
                break;
            }

            chain[i - 1].Remove(segments[i - 1]);
        }
    }
}
