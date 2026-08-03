using System.Text.Json.Nodes;

namespace BotNexus.Gateway.Configuration.Shadow;

/// <summary>How a single key differs between the JSON document and the store's round-trip.</summary>
public enum ConfigDiffKind
{
    /// <summary>Present in the JSON source, absent from the store's round-trip. The store lost a key.</summary>
    MissingFromStore = 0,

    /// <summary>Present in the store's round-trip, absent from the JSON source. The store invented a key.</summary>
    ExtraInStore = 1,

    /// <summary>
    /// Present in both, but the value differs. Includes the tri-state failure: a key that is
    /// <see cref="ConfigValueState.ExplicitNull"/> in JSON and <see cref="ConfigValueState.Unset"/> in
    /// the store (or vice versa) is a difference, not a match.
    /// </summary>
    ValueDiffers = 2,
}

/// <summary>One key-level discrepancy, carrying both sides so a report is actionable without re-running.</summary>
/// <param name="Path">Canonical dotted path of the key.</param>
/// <param name="Kind">Which class of discrepancy.</param>
/// <param name="Source">State and value as observed in the JSON source document.</param>
/// <param name="Store">State and value as observed in the store's round-trip.</param>
public readonly record struct ConfigDiffEntry(
    string Path,
    ConfigDiffKind Kind,
    ConfigEntry Source,
    ConfigEntry Store);

/// <summary>
/// The result of one shadow comparison. Immutable and self-describing so it can be logged, returned
/// from a CLI surface, or retained as the "most recent result" without re-running the migration.
/// </summary>
/// <param name="ComparedAtUtc">When the comparison ran.</param>
/// <param name="SourceKeyCount">
/// How many leaf keys the JSON source contributed. Reported alongside the discrepancy count because a
/// clean diff over an empty input is not evidence of anything - a sweep must state its input size.
/// </param>
/// <param name="StoreKeyCount">How many leaf keys the store round-trip contributed.</param>
/// <param name="Differences">Every discrepancy found, ordered by path.</param>
public sealed record ConfigShadowDiffReport(
    DateTimeOffset ComparedAtUtc,
    int SourceKeyCount,
    int StoreKeyCount,
    IReadOnlyList<ConfigDiffEntry> Differences)
{
    /// <summary>True when the store round-trip reproduced the source document exactly.</summary>
    public bool IsClean => Differences.Count == 0;

    /// <summary>
    /// A one-line summary suitable for a log field. States the input counts as well as the outcome,
    /// so "0 differences" can be distinguished from "the comparison never saw anything".
    /// </summary>
    public string Summary => IsClean
        ? $"clean: {SourceKeyCount} source keys, {StoreKeyCount} store keys, 0 differences"
        : $"{Differences.Count} difference(s) across {SourceKeyCount} source keys / {StoreKeyCount} store keys";
}

/// <summary>
/// Compares a configuration document against a store's round-trip of it, key by key (#2766 AC3, AC4).
///
/// <para>
/// <b>The diff is the deliverable, not the migration.</b> A migration that runs without throwing proves
/// nothing - it can drop a key, collapse a tri-state, or silently reorder and still complete happily.
/// What proves faithfulness is comparing the round-trip against the source and finding them identical,
/// over real production configuration rather than a fixture someone remembered to write.
/// </para>
///
/// <para>
/// <b>Comparison is on the flattened raw documents</b> (see <see cref="ConfigDocumentFlattener"/>),
/// never on bound <see cref="PlatformConfig"/> instances, because binding collapses "absent" and
/// "explicitly null" into an identical <c>null</c> field. A diff built on bound objects would report
/// clean against a store that had already lost the distinction the diff exists to police.
/// </para>
/// </summary>
public static class ConfigShadowDiff
{
    /// <summary>
    /// Compares two raw configuration documents.
    /// </summary>
    /// <param name="source">The authoritative JSON document.</param>
    /// <param name="storeRoundTrip">The document reconstructed from the store.</param>
    /// <param name="timeProvider">Clock seam; defaults to <see cref="TimeProvider.System"/>.</param>
    public static ConfigShadowDiffReport Compare(
        JsonObject? source,
        JsonObject? storeRoundTrip,
        TimeProvider? timeProvider = null)
    {
        var sourceEntries = ConfigDocumentFlattener.Flatten(source);
        var storeEntries = ConfigDocumentFlattener.Flatten(storeRoundTrip);

        var differences = new List<ConfigDiffEntry>();

        foreach (var (path, sourceEntry) in sourceEntries)
        {
            if (!storeEntries.TryGetValue(path, out var storeEntry))
            {
                differences.Add(new ConfigDiffEntry(
                    path,
                    ConfigDiffKind.MissingFromStore,
                    sourceEntry,
                    new ConfigEntry(path, ConfigValueState.Unknown, Value: null)));
                continue;
            }

            // State is compared before value. A key that is ExplicitNull on one side and Unset on the
            // other has equal values (both null) and unequal meaning - comparing values alone is exactly
            // how the tri-state collapse would slip through unreported.
            if (sourceEntry.State != storeEntry.State ||
                !string.Equals(sourceEntry.Value, storeEntry.Value, StringComparison.Ordinal))
            {
                differences.Add(new ConfigDiffEntry(
                    path,
                    ConfigDiffKind.ValueDiffers,
                    sourceEntry,
                    storeEntry));
            }
        }

        foreach (var (path, storeEntry) in storeEntries)
        {
            if (!sourceEntries.ContainsKey(path))
            {
                differences.Add(new ConfigDiffEntry(
                    path,
                    ConfigDiffKind.ExtraInStore,
                    new ConfigEntry(path, ConfigValueState.Unknown, Value: null),
                    storeEntry));
            }
        }

        differences.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));

        var clock = timeProvider ?? TimeProvider.System;
        return new ConfigShadowDiffReport(
            clock.GetUtcNow(),
            sourceEntries.Count,
            storeEntries.Count,
            differences);
    }
}
