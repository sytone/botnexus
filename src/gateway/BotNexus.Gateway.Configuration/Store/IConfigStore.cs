using System.Text.Json.Nodes;

namespace BotNexus.Gateway.Configuration.Store;

/// <summary>
/// Persists a configuration document and reads it back as flattened entries (#2646 PBI 1).
///
/// <para>
/// <b>Reads return <see cref="ConfigEntry"/> rather than a reconstructed document.</b> That is the
/// shape #2766's <c>ConfigShadowDiff.CompareEntries</c> consumes, and it is the only shape able to
/// report <see cref="ConfigValueState.Unset"/> - JSON cannot express "present and unset", so a
/// document-shaped read would silently discard the distinction the store exists to preserve. Returning
/// entries keeps the store honest about its own states rather than laundering them through a format
/// that cannot represent them.
/// </para>
/// </summary>
public interface IConfigStore
{
    /// <summary>Reads every stored entry, keyed by canonical dotted path.</summary>
    Task<IReadOnlyDictionary<string, ConfigEntry>> ReadEntriesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the stored configuration with <paramref name="document"/>.
    ///
    /// <para>
    /// Wholesale replacement rather than merge: an import is a snapshot, and merging would leave rows
    /// behind for keys the document no longer contains.
    /// </para>
    /// </summary>
    Task WriteDocumentAsync(JsonObject document, CancellationToken cancellationToken = default);
}

