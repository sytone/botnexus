using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Configuration.Tests;

public sealed class ConfigStoreParityDiagnosticTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"botnexus-parity-{Guid.NewGuid():N}");
    private string ConfigPath => Path.Combine(_home, "config.json");
    private string StorePath => Path.Combine(_home, ConfigStoreBootstrap.StoreFileName);

    public ConfigStoreParityDiagnosticTests() => Directory.CreateDirectory(_home);

    private async Task SeedAsync(string sql, string? compatibility)
    {
        var document = JsonNode.Parse(sql).ShouldBeOfType<JsonObject>();
        await ConfigStoreBootstrap.PopulateAsync(StorePath, document);
        ConfigStoreBootstrap.ReleaseConnections(StorePath);
        if (compatibility is not null)
            await File.WriteAllTextAsync(ConfigPath, compatibility);
    }

    [Theory]
    [InlineData("{}", "{}")]
    [InlineData("{\"x\":null,\"y\":{},\"z\":[]}", "{\"z\":[],\"y\":{},\"x\":null}")]
    [InlineData("{\"x\":[{\"a\":1,\"b\":2},[],null]}", "{\"x\":[{\"b\":2,\"a\":1},[],null]}")]
    public async Task InspectAsync_EquivalentCompleteDocuments_IsClean(string sql, string compatibility)
    {
        await SeedAsync(sql, compatibility);
        var result = await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath);
        result.Status.ShouldBe(ConfigStoreParityStatus.Clean);
        result.SqlBackend.ShouldBe(ConfigParityBackendState.Readable);
        result.CompatibilityBackend.ShouldBe(ConfigParityBackendState.Readable);
        result.SqlOnlyCount.ShouldBe(0);
        result.CompatibilityOnlyCount.ShouldBe(0);
        result.ChangedCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("{\"x\":null}", "{}", 1, 0, 0)]
    [InlineData("{}", "{\"x\":null}", 0, 1, 0)]
    [InlineData("{\"x\":null}", "{\"x\":false}", 0, 0, 1)]
    [InlineData("{\"x\":{}}", "{}", 1, 0, 0)]
    [InlineData("{}", "{\"x\":[]}", 0, 1, 0)]
    [InlineData("{\"x\":{}}", "{\"x\":[]}", 0, 0, 1)]
    [InlineData("{\"x\":[1,2]}", "{\"x\":[2,1]}", 0, 0, 1)]
    [InlineData("{\"x\":[{\"a\":null}]}", "{\"x\":[{}]}", 0, 0, 1)]
    [InlineData("{\"extension\":{\"sql\":1,\"changed\":2}}", "{\"extension\":{\"file\":1,\"changed\":3}}", 1, 1, 1)]
    [InlineData("{\"literal.key\":1}", "{\"literal.key\":1}", 1, 1, 0)]
    public async Task InspectAsync_Drift_PreservesPresenceAndShape(string sql, string compatibility, int sqlOnly, int fileOnly, int changed)
    {
        await SeedAsync(sql, compatibility);
        var result = await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath);
        result.Status.ShouldBe(ConfigStoreParityStatus.Drift);
        result.SqlOnlyCount.ShouldBe(sqlOnly);
        result.CompatibilityOnlyCount.ShouldBe(fileOnly);
        result.ChangedCount.ShouldBe(changed);
    }

    [Fact]
    public async Task InspectAsync_MissingBackends_DoesNotCreateAnything()
    {
        var result = await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath);
        result.Status.ShouldBe(ConfigStoreParityStatus.Absent);
        Directory.EnumerateFileSystemEntries(_home).ShouldBeEmpty();
        await File.WriteAllTextAsync(ConfigPath, "{}");
        result = await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath);
        result.Status.ShouldBe(ConfigStoreParityStatus.Disabled);
        File.Exists(StorePath).ShouldBeFalse();
    }

    [Fact]
    public async Task InspectAsync_SqlOnly_IsNotCleanOrEmptyFallback()
    {
        await SeedAsync("{\"unknown\":{\"preserved\":null}}", null);
        var result = await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath);
        result.Status.ShouldBe(ConfigStoreParityStatus.SqlOnly);
        result.SqlBackend.ShouldBe(ConfigParityBackendState.Readable);
        result.CompatibilityBackend.ShouldBe(ConfigParityBackendState.Absent);
        File.Exists(ConfigPath).ShouldBeFalse();
    }

    [Theory]
    [InlineData("{not-json-secret")]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task InspectAsync_UnreadableCompatibility_IsNotClean(string raw)
    {
        await SeedAsync("{}", raw);
        var result = await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath);
        result.Status.ShouldBe(ConfigStoreParityStatus.Unreadable);
        result.CompatibilityBackend.ShouldBe(ConfigParityBackendState.Unreadable);
        JsonSerializer.Serialize(result).ShouldNotContain(raw);
    }

    [Fact]
    public async Task InspectAsync_CorruptSql_IsUnreadableAndLeavesBytesUnchanged()
    {
        await File.WriteAllTextAsync(StorePath, "not-a-database-secret");
        await File.WriteAllTextAsync(ConfigPath, "{}");
        var before = await File.ReadAllBytesAsync(StorePath);
        var result = await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath);
        result.Status.ShouldBe(ConfigStoreParityStatus.Unreadable);
        result.SqlBackend.ShouldBe(ConfigParityBackendState.Unreadable);
        (await File.ReadAllBytesAsync(StorePath)).ShouldBe(before);
        JsonSerializer.Serialize(result).ShouldNotContain("not-a-database-secret");
    }

    [Fact]
    public async Task InspectAsync_EmptyDatabase_DoesNotInitializeSchema()
    {
        using (File.Create(StorePath)) { }
        var result = await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath);
        result.Status.ShouldBe(ConfigStoreParityStatus.Unreadable);
        new FileInfo(StorePath).Length.ShouldBe(0);
    }

    [Fact]
    public async Task InspectAsync_ValidStore_DoesNotChangeDocumentsRevisionOrFileSet()
    {
        await SeedAsync("{\"secret-key-name\":\"secret-value\"}", "{\"secret-key-name\":\"other-secret\"}");
        var files = Directory.GetFiles(_home).Where(path => !path.EndsWith("-wal", StringComparison.Ordinal) && !path.EndsWith("-shm", StringComparison.Ordinal)).Order().ToArray();
        var before = files.ToDictionary(path => path, File.ReadAllBytes);
        var result = await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath);
        result.Status.ShouldBe(ConfigStoreParityStatus.Drift);
        Directory.GetFiles(_home).Where(path => !path.EndsWith("-wal", StringComparison.Ordinal) && !path.EndsWith("-shm", StringComparison.Ordinal)).Order().ShouldBe(files);
        foreach (var path in files)
            File.ReadAllBytes(path).ShouldBe(before[path]);
        var serialized = JsonSerializer.Serialize(result);
        serialized.ShouldNotContain("secret-key-name");
        serialized.ShouldNotContain("secret-value");
        serialized.ShouldNotContain("other-secret");
        serialized.ShouldNotContain(_home);
    }

    [Fact]
    public async Task InspectAsync_LegacyStore_DoesNotMigrateOrArchive()
    {
        await SeedAsync("{}", "{}");
        var legacyPath = Path.ChangeExtension(StorePath, SqliteStorePathPolicy.LegacyExtension);
        File.Move(StorePath, legacyPath);
        var result = await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath);
        result.Status.ShouldBe(ConfigStoreParityStatus.Clean);
        File.Exists(legacyPath).ShouldBeTrue();
        File.Exists(StorePath).ShouldBeFalse();
        Directory.GetDirectories(_home).ShouldBeEmpty();
    }

    [Fact]
    public async Task InspectAsync_AmbiguousStoreNames_FailsClosedWithoutMigration()
    {
        await SeedAsync("{}", "{}");
        var legacyPath = Path.ChangeExtension(StorePath, SqliteStorePathPolicy.LegacyExtension);
        File.Copy(StorePath, legacyPath);
        var result = await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath);
        result.Status.ShouldBe(ConfigStoreParityStatus.Unreadable);
        result.SqlBackend.ShouldBe(ConfigParityBackendState.Unreadable);
        File.Exists(legacyPath).ShouldBeTrue();
        File.Exists(StorePath).ShouldBeTrue();
    }

    [Theory]
    [InlineData("UPDATE config_entries SET state = 999;")]
    [InlineData("UPDATE config_entries SET value = 'invalid-secret-json';")]
    public async Task InspectAsync_InvalidStoredEntry_FailsClosed(string mutation)
    {
        await SeedAsync("{\"x\":1}", "{\"x\":1}");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = StorePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = mutation;
            await command.ExecuteNonQueryAsync();
        }
        var result = await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath);
        result.Status.ShouldBe(ConfigStoreParityStatus.Unreadable);
        result.SqlBackend.ShouldBe(ConfigParityBackendState.Unreadable);
        JsonSerializer.Serialize(result).ShouldNotContain("invalid-secret-json");
    }

    [Fact]
    public async Task InspectAsync_Cancelled_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => ConfigStoreParityDiagnostic.InspectAsync(ConfigPath, cancellation.Token));
    }

    public void Dispose()
    {
        ConfigStoreBootstrap.ReleaseConnections(StorePath);
        Directory.Delete(_home, recursive: true);
    }
}
