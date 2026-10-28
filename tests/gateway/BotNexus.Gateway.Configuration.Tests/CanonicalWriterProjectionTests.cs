using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Gateway.Configuration.Writers;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>Exercises projection repair through the same factory used by configuration consumers.</summary>
public sealed class CanonicalWriterProjectionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"bn-projection-{Guid.NewGuid():N}");
    private readonly IFileSystem _fileSystem = new FileSystem();
    private string ConfigPath => Path.Combine(_directory, "config.json");
    private string StorePath => ConfigStoreBootstrap.ResolveStorePath(ConfigPath, _fileSystem);
    private string BackupDirectory => Path.Combine(_directory, "backups");

    private const string Seed = """
        {
          "version": 1,
          "gateway": { "listenUrl": "http://localhost:5000", "defaultTimezone": "UTC" },
          "channels": { "telegram": { "enabled": true, "botToken": "canonical-secret" } },
          "agents": { "alpha": { "model": "canonical-model", "unknown": { "enabled": true } } },
          "unmodelled": { "explicitNull": null, "empty": {}, "items": [1, "two"] }
        }
        """;

    public CanonicalWriterProjectionTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("malformed")]
    [InlineData("absent")]
    [InlineData("conflicting")]
    [InlineData("empty")]
    [InlineData("whitespace")]
    [InlineData("null")]
    public async Task Mutation_PreservesCanonicalSiblingsAndRepairsOnlyMalformedProjection(string projection)
    {
        var before = Parse(Seed);
        await ConfigStoreBootstrap.PopulateAsync(StorePath, before);
        var store = new SqliteConfigStore($"Data Source={StorePath}");
        var beforeEntries = await store.ReadEntriesAsync();
        var oldProjection = await SetProjectionAsync(projection);
        var writer = ConfigWriterFactory.Create(ConfigPath, _fileSystem);
        JsonObject? observed = null;

        await writer.MutateAsync(root =>
        {
            observed = root.DeepClone().AsObject();
            root["gateway"].ShouldBeOfType<JsonObject>()["listenUrl"] = "http://localhost:6000";
        }, "projection-repair");

        JsonNode.DeepEquals(observed, before).ShouldBeTrue("the mutation must see SQLite, never the projection");
        var expected = before.DeepClone().AsObject();
        expected["gateway"].ShouldBeOfType<JsonObject>()["listenUrl"] = "http://localhost:6000";
        var actualProjection = Parse(await File.ReadAllTextAsync(ConfigPath));
        if (projection == "malformed")
        {
            JsonNode.DeepEquals(actualProjection, expected).ShouldBeTrue(
                "only malformed JSON is rebuilt from the full canonical state, including unknown keys and explicit nulls");
        }
        else
        {
            // Match the established CLI delta contract: patch valid JSON, or start empty when
            // absent/empty/null. Unchanged canonical siblings belong to SQLite, not this projection.
            var expectedProjection = projection == "conflicting"
                ? Parse(oldProjection.ShouldNotBeNull()) : new JsonObject();
            expectedProjection["gateway"] ??= new JsonObject();
            expectedProjection["gateway"].ShouldBeOfType<JsonObject>()["listenUrl"] = "http://localhost:6000";
            JsonNode.DeepEquals(actualProjection, expectedProjection).ShouldBeTrue(
                "valid or absent projections must retain delta-patch behavior, not be reconstructed");
            actualProjection.ContainsKey("agents").ShouldBeFalse();
            actualProjection.ContainsKey("unmodelled").ShouldBeFalse();
            actualProjection["gateway"].ShouldBeOfType<JsonObject>().ContainsKey("defaultTimezone").ShouldBeFalse();
            if (projection == "conflicting")
            {
                actualProjection["channels"].ShouldBeOfType<JsonObject>()["telegram"]
                    .ShouldBeOfType<JsonObject>()["botToken"].ShouldNotBeNull().GetValue<string>().ShouldBe("stale-secret");
                actualProjection["projectionOnly"].ShouldNotBeNull().GetValue<bool>().ShouldBeTrue();
            }
        }

        var afterEntries = await store.ReadEntriesAsync();
        afterEntries.Keys.Order().ShouldBe(beforeEntries.Keys.Order());
        foreach (var (path, entry) in beforeEntries)
        {
            if (path == "gateway.listenUrl")
                afterEntries[path].Value.ShouldBe("\"http://localhost:6000\"");
            else
                afterEntries[path].ShouldBe(entry, $"canonical sibling {path} must remain unchanged");
        }
        JsonNode.DeepEquals(ConfigDocumentRehydrator.Rehydrate(afterEntries), expected).ShouldBeTrue();
        Directory.GetFiles(_directory, "*.tmp").ShouldBeEmpty();
        AssertOwnerOnly(ConfigPath);

        var backups = new ConfigBackupService(BackupDirectory, _fileSystem).List();
        if (oldProjection is null)
            backups.ShouldBeEmpty();
        else
        {
            var backup = backups.ShouldHaveSingleItem();
            (await File.ReadAllTextAsync(backup.FullPath)).ShouldBe(oldProjection);
            AssertOwnerOnly(backup.FullPath);
        }
    }

    [Fact]
    public async Task JsonOnlyMalformedDocument_FailsWithoutOverwritingOrCreatingStore()
    {
        const string malformed = "not-json";
        await File.WriteAllTextAsync(ConfigPath, malformed);
        var mutationCalled = false;
        var writer = ConfigWriterFactory.Create(ConfigPath, _fileSystem);

        await Should.ThrowAsync<JsonException>(() => writer.MutateAsync(root =>
        {
            mutationCalled = true;
            root["version"] = 2;
        }, "must-fail"));

        mutationCalled.ShouldBeFalse();
        (await File.ReadAllTextAsync(ConfigPath)).ShouldBe(malformed);
        File.Exists(StorePath).ShouldBeFalse();
        Directory.Exists(BackupDirectory).ShouldBeFalse();
        Directory.GetFiles(_directory, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public async Task JsonWriter_WithoutCanonicalFallback_RejectsMalformedProjection()
    {
        const string malformed = "not-json";
        await File.WriteAllTextAsync(ConfigPath, malformed);
        var before = Parse(Seed);
        var after = before.DeepClone().AsObject();
        after["gateway"].ShouldBeOfType<JsonObject>()["listenUrl"] = "http://localhost:6000";
        var writer = new JsonConfigurationWriter(ConfigPath, _fileSystem,
            new ConfigBackupService(BackupDirectory, _fileSystem));

        await Should.ThrowAsync<JsonException>(() => writer.ApplyChangeSetAsync(
            ConfigDocumentDiffer.Diff(before, after), "no-fallback"));

        (await File.ReadAllTextAsync(ConfigPath)).ShouldBe(malformed);
        File.Exists(StorePath).ShouldBeFalse();
        Directory.Exists(BackupDirectory).ShouldBeFalse();
        Directory.GetFiles(_directory, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public async Task ProjectionBackupFailure_IsDisclosedWhileCanonicalChangeStillLands()
    {
        await ConfigStoreBootstrap.PopulateAsync(StorePath, Parse(Seed));
        const string malformed = "not-json";
        await File.WriteAllTextAsync(ConfigPath, malformed);
        // A file occupying the backup directory deterministically rejects backup creation on both OSes.
        await File.WriteAllTextAsync(BackupDirectory, "blocked");
        var writer = ConfigWriterFactory.Create(ConfigPath, _fileSystem);

        var failure = await Should.ThrowAsync<AggregateException>(() => writer.MutateAsync(
            root => root["gateway"].ShouldBeOfType<JsonObject>()["listenUrl"] = "http://localhost:6000",
            "backup-failure"));

        failure.Message.ShouldContain("Applied to: sqlite");
        failure.InnerExceptions.ShouldHaveSingleItem().Message.ShouldContain("'json'");
        (await File.ReadAllTextAsync(ConfigPath)).ShouldBe(malformed);
        var entries = await new SqliteConfigStore($"Data Source={StorePath}").ReadEntriesAsync();
        entries["gateway.listenUrl"].Value.ShouldBe("\"http://localhost:6000\"");
        entries["channels.telegram.botToken"].Value.ShouldBe("\"canonical-secret\"");
        Directory.GetFiles(_directory, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public async Task NoOpMutation_DoesNotRepairOrBackUpProjection()
    {
        await ConfigStoreBootstrap.PopulateAsync(StorePath, Parse(Seed));
        const string malformed = "not-json";
        await File.WriteAllTextAsync(ConfigPath, malformed);
        var writer = ConfigWriterFactory.Create(ConfigPath, _fileSystem);

        await writer.MutateAsync(_ => { }, "no-op");

        (await File.ReadAllTextAsync(ConfigPath)).ShouldBe(malformed);
        Directory.Exists(BackupDirectory).ShouldBeFalse();
    }

    private async Task<string?> SetProjectionAsync(string projection)
    {
        var text = projection switch
        {
            "malformed" => "not-json",
            "absent" => null,
            "empty" => string.Empty,
            "whitespace" => " \r\n ",
            "null" => "null",
            "conflicting" => """{"gateway":{"listenUrl":"http://localhost:9999"},"channels":{"telegram":{"botToken":"stale-secret"}},"projectionOnly":true}""",
            _ => throw new ArgumentOutOfRangeException(nameof(projection))
        };
        if (text is not null)
            await File.WriteAllTextAsync(ConfigPath, text);
        return text;
    }

    private static JsonObject Parse(string json) => JsonNode.Parse(json).ShouldBeOfType<JsonObject>();

    private static void AssertOwnerOnly(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(path).ShouldBe(SecureFilePermissions.OwnerOnlyMode);
    }

    public void Dispose()
    {
        ConfigStoreBootstrap.ReleaseConnections(StorePath);
        Directory.Delete(_directory, recursive: true);
    }
}
