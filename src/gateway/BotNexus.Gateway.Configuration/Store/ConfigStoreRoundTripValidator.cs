using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Shadow;

namespace BotNexus.Gateway.Configuration.Store;

/// <summary>
/// Proves a dual read is byte-identical: file-loaded versus store-loaded (#2766 AC7).
///
/// <para>
/// <b>Why this is a separate check from <see cref="ConfigShadowDiff"/>.</b> The shadow diff compares
/// <em>entries</em>, which is the right granularity for locating a specific lost key and the only
/// granularity able to express <see cref="ConfigValueState.Unset"/>. AC7 asks a different question:
/// whether the document a consumer would actually receive is identical. Those can disagree - a
/// rehydrator that dropped an empty object, or emitted keys in an unstable order, produces an identical
/// entry set and a different document. Checking only entries would leave the rehydration step, the one
/// piece PBI 3 adds to the read path, entirely unverified.
/// </para>
///
/// <para>
/// <b>Compares canonical serialised text, not object graphs.</b> Text is what "byte-identical" means,
/// and it catches ordering and formatting divergence that a structural comparison would forgive. The
/// rehydrator sorts ordinally for exactly this reason, so a stable input yields stable text.
/// </para>
/// </summary>
public static class ConfigStoreRoundTripValidator
{
    /// <summary>
    /// Compares the document a consumer would get from the file against the one it would get from the
    /// store.
    /// </summary>
    /// <param name="source">The document as read from <c>config.json</c>.</param>
    /// <param name="storedEntries">The entries as read back from the store.</param>
    public static ConfigDualReadResult Compare(
        JsonObject? source,
        IReadOnlyDictionary<string, ConfigEntry> storedEntries)
    {
        ArgumentNullException.ThrowIfNull(storedEntries);

        var rehydrated = ConfigDocumentRehydrator.Rehydrate(storedEntries);

        // Normalise the source through flatten/rehydrate too, rather than serialising it directly.
        // Otherwise this would compare a hand-formatted file against generated JSON and report key
        // ORDER as a parity failure - a difference in the document's spelling, not in its content.
        // Normalising both sides through the same canonicalisation keeps the check about what the
        // store preserved.
        var normalisedSource = ConfigDocumentRehydrator.Rehydrate(ConfigDocumentFlattener.Flatten(source));

        var sourceText = normalisedSource.ToJsonString();
        var storeText = rehydrated.ToJsonString();

        return new ConfigDualReadResult(
            Identical: string.Equals(sourceText, storeText, StringComparison.Ordinal),
            SourceJson: sourceText,
            StoreJson: storeText);
    }
}

/// <summary>The outcome of a dual read parity check.</summary>
/// <param name="Identical">Whether both sources produced byte-identical canonical JSON.</param>
/// <param name="SourceJson">Canonical JSON as derived from the configuration file.</param>
/// <param name="StoreJson">Canonical JSON as derived from the store.</param>
public readonly record struct ConfigDualReadResult(bool Identical, string SourceJson, string StoreJson);
