using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Creates and populates the SQLite configuration store from <c>config.json</c> (#3514).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The store had no writer. <c>SqliteConfigStore.WriteDocumentAsync</c> was
/// called only by the shadow migration hosted service, which #3510 deleted as dead - correctly, as a
/// verification harness, but it was also the only code path that ever created <c>config.db</c>.
/// With no writer the file never appeared, and the provider registration was gated on the file
/// existing, so the store could never be reached by any supported action.
/// </para>
/// <para>
/// <b>Why a deliberate operator action rather than automatic creation.</b> Creating the store on
/// every start would silently change which source is authoritative for an installation that never
/// asked for it: once the file exists the provider registers, and store values win over the file.
/// Enabling a different configuration backend is a decision, so it takes a command.
/// </para>
/// </remarks>
public static class ConfigStoreBootstrap
{
    /// <summary>The store's file name, beside <c>config.json</c>.</summary>
    public const string StoreFileName = "config.db";

    /// <summary>
    /// Resolves the store path for a given <c>config.json</c> path.
    /// </summary>
    public static string ResolveStorePath(string configPath, IFileSystem fileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentNullException.ThrowIfNull(fileSystem);

        var directory = fileSystem.Path.GetDirectoryName(configPath);
        return string.IsNullOrEmpty(directory)
            ? StoreFileName
            : fileSystem.Path.Combine(directory, StoreFileName);
    }

    /// <summary>
    /// Populates the store at <paramref name="storePath"/> from <paramref name="document"/>,
    /// creating the database if it does not exist.
    /// </summary>
    /// <remarks>
    /// The write is a wholesale replacement, matching <see cref="SqliteConfigStore"/>'s import
    /// semantics: the document is a snapshot, and merging would leave rows behind for keys the
    /// document no longer contains.
    /// </remarks>
    public static async Task PopulateAsync(
        string storePath,
        JsonObject document,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);
        ArgumentNullException.ThrowIfNull(document);

        var store = new SqliteConfigStore($"Data Source={storePath}");
        await store.WriteDocumentAsync(document, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases any pooled SQLite connections held against <paramref name="storePath"/>, so the file
    /// can be deleted or replaced.
    /// </summary>
    /// <remarks>
    /// Microsoft.Data.Sqlite pools connections by connection string, and a pooled connection keeps an
    /// OS handle on the database file after the last reader has been disposed. Without this, deleting
    /// <c>config.db</c> in a process that has already read it fails with "the process cannot access
    /// the file" - which is exactly what an operator running <c>config store disable</c> after
    /// <c>config store status</c> would hit.
    /// </remarks>
    public static void ReleaseConnections(string storePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={storePath}");
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
    }

    /// <summary>
    /// Reports how many entries the store at <paramref name="storePath"/> holds, or
    /// <see langword="null"/> when it does not exist.
    /// </summary>
    /// <remarks>
    /// Used to confirm a populate actually landed. A count of zero is a distinct and useful answer -
    /// it means the store exists but contributes no keys, which is exactly the state in which the
    /// file continues to serve every value.
    /// </remarks>
    public static async Task<int?> CountEntriesAsync(
        string storePath,
        IFileSystem fileSystem,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);
        ArgumentNullException.ThrowIfNull(fileSystem);

        if (!fileSystem.File.Exists(storePath))
            return null;

        var store = new SqliteConfigStore($"Data Source={storePath}");
        var entries = await store.ReadEntriesAsync(cancellationToken).ConfigureAwait(false);
        return entries.Count;
    }
}
