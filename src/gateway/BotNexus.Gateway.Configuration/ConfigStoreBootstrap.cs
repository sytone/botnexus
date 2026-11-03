using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Configuration;

/// <summary>Publishes a verified SQLite authority before runtime configuration is composed.</summary>
public static class ConfigStoreBootstrap
{
    /// <summary>The canonical authority file name.</summary>
    public const string StoreFileName = "config" + SqliteStorePathPolicy.CanonicalExtension;

    /// <summary>Resolves the canonical store, including the supported legacy filename migration.</summary>
    public static string ResolveStorePath(string configPath, IFileSystem fileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentNullException.ThrowIfNull(fileSystem);
        var directory = fileSystem.Path.GetDirectoryName(configPath);
        return string.IsNullOrEmpty(directory)
            ? StoreFileName
            : SqliteStorePathPolicy.ResolveOwnedStorePath(directory, "config", fileSystem);
    }

    /// <summary>
    /// Opens an existing authority, or imports the raw legacy document exactly once. An existing
    /// empty store is initialized authority, never a request to import JSON again.
    /// </summary>
    /// <remarks>
    /// Publication is a same-directory atomic rename of a complete, verified, checkpointed database.
    /// A crash before publication leaves only an unreferenced temporary directory; a crash afterwards
    /// leaves a complete authority, including when its document is empty. Cleanup is best effort:
    /// unreferenced residue must neither mask a primary failure nor invalidate publication.
    /// The mutation sidecar lock
    /// serializes bootstrap across processes as well as against ordinary writers.
    /// </remarks>
    public static async Task<IConfigStore> EnsureAuthorityAsync(
        string configPath,
        IFileSystem fileSystem,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentNullException.ThrowIfNull(fileSystem);
        using var gate = await CrossProcessConfigLock.AcquireAsync(configPath, fileSystem, cancellationToken)
            .ConfigureAwait(false);
        var storePath = ResolveStorePath(configPath, fileSystem);
        if (fileSystem.File.Exists(storePath))
        {
            ValidateExistingAuthority(storePath);
            var existing = OpenAuthority(storePath);
            // Fail closed on corrupt/unsupported stores, even when a valid projection is present.
            _ = ConfigDocumentRehydrator.Rehydrate(await existing.ReadEntriesAsync(cancellationToken).ConfigureAwait(false));
            return existing;
        }

        JsonObject document = new();
        if (fileSystem.File.Exists(configPath))
        {
            var text = await fileSystem.File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false);
            document = JsonNode.Parse(text) as JsonObject
                ?? throw new JsonException("Legacy configuration must be a JSON object.");
            var directory = fileSystem.Path.GetDirectoryName(configPath) ?? ".";
            var backups = new ConfigBackupService(fileSystem.Path.Combine(directory, "backups"), fileSystem);
            backups.Backup(configPath, "before-sqlite-import");
            var backup = backups.List().FirstOrDefault(entry => entry.Reason == "before-sqlite-import")
                ?? throw new IOException("Legacy configuration backup was not retained.");
            var backedUpText = await fileSystem.File.ReadAllTextAsync(backup.FullPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(text, backedUpText, StringComparison.Ordinal))
                throw new IOException("Legacy configuration changed during backup; authority was not published.");
        }

        // Preserve the basename: the shared identity guard derives store kind from it.
        var temporaryDirectory = fileSystem.Path.Combine(fileSystem.Path.GetDirectoryName(storePath) ?? ".",
            ".config-bootstrap-" + Guid.NewGuid().ToString("N"));
        fileSystem.Directory.CreateDirectory(temporaryDirectory);
        var temporaryPath = fileSystem.Path.Combine(temporaryDirectory, StoreFileName);
        try
        {
            var temporary = new SqliteConfigStore(new SqliteConnectionStringBuilder
            {
                DataSource = temporaryPath,
                Pooling = false
            }.ToString());
            await temporary.WriteDocumentAsync(document, cancellationToken).ConfigureAwait(false);
            var verified = ConfigDocumentRehydrator.Rehydrate(await temporary.ReadEntriesAsync(cancellationToken).ConfigureAwait(false));
            if (!JsonNode.DeepEquals(document, verified))
                throw new InvalidDataException("Legacy configuration failed SQLite round-trip verification.");

            // Fold committed WAL pages into the one file that is atomically published.
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = temporaryPath,
                Pooling = false,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString()))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                using var checkpoint = connection.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                using var result = await checkpoint.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await result.ReadAsync(cancellationToken).ConfigureAwait(false) || result.GetInt32(0) != 0)
                    throw new IOException("Configuration bootstrap checkpoint could not complete.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            fileSystem.File.Move(temporaryPath, storePath);
            return OpenAuthority(storePath);
        }
        finally
        {
            try
            {
                // This invocation owns the entire GUID-named directory, not just SQLite's
                // DB/WAL/SHM files. Guard validation also creates receipt/lock sidecars here.
                // Never scan/delete other bootstrap directories: they may belong to another
                // attempt, and no residue is accepted as a published authority on reopen.
                fileSystem.Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                // A failed cleanup leaves unreferenced residue. Preserve the primary exception
                // before publication, or the successful result after the atomic rename; callers
                // must not retry an already-published operation because residue could not be removed.
            }
        }
    }

    private static void ValidateExistingAuthority(string storePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = storePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        connection.ValidateReadOnly(SqliteConfigStore.CurrentSchemaVersion);
        using var integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA quick_check;";
        if (!string.Equals(integrity.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
            throw new InvalidDataException("Configuration authority failed integrity verification.");
        using var shape = connection.CreateCommand();
        shape.CommandText = "SELECT scope, scope_id, key_path, state, value FROM config_entries LIMIT 0;";
        using var reader = shape.ExecuteReader();
    }

    private static IConfigStore OpenAuthority(string storePath) => new SqliteConfigStore(
        new SqliteConnectionStringBuilder { DataSource = storePath, Mode = SqliteOpenMode.ReadWrite }.ToString());

    /// <summary>Explicit whole-document import; ordinary edits must use row change sets instead.</summary>
    public static async Task PopulateAsync(string storePath, JsonObject document, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);
        ArgumentNullException.ThrowIfNull(document);
        await new SqliteConfigStore($"Data Source={storePath}").WriteDocumentAsync(document, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Releases pooled handles for an explicit maintenance operation.</summary>
    public static void ReleaseConnections(string storePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);
        // Both explicit-import and read/write-only authority pools may have been used.
        using var connection = new SqliteConnection($"Data Source={storePath}");
        SqliteConnection.ClearPool(connection);
        using var authorityConnection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = storePath, Mode = SqliteOpenMode.ReadWrite }.ToString());
        SqliteConnection.ClearPool(authorityConnection);
    }

    /// <summary>Reports null for absent authority, or its entry count (including initialized empty).</summary>
    public static async Task<int?> CountEntriesAsync(string storePath, IFileSystem fileSystem, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);
        ArgumentNullException.ThrowIfNull(fileSystem);
        return fileSystem.File.Exists(storePath)
            ? (await OpenAuthority(storePath).ReadEntriesAsync(cancellationToken).ConfigureAwait(false)).Count
            : null;
    }
}
