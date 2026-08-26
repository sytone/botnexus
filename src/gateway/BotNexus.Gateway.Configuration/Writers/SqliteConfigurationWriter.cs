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
    /// Delegates straight to the store, which applies one statement per named key. No document is
    /// materialised: the store is row-shaped already, so rebuilding a document here only to flatten it
    /// again would be the whole-document write wearing a different hat.
    /// </remarks>
    public Task ApplyChangeSetAsync(
        ConfigChangeSet changes,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return _store.ApplyChangesAsync(changes, cancellationToken);
    }
}
