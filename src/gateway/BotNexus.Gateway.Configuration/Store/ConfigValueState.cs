using System.Text.Json;
using System.Text.Json.Nodes;

namespace BotNexus.Gateway.Configuration.Store;

/// <summary>
/// The four states a configuration key can be in (#2766 AC4).
///
/// <para>
/// <b>Why four and not two.</b> Configuration inheritance is three-valued: a key <em>absent</em> from a
/// document means "inherit from the layer above", a key present with a <c>null</c> value means
/// "suppress the inherited value", and a key with a value means "override". <see cref="AgentConfigMerger"/>
/// detects this today via the raw <see cref="JsonElement"/>, because the distinction exists only in the
/// document - a bound POCO has already collapsed absent and null into the same <c>null</c> field.
/// </para>
///
/// <para>
/// A relational column cannot represent that distinction either: <c>NULL</c> means both "unset" and
/// "explicitly nulled". So the single most likely way a SQLite-backed store (#2646) breaks
/// configuration is by collapsing <see cref="Unset"/> into <see cref="ExplicitNull"/> - silently
/// handing back a world default to every agent that had deliberately declined it. No exception, no log
/// line, and the affected agents behave subtly differently forever.
/// </para>
///
/// <para>
/// <see cref="Unknown"/> is the fourth state and is not a value state at all: it means the key was not
/// present in the document being examined, as distinct from being present-and-unset. It exists so the
/// diff can distinguish "the store lost this key" from "the store recorded this key as unset", which
/// are different bugs with the same symptom if collapsed.
/// </para>
/// </summary>
public enum ConfigValueState
{
    /// <summary>The key does not appear in this document at all. Not a value; the absence of an entry.</summary>
    Unknown = 0,

    /// <summary>
    /// The key's parent object exists but the key itself is not present - "inherit from the layer above".
    /// Collapsing this into <see cref="ExplicitNull"/> is the tri-state failure #2646 must avoid.
    /// </summary>
    Unset = 1,

    /// <summary>
    /// The key is present with a JSON <c>null</c> - "suppress the inherited value". Deliberately distinct
    /// from <see cref="Unset"/>.
    /// </summary>
    ExplicitNull = 2,

    /// <summary>The key is present with a non-null value.</summary>
    Value = 3,
}

/// <summary>
/// A single configuration key's state and value, as observed in one document.
/// </summary>
/// <param name="Path">Canonical dotted path, e.g. <c>gateway.compaction.enabled</c>.</param>
/// <param name="State">Which of the four states this key is in.</param>
/// <param name="Value">
/// Canonical JSON text of the value when <see cref="State"/> is <see cref="ConfigValueState.Value"/>;
/// <see langword="null"/> otherwise. Held as text rather than as a live <see cref="JsonNode"/> so two
/// entries can be compared by ordinal string equality without re-walking the tree, and so a captured
/// report stays valid after the source document is disposed.
/// </param>
public readonly record struct ConfigEntry(string Path, ConfigValueState State, string? Value);

/// <summary>
/// Flattens a configuration document into canonical dotted paths, preserving the tri-state
/// distinction that a bound POCO destroys (#2766 AC4).
///
/// <para>
/// <b>Why this reads the raw document and never a round-tripped <see cref="PlatformConfig"/>.</b>
/// Binding to the POCO collapses "absent" and "explicitly null" into an identical <c>null</c> field,
/// which is precisely the distinction the diff exists to police. A diff built on bound objects would
/// report clean against a store that had already lost the distinction - a vacuous instrument that looks
/// healthy. So the flattener walks <see cref="JsonNode"/> directly.
/// </para>
///
/// <para>
/// <b>Arrays are leaves.</b> Configuration merges arrays wholesale rather than element-wise (pinned by
/// <c>ExtensionConfigMergerParityTests</c>), so an array's identity is its whole serialised value.
/// Descending into array indices would report a reordering as N changes rather than one, and would
/// invent paths like <c>tools[0]</c> that no config path resolver recognises.
/// </para>
/// </summary>
public static class ConfigDocumentFlattener
{
    private static readonly JsonSerializerOptions CanonicalValueOptions = new()
    {
        WriteIndented = false,
    };

    /// <summary>
    /// Flattens <paramref name="root"/> into one <see cref="ConfigEntry"/> per leaf, keyed by canonical
    /// dotted path.
    /// </summary>
    /// <param name="root">
    /// The raw configuration document. <see langword="null"/> yields an empty result, which is the
    /// correct reading of "no document" - every key is <see cref="ConfigValueState.Unknown"/> by absence
    /// rather than being asserted as unset.
    /// </param>
    public static IReadOnlyDictionary<string, ConfigEntry> Flatten(JsonObject? root)
    {
        var result = new Dictionary<string, ConfigEntry>(StringComparer.Ordinal);
        if (root is null)
        {
            return result;
        }

        Walk(root, prefix: string.Empty, result);
        return result;
    }

    private static void Walk(JsonObject obj, string prefix, Dictionary<string, ConfigEntry> sink)
    {
        foreach (var (key, node) in obj)
        {
            var path = prefix.Length == 0 ? key : $"{prefix}.{key}";

            switch (node)
            {
                case null:
                    // Present with an explicit JSON null. This is the state a relational NULL column
                    // cannot distinguish from absence, so it is recorded distinctly and deliberately.
                    sink[path] = new ConfigEntry(path, ConfigValueState.ExplicitNull, Value: null);
                    break;

                case JsonObject nested when nested.Count == 0:
                    // An empty object is a leaf with a value, not a branch with no children. Treating it
                    // as a branch would make it vanish from the flattened form entirely, so a store that
                    // dropped it would diff clean.
                    sink[path] = new ConfigEntry(path, ConfigValueState.Value, "{}");
                    break;

                case JsonObject nested:
                    Walk(nested, path, sink);
                    break;

                default:
                    sink[path] = new ConfigEntry(path, ConfigValueState.Value, Canonicalise(node));
                    break;
            }
        }
    }

    /// <summary>
    /// Renders a leaf to canonical JSON text so two documents' values compare by ordinal equality.
    /// </summary>
    private static string Canonicalise(JsonNode node) => node.ToJsonString(CanonicalValueOptions);
}

