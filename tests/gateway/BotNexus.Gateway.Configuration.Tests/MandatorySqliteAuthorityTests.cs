using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Gateway.Configuration.Writers;
using Microsoft.Extensions.Configuration;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>Production-boundary regressions for the mandatory SQLite authority contract.</summary>
public sealed class MandatorySqliteAuthorityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"bn-authority-{Guid.NewGuid():N}");
    private readonly IFileSystem _fileSystem = new FileSystem();
    private string ConfigPath => Path.Combine(_directory, "config.json");
    private string StorePath => ConfigStoreBootstrap.ResolveStorePath(ConfigPath, _fileSystem);
    private const string Seed = """
        {"version":1,"gateway":{"listenUrl":"http://localhost:5000"},
         "unknown":{"items":[1,null,"two"],"explicitNull":null,"empty":{}},
         "channels":{"telegram":{"botToken":"canonical-secret"}}}
        """;

    public MandatorySqliteAuthorityTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("{\"projectionOnly\":true,\"gateway\":{\"listenUrl\":\"http://localhost:9999\"}}")]
    [InlineData("not-json")]
    [InlineData(null)]
    public async Task ExistingStore_ReadPipelineNeverConsultsProjection(string? projection)
    {
        await ConfigStoreBootstrap.PopulateAsync(StorePath, Parse(Seed));
        if (projection is not null)
            await File.WriteAllTextAsync(ConfigPath, projection);

        using var configuration = new ConfigurationBuilder().AddPlatformConfiguration(ConfigPath).Build()
            .ShouldBeOfType<ConfigurationRoot>();

        configuration.Providers.ShouldHaveSingleItem().ShouldBeOfType<SqliteConfigurationProvider>();
        configuration["gateway:listenUrl"].ShouldBe("http://localhost:5000");
        configuration["projectionOnly"].ShouldBeNull();
        File.Exists(ConfigPath).ShouldBe(projection is not null);
    }

    [Fact]
    public async Task EmptyAuthoritativeStore_DoesNotResurrectProjectionKeys()
    {
        await ConfigStoreBootstrap.PopulateAsync(StorePath, new JsonObject());
        await File.WriteAllTextAsync(ConfigPath, Seed);
        var writer = ConfigWriterFactory.Create(ConfigPath, _fileSystem);

        (await writer.ReadAsync()).Count.ShouldBe(0);
        using var configuration = new ConfigurationBuilder().AddPlatformConfiguration(ConfigPath).Build()
            .ShouldBeOfType<ConfigurationRoot>();
        configuration["gateway:listenUrl"].ShouldBeNull();
    }

    [Fact]
    public async Task MissingProjection_MutationCommitsOnlyToSqliteAndDoesNotCreateJson()
    {
        await ConfigStoreBootstrap.PopulateAsync(StorePath, Parse(Seed));
        var store = new SqliteConfigStore($"Data Source={StorePath}");
        var before = await store.ReadEntriesAsync();
        var writer = ConfigWriterFactory.Create(ConfigPath, _fileSystem);
        writer.Backends.ShouldContain(backend => backend.Name == "sqlite" && backend.Location == StorePath && backend.WinsOnRead);

        await writer.MutateAsync(root => root["gateway"].ShouldBeOfType<JsonObject>()["listenUrl"] = "http://localhost:6000", "authority-test");

        File.Exists(ConfigPath).ShouldBeFalse();
        var after = await store.ReadEntriesAsync();
        after.Keys.Order().ShouldBe(before.Keys.Order());
        foreach (var (key, entry) in before)
            if (key == "gateway.listenUrl")
                after[key].Value.ShouldBe("\"http://localhost:6000\"");
            else
                after[key].ShouldBe(entry);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"projectionOnly\":true}")]
    public async Task ExistingProjection_IsOutputOnlyFullCanonicalSnapshot(string projection)
    {
        await ConfigStoreBootstrap.PopulateAsync(StorePath, Parse(Seed));
        await File.WriteAllTextAsync(ConfigPath, projection);
        var writer = ConfigWriterFactory.Create(ConfigPath, _fileSystem);

        await writer.MutateAsync(root => root["gateway"].ShouldBeOfType<JsonObject>()["listenUrl"] = "http://localhost:6000", "authority-test");

        var canonical = await writer.ReadAsync();
        JsonNode.DeepEquals(Parse(await File.ReadAllTextAsync(ConfigPath)), canonical).ShouldBeTrue();
        canonical["unknown"].ShouldBeOfType<JsonObject>().ContainsKey("explicitNull").ShouldBeTrue();
        canonical.ContainsKey("projectionOnly").ShouldBeFalse();
    }

    [Fact]
    public async Task ProjectionFailure_DoesNotReportCommittedMutationAsFailed()
    {
        await ConfigStoreBootstrap.PopulateAsync(StorePath, Parse(Seed));
        await File.WriteAllTextAsync(ConfigPath, "not-json");
        await File.WriteAllTextAsync(Path.Combine(_directory, "backups"), "blocked-directory");
        var writer = ConfigWriterFactory.Create(ConfigPath, _fileSystem);

        var outcome = await writer.MutateWithResultAsync(root => root["version"] = 2, "projection-failure");

        (await writer.ReadAsync())["version"].ShouldNotBeNull().GetValue<int>().ShouldBe(2);
        outcome.Warnings.ShouldHaveSingleItem().Code.ShouldBe("configuration-projection-refresh-failed");
        (await File.ReadAllTextAsync(ConfigPath)).ShouldBe("not-json");
    }

    [Fact]
    public async Task CorruptStore_StartupAndMutationFailClosedWithoutJsonFallback()
    {
        await File.WriteAllTextAsync(StorePath, "not-a-database");
        await File.WriteAllTextAsync(ConfigPath, Seed);
        var mutationCalled = false;

        Should.Throw<Exception>(() => new ConfigurationBuilder().AddPlatformConfiguration(ConfigPath).Build());
        await Should.ThrowAsync<Exception>(async () =>
        {
            var writer = ConfigWriterFactory.Create(ConfigPath, _fileSystem);
            await writer.MutateAsync(root => { mutationCalled = true; root["version"] = 2; }, "store-failure");
        });

        mutationCalled.ShouldBeFalse();
        (await File.ReadAllTextAsync(ConfigPath)).ShouldBe(Seed);
    }

    [Fact]
    public async Task LegacyImport_CreatesVerifiedStoreAndRetainsExactBackup()
    {
        await File.WriteAllTextAsync(ConfigPath, Seed);
        using var configuration = new ConfigurationBuilder().AddPlatformConfiguration(ConfigPath).Build()
            .ShouldBeOfType<ConfigurationRoot>();

        File.Exists(StorePath).ShouldBeTrue();
        configuration.Providers.ShouldHaveSingleItem().ShouldBeOfType<SqliteConfigurationProvider>();
        var canonical = ConfigDocumentRehydrator.Rehydrate(await new SqliteConfigStore($"Data Source={StorePath}").ReadEntriesAsync());
        JsonNode.DeepEquals(canonical, Parse(Seed)).ShouldBeTrue();
        var backup = new ConfigBackupService(Path.Combine(_directory, "backups"), _fileSystem).List().ShouldHaveSingleItem();
        (await File.ReadAllTextAsync(backup.FullPath)).ShouldBe(Seed);
    }

    [Fact]
    public async Task FreshWriter_CreatesSqliteWithoutCreatingProjection()
    {
        var writer = ConfigWriterFactory.Create(ConfigPath, _fileSystem);
        await writer.MutateAsync(root => root["version"] = 1, "fresh-init");

        File.Exists(StorePath).ShouldBeTrue();
        File.Exists(ConfigPath).ShouldBeFalse();
        (await new SqliteConfigStore($"Data Source={StorePath}").ReadEntriesAsync())["version"].Value.ShouldBe("1");
    }

    [Fact]
    public async Task RepeatedBootstrap_DoesNotReimportChangedProjectionOrRefillEmptyAuthority()
    {
        await File.WriteAllTextAsync(ConfigPath, Seed);
        using (var first = new ConfigurationBuilder().AddPlatformConfiguration(ConfigPath).Build()
            .ShouldBeOfType<ConfigurationRoot>())
            first["version"].ShouldBe("1");
        await ConfigStoreBootstrap.PopulateAsync(StorePath, new JsonObject());
        await File.WriteAllTextAsync(ConfigPath, "{\"version\":99}");
        using var second = new ConfigurationBuilder().AddPlatformConfiguration(ConfigPath).Build()
            .ShouldBeOfType<ConfigurationRoot>();
        second["version"].ShouldBeNull();
        (await ConfigWriterFactory.Create(ConfigPath, _fileSystem).ReadAsync()).Count.ShouldBe(0);
        new ConfigBackupService(Path.Combine(_directory, "backups"), _fileSystem).List().Count.ShouldBe(1);
    }

    [Fact]
    public async Task ConcurrentBootstrap_PublishesOneVerifiedImport()
    {
        await File.WriteAllTextAsync(ConfigPath, Seed);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            using var root = new ConfigurationBuilder().AddPlatformConfiguration(ConfigPath).Build()
                .ShouldBeOfType<ConfigurationRoot>();
            root["version"].ShouldBe("1");
        })));
        JsonNode.DeepEquals(await ConfigWriterFactory.Create(ConfigPath, _fileSystem).ReadAsync(), Parse(Seed)).ShouldBeTrue();
        new ConfigBackupService(Path.Combine(_directory, "backups"), _fileSystem).List().Count.ShouldBe(1);
    }

    [Fact]
    public async Task InvalidLegacyImport_DoesNotPublishAuthority()
    {
        await File.WriteAllTextAsync(ConfigPath, "not-json");
        Should.Throw<Exception>(() => new ConfigurationBuilder().AddPlatformConfiguration(ConfigPath).Build());
        File.Exists(StorePath).ShouldBeFalse();
        (await File.ReadAllTextAsync(ConfigPath)).ShouldBe("not-json");
    }

    [Fact]
    public async Task BackupFailure_DoesNotPublishLegacyImport()
    {
        await File.WriteAllTextAsync(ConfigPath, Seed);
        await File.WriteAllTextAsync(Path.Combine(_directory, "backups"), "blocked-directory");
        Should.Throw<Exception>(() => new ConfigurationBuilder().AddPlatformConfiguration(ConfigPath).Build());
        File.Exists(StorePath).ShouldBeFalse();
        (await File.ReadAllTextAsync(ConfigPath)).ShouldBe(Seed);
    }

    [Fact]
    public async Task CancelledMutation_DoesNotCommitOrProject()
    {
        await ConfigStoreBootstrap.PopulateAsync(StorePath, Parse(Seed));
        await File.WriteAllTextAsync(ConfigPath, "stale");
        var writer = ConfigWriterFactory.Create(ConfigPath, _fileSystem);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => writer.MutateAsync(root => root["version"] = 2,
            "cancelled", cancellation.Token));
        (await writer.ReadAsync())["version"].ShouldNotBeNull().GetValue<int>().ShouldBe(1);
        (await File.ReadAllTextAsync(ConfigPath)).ShouldBe("stale");
    }

    [Fact]
    public async Task UnsupportedStore_RefusesProjectionWithoutChangingStore()
    {
        await ConfigStoreBootstrap.PopulateAsync(StorePath, Parse(Seed));
        ConfigStoreBootstrap.ReleaseConnections(StorePath);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={StorePath}"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE store_meta SET value = '999' WHERE key = 'schema_version'; PRAGMA user_version=999;";
            await command.ExecuteNonQueryAsync();
        }
        await File.WriteAllTextAsync(ConfigPath, Seed);
        Should.Throw<Exception>(() => new ConfigurationBuilder().AddPlatformConfiguration(ConfigPath).Build());
        Should.Throw<Exception>(() => ConfigWriterFactory.Create(ConfigPath, _fileSystem));
        (await File.ReadAllTextAsync(ConfigPath)).ShouldBe(Seed);
    }

    [Fact]
    public async Task LegacyFilename_MigratesAuthorityWithoutImportingStaleProjection()
    {
        var legacyPath = Path.Combine(_directory, "config.db");
        await ConfigStoreBootstrap.PopulateAsync(legacyPath, Parse(Seed));
        ConfigStoreBootstrap.ReleaseConnections(legacyPath);
        await File.WriteAllTextAsync(ConfigPath, "{\"version\":99}");
        using var root = new ConfigurationBuilder().AddPlatformConfiguration(ConfigPath).Build()
            .ShouldBeOfType<ConfigurationRoot>();
        root["version"].ShouldBe("1");
        File.Exists(legacyPath).ShouldBeFalse();
        File.Exists(StorePath).ShouldBeTrue();
    }

    [Fact]
    public async Task InterruptedBootstrapTemporaryFile_IsNotAcceptedAsCompletedAuthority()
    {
        await File.WriteAllTextAsync(StorePath + ".bootstrap-interrupted", "incomplete");
        await File.WriteAllTextAsync(ConfigPath, Seed);
        using var root = new ConfigurationBuilder().AddPlatformConfiguration(ConfigPath).Build()
            .ShouldBeOfType<ConfigurationRoot>();
        root["version"].ShouldBe("1");
        JsonNode.DeepEquals(await ConfigWriterFactory.Create(ConfigPath, _fileSystem).ReadAsync(), Parse(Seed)).ShouldBeTrue();
    }

    private static JsonObject Parse(string text) => JsonNode.Parse(text).ShouldBeOfType<JsonObject>();

    public void Dispose()
    {
        ConfigStoreBootstrap.ReleaseConnections(StorePath);
        Directory.Delete(_directory, recursive: true);
    }
}
