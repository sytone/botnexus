using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;

namespace BotNexus.Gateway.Configuration.Writers;

/// <summary>
/// Turns "here is the updated DTO for this subtree" into the exact set of keys that changed (#3532).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why reflection over the DTO instead of a hand-maintained key list.</b> A declared list of writable
/// paths is a second source of truth that drifts the moment a property is added - which is the defect
/// class <c>ConfigFieldCoverageFenceArchitectureTests</c> exists to police. Serialising the DTO and
/// flattening the result derives the keys from the type itself, so a new property is writable the day it
/// is declared and no list needs updating.
/// </para>
/// <para>
/// <b>Why it reuses <see cref="ConfigDocumentFlattener"/> rather than walking the DTO directly.</b> The
/// store already flattens documents with that walker, including its two non-obvious rules - arrays are
/// leaves, and an empty object is a value rather than a branch. A second walker would eventually
/// disagree about one of them, and the disagreement would surface as keys that write but never read
/// back. One walker, used by both sides, cannot drift.
/// </para>
/// <para>
/// <b>The unmodelled-key hazard, and why the prefix contains it.</b> 33 of the 34 configuration classes
/// carry no <c>[JsonExtensionData]</c>, so a key present in the stored document but absent from the CLR
/// type does not survive a typed round-trip - it deserialises to nothing and re-serialises as gone.
/// Under a whole-document write that silently deletes it. Here it is reported as a removal within the
/// prefix only, so the caller's blast radius is bounded to the subtree it named, and
/// <see cref="ConfigDiffOptions.PreserveUnmodelledKeys"/> can suppress the removal entirely for callers
/// that cannot vouch for completeness.
/// </para>
/// </remarks>
public static class ConfigDtoDiffer
{
    /// <summary>
    /// Serialisation settings used to project a DTO into document shape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>camelCase is mandatory, not cosmetic.</b> Every persistence path in this project writes through
    /// <see cref="JsonNamingPolicy.CamelCase"/> (<c>PlatformConfigWriter</c>, <c>PlatformConfigSchema</c>,
    /// <c>ConfigHydrationService</c>), so the stored document holds <c>model</c>, not <c>Model</c>. A
    /// differ that projected PascalCase would match nothing: every property would read as a new key and
    /// every real key as a removal, so a one-field edit would delete the whole subtree and re-add it
    /// under keys nothing reads. That is a worse #2816 than the one this class replaces.
    /// </para>
    /// <para>
    /// <b><c>DefaultIgnoreCondition</c> is deliberately NOT <c>WhenWritingNull</c></b>, even though
    /// <c>PlatformConfigWriter.PlatformWriteOptions</c> sets it. That setting is what lets the old writer
    /// silently convert "suppress the inherited value" into "inherit" - a null property simply vanishes
    /// from the output. Here a null must survive as JSON <c>null</c> so it lands as
    /// <see cref="ConfigValueState.ExplicitNull"/> and is written as a suppression. Copying that flag
    /// across would reintroduce the tri-state collapse the store's design notes call the highest-risk
    /// failure in this direction.
    /// </para>
    /// </remarks>
    private static readonly JsonSerializerOptions ProjectionOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Computes the change set between the stored document and an updated DTO for one subtree.
    /// </summary>
    /// <param name="current">The document as currently stored. <see langword="null"/> is treated as empty.</param>
    /// <param name="dto">The updated object for <paramref name="pathPrefix"/>.</param>
    /// <param name="pathPrefix">
    /// Canonical dotted path the DTO speaks for, e.g. <c>agents.nova</c>. Empty means the whole document.
    /// </param>
    /// <param name="options">Diff behaviour; defaults to <see cref="ConfigDiffOptions.Default"/>.</param>
    public static ConfigChangeSet Diff(
        JsonObject? current,
        object dto,
        string pathPrefix,
        ConfigDiffOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(pathPrefix);
        options ??= ConfigDiffOptions.Default;

        var desired = Project(dto, pathPrefix);
        var existing = ScopedExisting(current, pathPrefix);

        var upserts = new List<ConfigEntry>();
        foreach (var (path, entry) in desired)
        {
            // Compare state before value. Two entries can share a null value and still differ - Unset
            // versus ExplicitNull - and treating them as equal is exactly the collapse that hands a
            // world default back to an agent that deliberately declined it.
            if (existing.TryGetValue(path, out var before)
                && before.State == entry.State
                && string.Equals(before.Value, entry.Value, StringComparison.Ordinal))
            {
                continue;
            }

            upserts.Add(entry);
        }

        var removals = new List<string>();
        if (!options.PreserveUnmodelledKeys)
        {
            foreach (var path in existing.Keys)
            {
                if (!desired.ContainsKey(path))
                {
                    removals.Add(path);
                }
            }

            removals.Sort(StringComparer.Ordinal);
        }

        return new ConfigChangeSet(pathPrefix, upserts, removals);
    }

