using System.Collections.Concurrent;
using System.IO.Abstractions;
using Microsoft.Data.Sqlite;

namespace BotNexus.Persistence.Sqlite;

/// <summary>
/// Defines the filename policy for SQLite databases owned by BotNexus and migrates the bounded
/// legacy <c>.db</c> form before a writer opens the store.
/// </summary>
public static class SqliteStorePathPolicy
{
    /// <summary>The canonical extension for every BotNexus-owned SQLite database.</summary>
    public const string CanonicalExtension = ".sqlite";

    /// <summary>The temporary compatibility extension accepted for existing stores.</summary>
    public const string LegacyExtension = ".db";

    private static readonly string[] SidecarSuffixes = ["-wal", "-shm"];
    private static readonly ConcurrentDictionary<string, object> MigrationLocks =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves an owned store to its canonical path. A lone legacy database blocks this call while
    /// it is validated, migrated, and archived with its sidecars. If both active forms exist,
    /// neither is opened or modified.
    /// </summary>
    public static string ResolveOwnedStorePath(
        this string directory,
        string storeName,
        IFileSystem? fileSystem = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeName);

        if (storeName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
            || Path.GetFileName(storeName) != storeName)
        {
            throw new ArgumentException("Store name must be a file name without a directory.", nameof(storeName));
        }

        var bareName = StripKnownExtension(storeName);
        var fs = fileSystem ?? new FileSystem();
        var canonicalPath = fs.Path.Combine(directory, bareName + CanonicalExtension);
        var migrationLock = MigrationLocks.GetOrAdd(canonicalPath, static _ => new object());
        lock (migrationLock)
        {
            var canonicalMatches = FindMatches(fs, directory, bareName + CanonicalExtension);
            var legacyMatches = FindMatches(fs, directory, bareName + LegacyExtension);

            if (canonicalMatches.Count > 1 || legacyMatches.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Multiple case-variant SQLite stores exist for '{bareName}' in '{directory}'. " +
                    "BotNexus cannot start this store until the duplicate is removed.");
            }

            if (canonicalMatches.Count == 1 && legacyMatches.Count == 1)
            {
                throw new InvalidOperationException(
                    $"Both canonical SQLite store '{canonicalMatches[0]}' and legacy store '{legacyMatches[0]}' exist. " +
                    "BotNexus cannot safely migrate or open either store while both files are present.");
            }

            if (canonicalMatches.Count == 1)
                return canonicalMatches[0];

            if (legacyMatches.Count == 0)
                return canonicalPath;

