using Microsoft.Data.Sqlite;
using Shouldly;

namespace BotNexus.Persistence.Sqlite.Tests;

public sealed class SqliteStorePathPolicyTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"botnexus-sqlite-path-policy-{Guid.NewGuid():N}");

    public SqliteStorePathPolicyTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void ResolveOwnedStorePath_NewStore_UsesCanonicalExtension()
    {
        var path = SqliteStorePathPolicy.ResolveOwnedStorePath(_directory, "sample");

        path.ShouldBe(Path.Combine(_directory, "sample.sqlite"));
        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public void ResolveOwnedStorePath_LegacyOnly_BlocksUntilDatabaseAndSidecarsAreArchived()
    {
        var legacyPath = Path.Combine(_directory, "sample.db");
        CreateDatabase(legacyPath);
        File.WriteAllBytes(legacyPath + "-wal", []);
        File.WriteAllBytes(legacyPath + "-shm", []);

        var path = SqliteStorePathPolicy.ResolveOwnedStorePath(_directory, "sample");

        path.ShouldBe(Path.Combine(_directory, "sample.sqlite"));
        File.Exists(path).ShouldBeTrue();
        ReadValue(path).ShouldBe("preserved");
        File.Exists(legacyPath).ShouldBeFalse();
        File.Exists(legacyPath + "-wal").ShouldBeFalse();
        File.Exists(legacyPath + "-shm").ShouldBeFalse();

        var archiveDirectory = Directory.GetDirectories(
            Path.Combine(_directory, "sqlite-archive"),
            "sample-*",
            SearchOption.TopDirectoryOnly).ShouldHaveSingleItem();
        var archivedLegacy = Path.Combine(archiveDirectory, "sample.db");
        File.Exists(archivedLegacy).ShouldBeTrue();
        File.Exists(archivedLegacy + "-wal").ShouldBeTrue();
        File.Exists(archivedLegacy + "-shm").ShouldBeTrue();
        ReadValue(archivedLegacy).ShouldBe("preserved");
    }

    [Fact]
    public void ResolveOwnedStorePath_BothFilesExist_RefusesToChoose()
    {
        var legacyPath = Path.Combine(_directory, "sample.db");
        var canonicalPath = Path.Combine(_directory, "sample.sqlite");
        CreateDatabase(legacyPath);
        CreateDatabase(canonicalPath);

        var exception = Should.Throw<InvalidOperationException>(() =>
            SqliteStorePathPolicy.ResolveOwnedStorePath(_directory, "sample"));

        exception.Message.ShouldContain(canonicalPath);
        exception.Message.ShouldContain(legacyPath);
        File.Exists(canonicalPath).ShouldBeTrue();
        File.Exists(legacyPath).ShouldBeTrue();
    }

    [Fact]
    public void ResolveOwnedStorePath_InvalidLegacyDatabase_LeavesOriginalIntact()
    {
        var legacyPath = Path.Combine(_directory, "sample.db");
        File.WriteAllText(legacyPath, "not a SQLite database");

        Should.Throw<InvalidOperationException>(() =>
            SqliteStorePathPolicy.ResolveOwnedStorePath(_directory, "sample"));

        File.ReadAllText(legacyPath).ShouldBe("not a SQLite database");
        File.Exists(Path.Combine(_directory, "sample.sqlite")).ShouldBeFalse();
    }

    [Fact]
    public void ResolveOwnedStorePath_LegacyExtensionMatchingIsCaseInsensitive()
    {
        var legacyPath = Path.Combine(_directory, "sample.DB");
        CreateDatabase(legacyPath);

        var path = SqliteStorePathPolicy.ResolveOwnedStorePath(_directory, "sample");

        path.ShouldBe(Path.Combine(_directory, "sample.sqlite"));
        File.Exists(path).ShouldBeTrue();
        File.Exists(legacyPath).ShouldBeFalse();
    }

    [Fact]
    public void ResolveOwnedStorePath_AfterMigration_IsIdempotent()
    {
        var legacyPath = Path.Combine(_directory, "sample.db");
        CreateDatabase(legacyPath);

        var first = SqliteStorePathPolicy.ResolveOwnedStorePath(_directory, "sample");
        var second = SqliteStorePathPolicy.ResolveOwnedStorePath(_directory, "sample");

        second.ShouldBe(first);
        ReadValue(second).ShouldBe("preserved");
    }

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolsUnder(_directory);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static void CreateDatabase(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE payload(value TEXT NOT NULL); INSERT INTO payload VALUES ('preserved');";
        command.ExecuteNonQuery();
    }

    private static string ReadValue(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM payload LIMIT 1;";
        return (string)(command.ExecuteScalar() ?? throw new InvalidOperationException("Missing test payload."));
    }
}