    /// <summary>
    /// Serialises <paramref name="dto"/> and flattens it into fully-qualified paths under
    /// <paramref name="pathPrefix"/>.
    /// </summary>
    private static IReadOnlyDictionary<string, ConfigEntry> Project(object dto, string pathPrefix)
    {
        var node = JsonSerializer.SerializeToNode(dto, dto.GetType(), ProjectionOptions);

        // A DTO that serialises to a scalar or an array is a single leaf AT the prefix, not a subtree
        // under it. Rejecting it instead would block legitimate writes to leaf-valued paths.
        if (node is not JsonObject obj)
        {
            if (pathPrefix.Length == 0)
            {
                throw new ArgumentException(
                    "A root-scoped change set requires an object DTO; a scalar cannot describe the " +
                    "whole configuration document.",
                    nameof(dto));
            }

            var state = node is null ? ConfigValueState.ExplicitNull : ConfigValueState.Value;
            var value = node?.ToJsonString(ProjectionOptions);
            return new Dictionary<string, ConfigEntry>(StringComparer.Ordinal)
            {
                [pathPrefix] = new ConfigEntry(pathPrefix, state, value),
            };
        }

        var flat = ConfigDocumentFlattener.Flatten(obj);
        if (pathPrefix.Length == 0)
        {
            return flat;
        }

        var qualified = new Dictionary<string, ConfigEntry>(StringComparer.Ordinal);
        foreach (var (relative, entry) in flat)
        {
            var full = $"{pathPrefix}.{relative}";
            qualified[full] = entry with { Path = full };
        }

        return qualified;
    }

    /// <summary>
    /// The stored entries that fall within <paramref name="pathPrefix"/>.
    /// </summary>
    /// <remarks>
    /// Segment-aware on purpose: a raw <c>StartsWith("agents.nova")</c> would also match
    /// <c>agents.novaBackup.model</c> and delete an unrelated agent's configuration. The prefix must be
    /// followed by a path separator to count as inside the subtree.
    /// </remarks>
    private static IReadOnlyDictionary<string, ConfigEntry> ScopedExisting(JsonObject? current, string pathPrefix)
    {
        var all = ConfigDocumentFlattener.Flatten(current);
        if (pathPrefix.Length == 0)
        {
            return all;
        }

        var scoped = new Dictionary<string, ConfigEntry>(StringComparer.Ordinal);
        var boundary = pathPrefix + ".";
        foreach (var (path, entry) in all)
        {
            if (string.Equals(path, pathPrefix, StringComparison.Ordinal)
                || path.StartsWith(boundary, StringComparison.Ordinal))
            {
                scoped[path] = entry;
            }
        }

        return scoped;
    }
}

/// <summary>
/// Diff behaviour for <see cref="ConfigDtoDiffer"/>.
/// </summary>
/// <param name="PreserveUnmodelledKeys">
/// When true, keys stored under the prefix but absent from the DTO are left alone instead of being
/// removed. Callers that hold a partially-modelled view of a subtree must set this: without it, every
/// key their CLR type does not declare would be deleted on save, which is the #2816 failure.
/// </param>
public sealed record ConfigDiffOptions(bool PreserveUnmodelledKeys)
{
    /// <summary>
    /// The default: a DTO speaks completely for its subtree, so absence means removal.
    /// </summary>
    /// <remarks>
    /// Chosen as the default because the alternative makes deletion impossible - in the eight keyed
    /// dictionaries a removed agent or channel is visible only as absence, so a differ that never
    /// removes could not express it at all.
    /// </remarks>
    public static ConfigDiffOptions Default { get; } = new(PreserveUnmodelledKeys: false);

    /// <summary>
    /// For callers whose DTO models only part of the subtree it is writing.
    /// </summary>
    public static ConfigDiffOptions Additive { get; } = new(PreserveUnmodelledKeys: true);
}
