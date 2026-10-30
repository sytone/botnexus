using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Gateway.Configuration.Writers;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Configuration.Tests;

public sealed class ConfigStoreReconciliationTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"botnexus-reconciliation-{Guid.NewGuid():N}");
    private string ConfigPath => Path.Combine(_home, "config.json");
    private string StorePath => Path.Combine(_home, ConfigStoreBootstrap.StoreFileName);

    public ConfigStoreReconciliationTests() => Directory.CreateDirectory(_home);

    private async Task<PlatformConfigWriter> SeedAsync(string sql, string? compatibility)
    {
        await ConfigStoreBootstrap.PopulateAsync(StorePath, JsonNode.Parse(sql).ShouldBeOfType<JsonObject>());
        ConfigStoreBootstrap.ReleaseConnections(StorePath);
        if (compatibility is not null)
            await File.WriteAllTextAsync(ConfigPath, compatibility);
        var writer = ConfigWriterFactory.Create(ConfigPath, new FileSystem());
        writer.Backends.ShouldContain(backend => backend.Name == "sqlite" && backend.Location == StorePath && backend.WinsOnRead);
        writer.Backends.ShouldContain(backend => backend.Name == "json" && backend.Location == ConfigPath);
        return writer;
    }

    [Theory]
    [InlineData("{\"extension\":{\"shared\":\"sql\",\"sqlOnly\":null}}", "{\"extension\":{\"shared\":\"file\",\"fileOnly\":null}}", "{\"extension\":{\"shared\":\"sql\",\"sqlOnly\":null,\"fileOnly\":null}}")]
    [InlineData("{}", "{\"unknown\":{\"retained\":null}}", "{\"unknown\":{\"retained\":null}}")]
    [InlineData("{\"unknown\":{\"retained\":null}}", null, "{\"unknown\":{\"retained\":null}}")]
    [InlineData("{\"extension\":{\"items\":[1,2],\"flag\":null}}", "{\"extension\":{\"items\":[3,4],\"flag\":true}}", "{\"extension\":{\"items\":[1,2],\"flag\":null}}")]
    public async Task ReconcileAsync_ExplicitApply_ConvergesCompleteUnion(string sql, string? compatibility, string expected)
    {
        var writer = await SeedAsync(sql, compatibility);
        var before = Directory.GetFiles(_home).Where(path => !path.EndsWith("-wal", StringComparison.Ordinal) && !path.EndsWith("-shm", StringComparison.Ordinal)).ToDictionary(path => path, File.ReadAllBytes);
        var preview = await PlatformConfigWriter.PreviewReconciliationAsync(ConfigPath);
        preview.CanApply.ShouldBeTrue();
        Directory.GetFiles(_home).Where(path => !path.EndsWith("-wal", StringComparison.Ordinal) && !path.EndsWith("-shm", StringComparison.Ordinal)).Order().ShouldBe(before.Keys.Order());
        Directory.GetDirectories(_home).ShouldBeEmpty();
        foreach (var (path, bytes) in before)
            File.ReadAllBytes(path).ShouldBe(bytes);
        (await writer.ApplyReconciliationAsync(preview)).ShouldBeEmpty();
        var expectedDocument = JsonNode.Parse(expected).ShouldBeOfType<JsonObject>();
        JsonNode.DeepEquals(JsonNode.Parse(await File.ReadAllTextAsync(ConfigPath)), expectedDocument).ShouldBeTrue();
        var entries = await new SqliteConfigStore(new SqliteConnectionStringBuilder { DataSource = StorePath }.ToString()).ReadEntriesAsync();
        JsonNode.DeepEquals(ConfigDocumentRehydrator.Rehydrate(entries), expectedDocument).ShouldBeTrue();
        (await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath)).Status.ShouldBe(ConfigStoreParityStatus.Clean);
    }

    [Theory]
    [InlineData("{\"x\":{\"a\":1}}", "{\"x\":null}")]
    [InlineData("{\"x\":null}", "{\"x\":{\"a\":1}}")]
    [InlineData("{\"x\":[1]}", "{\"x\":[1,2]}")]
    [InlineData("{\"x\":[{}]}", "{\"x\":[1]}")]
    [InlineData("{\"x\":[1]}", "{\"x\":true}")]
    [InlineData("{}", "{\"literal.key\":1}")]
    [InlineData("{}", "{\"x\":1,\"X\":2}")]
    [InlineData("{\"gateway\":{\"listenUrl\":\"not-a-url\"}}", "{}")]
    public async Task ReconcileAsync_UnsafeCandidate_RefusesWithoutWrites(string sql, string compatibility)
    {
        await SeedAsync(sql, compatibility);
        var before = Directory.GetFiles(_home).Where(path => !path.EndsWith("-wal", StringComparison.Ordinal) && !path.EndsWith("-shm", StringComparison.Ordinal)).ToDictionary(path => path, File.ReadAllBytes);
        var preview = await PlatformConfigWriter.PreviewReconciliationAsync(ConfigPath);
        preview.CanApply.ShouldBeFalse();
        foreach (var (path, bytes) in before)
            File.ReadAllBytes(path).ShouldBe(bytes);
        Directory.GetDirectories(_home).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApplyReconciliationAsync_EitherBeforeImageChanged_RefusesWithoutConfigurationWrite(bool sql)
    {
        var writer = await SeedAsync("{\"x\":1}", "{\"x\":2}");
        var preview = await PlatformConfigWriter.PreviewReconciliationAsync(ConfigPath);
        preview.CanApply.ShouldBeTrue();
        if (sql)
            await ConfigStoreBootstrap.PopulateAsync(StorePath, JsonNode.Parse("{\"x\":3}").ShouldBeOfType<JsonObject>());
        else
            await File.WriteAllTextAsync(ConfigPath, "{\"x\":3}");
        ConfigStoreBootstrap.ReleaseConnections(StorePath);
        var before = Directory.GetFiles(_home).Where(path => !path.EndsWith("-wal", StringComparison.Ordinal) && !path.EndsWith("-shm", StringComparison.Ordinal)).ToDictionary(path => path, File.ReadAllBytes);
        (await writer.ApplyReconciliationAsync(preview)).ShouldNotBeEmpty();
        foreach (var (path, bytes) in before)
            File.ReadAllBytes(path).ShouldBe(bytes);
        Directory.Exists(Path.Combine(_home, "backups")).ShouldBeFalse();
    }

    [Fact]
    public async Task PreviewReconciliationAsync_AbsentOrUninitializedStore_DoesNotCreateOrInitialize()
    {
        (await PlatformConfigWriter.PreviewReconciliationAsync(ConfigPath)).CanApply.ShouldBeFalse();
        Directory.EnumerateFileSystemEntries(_home).ShouldBeEmpty();
        await File.WriteAllTextAsync(ConfigPath, "{}");
        using (File.Create(StorePath)) { }
        (await PlatformConfigWriter.PreviewReconciliationAsync(ConfigPath)).CanApply.ShouldBeFalse();
        new FileInfo(StorePath).Length.ShouldBe(0);
        Directory.GetDirectories(_home).ShouldBeEmpty();
    }

    [Fact]
    public async Task PreviewReconciliationAsync_LegacyStore_RefusesWithoutMigration()
    {
        await SeedAsync("{}", "{}");
        var legacy = Path.ChangeExtension(StorePath, ".db");
        File.Move(StorePath, legacy);
        (await PlatformConfigWriter.PreviewReconciliationAsync(ConfigPath)).CanApply.ShouldBeFalse();
        File.Exists(StorePath).ShouldBeFalse();
        File.Exists(legacy).ShouldBeTrue();
        Directory.GetDirectories(_home).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("{\"x\":[]}", "{\"x\":[1]}")]
    [InlineData("{\"gateway\":{\"listenUrl\":\"http://localhost:5000\"}}", "{\"gateway\":null}")]
    public async Task PreviewReconciliationAsync_DestructiveShape_Refuses(string sql, string compatibility)
    {
        await SeedAsync(sql, compatibility);
        var before = File.ReadAllBytes(ConfigPath);
        (await PlatformConfigWriter.PreviewReconciliationAsync(ConfigPath)).CanApply.ShouldBeFalse();
        File.ReadAllBytes(ConfigPath).ShouldBe(before);
        Directory.GetDirectories(_home).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("UPDATE store_meta SET value = '1' WHERE key = 'schema_version';")]
    [InlineData("PRAGMA user_version = 1;")]
    [InlineData("DELETE FROM config_revision;")]
    [InlineData("UPDATE config_entries SET state = 0, value = NULL;")]
    [InlineData("UPDATE config_entries SET scope_id = 'unsupported';")]
    public async Task PreviewReconciliationAsync_UnsupportedStore_RefusesWithoutRepair(string mutation)
    {
        await SeedAsync("{\"x\":1}", "{}");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = StorePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = mutation;
            await command.ExecuteNonQueryAsync();
        }
        var before = Directory.GetFiles(_home).Where(path => !path.EndsWith("-wal", StringComparison.Ordinal) && !path.EndsWith("-shm", StringComparison.Ordinal)).ToDictionary(path => path, File.ReadAllBytes);
        (await PlatformConfigWriter.PreviewReconciliationAsync(ConfigPath)).CanApply.ShouldBeFalse();
        foreach (var (path, bytes) in before)
            File.ReadAllBytes(path).ShouldBe(bytes);
        Directory.GetDirectories(_home).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("not-json-private-payload")]
    [InlineData("[]")]
    [InlineData("{\"x\":1,\"x\":2}")]
    public async Task PreviewReconciliationAsync_UnreadableCompatibility_RefusesWithoutBackup(string compatibility)
    {
        await SeedAsync("{}", compatibility);
        var before = File.ReadAllBytes(ConfigPath);
        (await PlatformConfigWriter.PreviewReconciliationAsync(ConfigPath)).CanApply.ShouldBeFalse();
        File.ReadAllBytes(ConfigPath).ShouldBe(before);
        Directory.GetDirectories(_home).ShouldBeEmpty();
    }

    [Fact]
    public async Task ApplyReconciliationAsync_AlreadyClean_DoesNotWriteOrBackup()
    {
        var writer = await SeedAsync("{\"x\":null}", "{ \"x\": null }");
        var before = Directory.GetFiles(_home).Where(path => !path.EndsWith("-wal", StringComparison.Ordinal) && !path.EndsWith("-shm", StringComparison.Ordinal)).ToDictionary(path => path, File.ReadAllBytes);
        var preview = await PlatformConfigWriter.PreviewReconciliationAsync(ConfigPath);
        (await writer.ApplyReconciliationAsync(preview)).ShouldBeEmpty();
        foreach (var (path, bytes) in before)
            File.ReadAllBytes(path).ShouldBe(bytes);
        Directory.Exists(Path.Combine(_home, "backups")).ShouldBeFalse();
    }

    [Fact]
    public async Task ApplyReconciliationAsync_PartialBackendFailure_ThrowsAndDoesNotClaimSuccess()
    {
        await SeedAsync("{\"x\":1}", "{\"x\":2,\"fileOnly\":null}");
        var preview = await PlatformConfigWriter.PreviewReconciliationAsync(ConfigPath);
        preview.CanApply.ShouldBeTrue();
        var backend = new FanOutConfigurationWriter([
            new JsonConfigurationWriter(ConfigPath, new FileSystem()),
            new FailingSqlWriter(StorePath)
        ]);
        var writer = new PlatformConfigWriter(ConfigPath, new FileSystem(), null, backend);
        var failure = await Should.ThrowAsync<AggregateException>(() => writer.ApplyReconciliationAsync(preview));
        failure.Message.ShouldContain("Persisted to: json");
        (await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath)).Status.ShouldBe(ConfigStoreParityStatus.Drift);
    }

    [Fact]
    public async Task ApplyReconciliationAsync_AbsentCompatibilityCreatedAfterPreview_Refuses()
    {
        var writer = await SeedAsync("{\"x\":1}", null);
        var preview = await PlatformConfigWriter.PreviewReconciliationAsync(ConfigPath);
        await File.WriteAllTextAsync(ConfigPath, "{\"x\":2}");
        var before = File.ReadAllBytes(ConfigPath);
        (await writer.ApplyReconciliationAsync(preview)).ShouldNotBeEmpty();
        File.ReadAllBytes(ConfigPath).ShouldBe(before);
        Directory.Exists(Path.Combine(_home, "backups")).ShouldBeFalse();
    }

    [Fact]
    public async Task ApplyReconciliationAsync_StoreDeletedAfterPreview_DoesNotRecreate()
    {
        var writer = await SeedAsync("{\"x\":1}", "{\"x\":2}");
        var preview = await PlatformConfigWriter.PreviewReconciliationAsync(ConfigPath);
        File.Delete(StorePath);
        var before = File.ReadAllBytes(ConfigPath);
        (await writer.ApplyReconciliationAsync(preview)).ShouldNotBeEmpty();
        File.Exists(StorePath).ShouldBeFalse();
        File.ReadAllBytes(ConfigPath).ShouldBe(before);
        Directory.Exists(Path.Combine(_home, "backups")).ShouldBeFalse();
    }

    [Fact]
    public async Task ApplyReconciliationAsync_UnsafePreview_DoesNotAcquireLockOrWrite()
    {
        var writer = await SeedAsync("{\"x\":1}", "{\"x\":{\"a\":1}}");
        var preview = await PlatformConfigWriter.PreviewReconciliationAsync(ConfigPath);
        preview.CanApply.ShouldBeFalse();
        (await writer.ApplyReconciliationAsync(preview)).ShouldNotBeEmpty();
        Directory.GetDirectories(_home).ShouldBeEmpty();
    }

    [Fact]
    public async Task PreviewReconciliationAsync_AmbiguousStoreNamesOrCorruptSql_RefusesWithoutWrites()
    {
        await SeedAsync("{}", "{}");
        var legacy = Path.ChangeExtension(StorePath, ".db");
        File.Copy(StorePath, legacy);
        var before = File.ReadAllBytes(StorePath);
        (await PlatformConfigWriter.PreviewReconciliationAsync(ConfigPath)).CanApply.ShouldBeFalse();
        File.ReadAllBytes(StorePath).ShouldBe(before);
        File.Delete(legacy);
        await File.WriteAllTextAsync(StorePath, "corrupt-private-payload");
        (await PlatformConfigWriter.PreviewReconciliationAsync(ConfigPath)).CanApply.ShouldBeFalse();
        (await File.ReadAllTextAsync(StorePath)).ShouldBe("corrupt-private-payload");
        Directory.GetDirectories(_home).ShouldBeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_ExactCapacityMapIds_PreservesUnequalCaseAndSeparators()
    {
        var writer = await SeedAsync("""
            {"providers":{"test":{"chat":{"modelCapacities":{"Model.1":{"contextWindow":10000},"vendor:model":{"contextWindow":30000}}}}}}
            """, """
            {"providers":{"test":{"chat":{"modelCapacities":{"model.1":{"contextWindow":20000},"vendor:model":{"contextWindow":99999}}}}}}
            """);
        var preview = await PlatformConfigWriter.PreviewReconciliationAsync(ConfigPath);
        preview.CanApply.ShouldBeTrue();
        (await writer.ApplyReconciliationAsync(preview)).ShouldBeEmpty();
        var root = JsonNode.Parse(await File.ReadAllTextAsync(ConfigPath)).ShouldBeOfType<JsonObject>();
        var map = root["providers"].ShouldBeOfType<JsonObject>()["test"].ShouldBeOfType<JsonObject>()["chat"].ShouldBeOfType<JsonObject>()["modelCapacities"].ShouldBeOfType<JsonObject>();
        map.Count.ShouldBe(3);
        map["Model.1"].ShouldBeOfType<JsonObject>()["contextWindow"].ShouldBeAssignableTo<JsonValue>().ShouldNotBeNull().GetValue<int>().ShouldBe(10000);
        map["model.1"].ShouldBeOfType<JsonObject>()["contextWindow"].ShouldBeAssignableTo<JsonValue>().ShouldNotBeNull().GetValue<int>().ShouldBe(20000);
        map["vendor:model"].ShouldBeOfType<JsonObject>()["contextWindow"].ShouldBeAssignableTo<JsonValue>().ShouldNotBeNull().GetValue<int>().ShouldBe(30000);
        (await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath)).Status.ShouldBe(ConfigStoreParityStatus.Clean);
    }

    [Theory]
    [InlineData("{\"gateway\":{\"listenUrl\":\"http://localhost:5000\",\"ListenUrl\":\"http://localhost:6000\"}}")]
    [InlineData("{\"providers\":{\"test\":{\"chat\":{\"modelCapacities\":{\"Model.1\":{\"contextWindow\":10000,\"ContextWindow\":20000}}}}}}")]
    public async Task PreviewReconciliationAsync_OrdinaryBinderAliases_StillRefuses(string compatibility)
    {
        await SeedAsync("{}", compatibility);
        (await PlatformConfigWriter.PreviewReconciliationAsync(ConfigPath)).CanApply.ShouldBeFalse();
        Directory.GetDirectories(_home).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(5000, true)]
    [InlineData(5001, false)]
    [InlineData(6000, false)]
    public async Task PreviewReconciliationAsync_UnionRowLimit_RefusesBeyondThreshold(int compatibilityCount, bool accepted)
    {
        var sql = new JsonObject();
        var compatibility = new JsonObject();
        var sqlCount = compatibilityCount == 6000 ? 6000 : 5000;
        for (var index = 0; index < sqlCount; index++)
            sql["sql" + index] = index;
        for (var index = 0; index < compatibilityCount; index++)
            compatibility["file" + index] = index;
        var writer = await SeedAsync(sql.ToJsonString(), compatibility.ToJsonString());
        var before = File.ReadAllBytes(ConfigPath);
        var preview = await PlatformConfigWriter.PreviewReconciliationAsync(ConfigPath);
        preview.CanApply.ShouldBe(accepted);
        if (!accepted)
        {
            (await writer.ApplyReconciliationAsync(preview)).ShouldNotBeEmpty();
            File.ReadAllBytes(ConfigPath).ShouldBe(before);
            Directory.GetDirectories(_home).ShouldBeEmpty();
        }
        else
        {
            (await writer.ApplyReconciliationAsync(preview)).ShouldBeEmpty();
            (await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath)).Status.ShouldBe(ConfigStoreParityStatus.Clean);
        }
    }

    [Fact]
    public async Task PreviewReconciliationAsync_IndentedPersistedBytesExceedLimit_RefusesBeforeWrites()
    {
        var compatibility = new JsonObject();
        for (var index = 0; index < 9000; index++)
            compatibility["key" + index] = new string('a', 450);
        System.Text.Encoding.UTF8.GetByteCount(compatibility.ToJsonString()).ShouldBeLessThan(ConfigStoreParityDiagnostic.MaximumDocumentBytes);
        System.Text.Encoding.UTF8.GetByteCount(compatibility.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }))
            .ShouldBeGreaterThan(ConfigStoreParityDiagnostic.MaximumDocumentBytes);
        var writer = await SeedAsync("{}", compatibility.ToJsonString());
        var before = File.ReadAllBytes(ConfigPath);
        var preview = await PlatformConfigWriter.PreviewReconciliationAsync(ConfigPath);
        preview.CanApply.ShouldBeFalse();
        (await writer.ApplyReconciliationAsync(preview)).ShouldNotBeEmpty();
        File.ReadAllBytes(ConfigPath).ShouldBe(before);
        Directory.GetDirectories(_home).ShouldBeEmpty();
    }

    [Fact]
    public async Task PreviewReconciliationAsync_UnionAggregateBytesExceedLimit_Refuses()
    {
        var payload = new string('a', ConfigStoreParityDiagnostic.MaximumDocumentBytes / 2);
        var writer = await SeedAsync(new JsonObject { ["sql"] = payload }.ToJsonString(),
            new JsonObject { ["file"] = payload }.ToJsonString());
        var preview = await PlatformConfigWriter.PreviewReconciliationAsync(ConfigPath);
        preview.CanApply.ShouldBeFalse();
        (await writer.ApplyReconciliationAsync(preview)).ShouldNotBeEmpty();
        Directory.GetDirectories(_home).ShouldBeEmpty();
    }

    [Fact]
    public async Task PreviewReconciliationAsync_UnionNodeBudget_RefusesBeforeWrites()
    {
        var sqlArray = new JsonArray();
        var compatibilityArray = new JsonArray();
        for (var index = 0; index < 60_000; index++)
        {
            sqlArray.Add(index);
            compatibilityArray.Add(index);
        }
        var writer = await SeedAsync(new JsonObject { ["sql"] = sqlArray }.ToJsonString(),
            new JsonObject { ["file"] = compatibilityArray }.ToJsonString());
        var before = File.ReadAllBytes(ConfigPath);
        var preview = await PlatformConfigWriter.PreviewReconciliationAsync(ConfigPath);
        preview.CanApply.ShouldBeFalse();
        (await writer.ApplyReconciliationAsync(preview)).ShouldNotBeEmpty();
        File.ReadAllBytes(ConfigPath).ShouldBe(before);
        Directory.GetDirectories(_home).ShouldBeEmpty();
    }

    [Fact]
    public async Task ApplyReconciliationAsync_NormalizedCaseEquivalentTarget_FollowsOperatingSystemRules()
    {
        await SeedAsync("{\"x\":1}", "{\"x\":2}");
        var preview = await PlatformConfigWriter.PreviewReconciliationAsync(ConfigPath);
        var alternate = Path.Combine(_home, ".", "CONFIG.JSON");
        var backend = new FanOutConfigurationWriter([
            new JsonConfigurationWriter(alternate, new FileSystem()),
            new SqliteConfigurationWriter(new SqliteConfigStore(new SqliteConnectionStringBuilder { DataSource = StorePath }.ToString()), StorePath.ToUpperInvariant())
        ]);
        var writer = new PlatformConfigWriter(alternate, new FileSystem(), null, backend);
        var errors = await writer.ApplyReconciliationAsync(preview);
        if (OperatingSystem.IsWindows())
        {
            errors.ShouldBeEmpty();
            (await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath)).Status.ShouldBe(ConfigStoreParityStatus.Clean);
        }
        else
        {
            errors.ShouldNotBeEmpty();
            Directory.GetDirectories(_home).ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyReconciliationAsync_WrongLocationOrJsonOnly_RefusesWithoutWrites(bool jsonOnly)
    {
        await SeedAsync("{\"x\":1}", "{\"x\":2}");
        var preview = await PlatformConfigWriter.PreviewReconciliationAsync(ConfigPath);
        IConfigurationWriter backend = new JsonConfigurationWriter(ConfigPath, new FileSystem());
        if (!jsonOnly)
            backend = new FanOutConfigurationWriter([backend,
                new SqliteConfigurationWriter(new SqliteConfigStore(new SqliteConnectionStringBuilder { DataSource = StorePath }.ToString()), Path.Combine(_home, "other.sqlite"))]);
        var writer = new PlatformConfigWriter(ConfigPath, new FileSystem(), null, backend);
        var before = File.ReadAllBytes(ConfigPath);
        (await writer.ApplyReconciliationAsync(preview)).ShouldNotBeEmpty();
        File.ReadAllBytes(ConfigPath).ShouldBe(before);
        Directory.GetDirectories(_home).ShouldBeEmpty();
    }

    private sealed class FailingSqlWriter(string location) : IConfigurationWriter
    {
        public string Name => "sqlite";
        public IReadOnlyList<ConfigurationBackendDescriptor> Backends => [new(Name, location, WinsOnRead: true)];
        public Task WriteAsync(JsonObject document, string reason, CancellationToken cancellationToken = default)
            => throw new IOException("synthetic SQL persistence failure");
        public Task ApplyChangeSetAsync(ConfigChangeSet changes, string reason, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Reconciliation must not use the ordinary edit delta.");
    }

    public void Dispose()
    {
        ConfigStoreBootstrap.ReleaseConnections(StorePath);
        Directory.Delete(_home, recursive: true);
    }
}
