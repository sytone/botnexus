using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;

namespace BotNexus.Gateway.Configuration.Writers;

/// <summary>
/// Writes the configuration document to the SQLite store (#3527).
/// </summary>
/// <remarks>
/// Thin by design: <see cref="SqliteConfigStore.WriteDocumentAsync"/> already flattens the document,
/// preserves the tri-state that a nullable column would collapse, and replaces wholesale inside a
/// transaction. This adapts that to the writer contract and adds nothing - a second implementation
/// of the flattening rules is exactly the drift the single-store design exists to prevent.
/// </remarks>
public sealed class SqliteConfigurationWriter : IConfigurationWriter
{
    private readonly IConfigStore _store;

    /// <summary>Creates a writer over <paramref name="store"/>.</summary>
    public SqliteConfigurationWriter(IConfigStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public string Name => "sqlite";

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="reason"/> is unused: the store keeps no backup history, because rolling back
    /// is deleting <c>config.db</c> and the JSON file remains the durable copy.
    /// </remarks>
    public Task WriteAsync(JsonObject document, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        return _store.WriteDocumentAsync(document, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reads the store's own entries rather than re-reading the JSON file. The store is what this writer
    /// is about to modify, so diffing against anything else would compute a change set against a
    /// document that is not there - and during a JSON-to-SQLite transition the two legitimately differ
    /// until the first fan-out write reconciles them.
    /// </remarks>
    public async Task<ConfigChangeSet> ApplyAsync(
        object dto,
        string pathPrefix,
        string reason,
        ConfigDiffOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(pathPrefix);

        var entries = await _store.ReadEntriesAsync(cancellationToken).ConfigureAwait(false);
        var current = ConfigDocumentRehydrator.Rehydrate(entries);

        var changes = ConfigDtoDiffer.Diff(current, dto, pathPrefix, options);
        await _store.ApplyChangesAsync(changes, cancellationToken).ConfigureAwait(false);
        return changes;
    }
}
