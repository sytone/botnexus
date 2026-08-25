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

    /// <summary>
    /// Applies only the keys named in <paramref name="changes"/>, leaving every other row untouched (#3532).
    ///
    /// <para>
    /// <b>Why this exists alongside <see cref="WriteDocumentAsync"/>.</b> The document write is honest
    /// for an import - a snapshot genuinely replaces everything. It is dishonest for an edit, because it
    /// cannot distinguish "unchanged" from "not supplied" and so rewrites ~200-400 rows to change one
    /// field. Worse, any key the caller's DTO does not model is absent from the snapshot and is deleted:
    /// that is #2816, where a <c>channels</c> write carrying one field destroyed the Service Bus settings
    /// and two bot tokens beneath it.
    /// </para>
    ///
    /// <para>
    /// Both remain because they answer different questions. An importer replacing the world should keep
    /// using <see cref="WriteDocumentAsync"/>; anything editing a subtree must use this.
    /// </para>
    /// </summary>
    /// <param name="changes">The keys to upsert and remove, scoped to a path prefix.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ApplyChangesAsync(ConfigChangeSet changes, CancellationToken cancellationToken = default);
}

