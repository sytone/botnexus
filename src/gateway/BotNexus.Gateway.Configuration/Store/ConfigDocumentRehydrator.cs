using System.Text.Json.Nodes;

namespace BotNexus.Gateway.Configuration.Store;

/// <summary>
/// Rebuilds a configuration document from flattened entries - the inverse of
/// <see cref="ConfigDocumentFlattener"/> (#2646 PBI 3).
///
/// <para>
/// <b>Why the read path needs this at all.</b> The store's natural shape is one row per key, which is
/// what preserves the tri-state distinction a nullable column destroys. But every existing consumer of
/// configuration - <see cref="PlatformConfigLoader"/>, the mergers, the writer, the schema validators -
/// speaks <see cref="JsonObject"/>. Making the store authoritative therefore means reconstructing a
/// document, and the reconstruction has to be provably lossless or the cutover silently changes
/// configuration for every agent.
/// </para>
///
/// <para>
/// <b>The round trip is lossless because the flattener never emits <see cref="ConfigValueState.Unset"/>.</b>
/// A document can express only two of the three inheritance states directly: a key is absent, or it is
/// present with <c>null</c>, or it is present with a value. "Present and unset" has no JSON spelling,
/// which is exactly why <see cref="ConfigValueState.Unset"/> exists as a distinct state for the store to
/// report. So flatten produces only <see cref="ConfigValueState.ExplicitNull"/> and
/// <see cref="ConfigValueState.Value"/>, and rehydrating those two is total. <see cref="ConfigValueState.Unset"/>
/// and <see cref="ConfigValueState.Unknown"/> rehydrate to <em>absence</em>, which is their correct
/// document reading - both mean "inherit from the layer above".
/// </para>
///
/// <para>
/// <b>Known limitation, deliberately surfaced rather than hidden: a key whose own name contains a dot
/// cannot survive the round trip.</b> The flattener joins path segments with <c>.</c>, so
/// <c>{"a.b": 1}</c> and <c>{"a": {"b": 1}}</c> flatten to the identical path <c>a.b</c> and rehydrate
/// to the nested form. Configuration keys are C# property names and extension identifiers, neither of
/// which contains a dot today, and <see cref="ConfigStoreRoundTripValidator"/> would report any document
/// that violated it as a difference rather than letting it pass silently. The alternative - escaping
/// separators - would make every stored path unreadable in a database a human is expected to inspect,
/// for a case that does not occur. If it ever does occur, the diff reports it loudly at startup while
/// JSON is still authoritative.
/// </para>
/// </summary>
public static class ConfigDocumentRehydrator
{
    /// <summary>
    /// Rebuilds a document from <paramref name="entries"/>.
    /// </summary>
    /// <param name="entries">Flattened entries keyed by canonical dotted path.</param>
    /// <returns>
    /// The reconstructed document. An empty entry set yields an empty object rather than
    /// <see langword="null"/>: "no keys" and "no document" are different, and only the caller that read
    /// the store knows which it observed.
    /// </returns>
    public static JsonObject Rehydrate(IReadOnlyDictionary<string, ConfigEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var root = new JsonObject();

        // Ordinal sort makes the reconstruction deterministic. Two runs over the same rows must
        // produce byte-identical JSON, otherwise a parity check comparing serialised text would
        // report spurious differences driven purely by row order from SQLite.
        foreach (var path in entries.Keys.OrderBy(static k => k, StringComparer.Ordinal))
        {
            var entry = entries[path];

            // Unset and Unknown both mean "the key is not in this document". Writing a JSON null for
            // them would be the exact tri-state collapse the store exists to prevent, in the opposite
            // direction: every declined inheritance would become an explicit suppression.
            if (entry.State is ConfigValueState.Unset or ConfigValueState.Unknown)
            {
                continue;
            }

            Place(root, path, entry);
        }

        return root;
    }

    private static void Place(JsonObject root, string path, ConfigEntry entry)
    {
        var segments = path.Split('.');
        var cursor = root;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];

            if (cursor[segment] is JsonObject existing)
            {
                cursor = existing;
                continue;
            }

            // A non-object already sitting at an intermediate segment means two stored paths disagree
            // about the document's shape (e.g. both "a" and "a.b" as leaves). That is a corrupt store,
            // not a recoverable ambiguity: silently overwriting one would produce a document that
            // round-trips cleanly while having lost a key.
            if (cursor[segment] is not null)
            {
                throw new InvalidOperationException(
                    $"Cannot rebuild configuration path '{path}': segment '{segment}' is already a " +
                    "non-object leaf. Two stored entries describe incompatible document shapes, which " +
                    "means the store's contents are inconsistent and must not be treated as authoritative.");
            }

            var created = new JsonObject();
            cursor[segment] = created;
            cursor = created;
        }

        var leaf = segments[^1];
        cursor[leaf] = entry.State switch
        {
            ConfigValueState.ExplicitNull => null,
            _ => JsonNode.Parse(entry.Value ?? "null"),
        };
    }
}