            MigrateLegacyStore(fs, legacyMatches[0], canonicalPath, bareName);
            return canonicalPath;
        }
    }

    private static void MigrateLegacyStore(IFileSystem fs, string legacyPath, string canonicalPath, string storeKind)
    {
        ValidateDatabase(legacyPath, storeKind);

        var temporaryPath = canonicalPath + $".migrating-{Guid.NewGuid():N}";
        try
        {
            CopyDatabaseSnapshot(legacyPath, temporaryPath);
            ValidateDatabase(temporaryPath, storeKind);

            // The validated snapshot is promoted with one same-directory rename. SQLite's backup API
            // has already folded committed WAL pages into it, so the canonical store never depends on
            // a partially-renamed set of main/WAL/SHM files.
            fs.File.Move(temporaryPath, canonicalPath);

            ArchiveLegacyStore(fs, legacyPath, storeKind, canonicalPath);
        }
        catch (Exception migrationError)
        {
            if (fs.File.Exists(temporaryPath))
                fs.File.Delete(temporaryPath);

            throw new InvalidOperationException(
                $"SQLite store migration from '{legacyPath}' to '{canonicalPath}' failed. " +
                "The legacy database remains authoritative.",
                migrationError);
        }
    }

    private static void ArchiveLegacyStore(
        IFileSystem fs,
        string legacyPath,
        string storeKind,
        string canonicalPath)
    {
        var directory = fs.Path.GetDirectoryName(legacyPath)
            ?? throw new InvalidOperationException($"Legacy SQLite store '{legacyPath}' has no parent directory.");
        var archiveDirectory = fs.Path.Combine(
            directory,
            "sqlite-archive",
            $"{storeKind}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}");
        fs.Directory.CreateDirectory(archiveDirectory);

        var sources = new List<string> { legacyPath };
        foreach (var suffix in SidecarSuffixes)
        {
            var sidecar = FindSidecar(fs, legacyPath, suffix);
            if (sidecar is not null)
                sources.Add(sidecar);
        }

        var archived = new List<(string Source, string Destination)>();
        try
        {
            foreach (var source in sources)
            {
                var destination = fs.Path.Combine(archiveDirectory, fs.Path.GetFileName(source));
                fs.File.Move(source, destination);
                archived.Add((source, destination));
            }
        }
        catch
        {
            foreach (var move in archived.AsEnumerable().Reverse())
            {
                if (fs.File.Exists(move.Destination) && !fs.File.Exists(move.Source))
                    fs.File.Move(move.Destination, move.Source);
            }

            if (fs.File.Exists(canonicalPath) && fs.File.Exists(legacyPath))
                fs.File.Delete(canonicalPath);
            throw;
        }
    }

    private static void CopyDatabaseSnapshot(string sourcePath, string destinationPath)
    {
        var sourceBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        var destinationBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };

        using var source = new SqliteConnection(sourceBuilder.ToString());
        using var destination = new SqliteConnection(destinationBuilder.ToString());
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
    }

    private static void ValidateDatabase(string path, string expectedStoreKind)
    {
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();

            using (var integrity = connection.CreateCommand())
            {
                integrity.CommandText = "PRAGMA quick_check;";
                var result = integrity.ExecuteScalar() as string;
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"SQLite integrity check returned '{result ?? "no result"}'.");
            }

            ValidateIdentity(connection, path, expectedStoreKind);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"SQLite store '{path}' failed pre-migration validation and was not moved: {ex.Message}", ex);
        }
    }

    private static void ValidateIdentity(SqliteConnection connection, string path, string expectedStoreKind)
    {
        using var table = connection.CreateCommand();
        table.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        table.Parameters.AddWithValue("$name", SqliteStoreIdentity.TableName);
        if (table.ExecuteScalar() is null)
            return;

        var storedWorld = ReadMetadata(connection, SqliteStoreIdentity.WorldIdKey);
        var storedKind = ReadMetadata(connection, SqliteStoreIdentity.StoreKindKey);
        var identity = SqliteStoreIdentityGuard.Identity;

        if (identity is not null
            && !string.IsNullOrWhiteSpace(storedWorld)
            && !string.Equals(storedWorld, identity.WorldId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Store identity belongs to world '{storedWorld}', not '{identity.WorldId}'.");
        }

        if (!string.IsNullOrWhiteSpace(storedKind)
            && !string.Equals(storedKind, expectedStoreKind, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Store identity at '{path}' declares kind '{storedKind}', not '{expectedStoreKind}'.");
        }
    }

    private static string? ReadMetadata(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT value FROM {SqliteStoreIdentity.TableName} WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static IReadOnlyList<string> FindMatches(IFileSystem fs, string directory, string fileName)
    {
        if (!fs.Directory.Exists(directory))
            return [];

        return fs.Directory
            .EnumerateFiles(directory)
            .Where(path => string.Equals(fs.Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static string? FindSidecar(IFileSystem fs, string databasePath, string suffix)
    {
        var directory = fs.Path.GetDirectoryName(databasePath);
        if (string.IsNullOrEmpty(directory))
            return null;

        var fileName = fs.Path.GetFileName(databasePath) + suffix;
        return FindMatches(fs, directory, fileName).SingleOrDefault();
    }

    private static string StripKnownExtension(string storeName)
    {
        if (storeName.EndsWith(CanonicalExtension, StringComparison.OrdinalIgnoreCase))
            return storeName[..^CanonicalExtension.Length];
        if (storeName.EndsWith(LegacyExtension, StringComparison.OrdinalIgnoreCase))
            return storeName[..^LegacyExtension.Length];
        return storeName;
    }
}
