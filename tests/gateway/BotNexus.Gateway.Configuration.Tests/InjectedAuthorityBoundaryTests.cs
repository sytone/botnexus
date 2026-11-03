using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Gateway.Configuration.Writers;
using Microsoft.Extensions.Configuration;

namespace BotNexus.Gateway.Configuration.Tests;

public sealed class InjectedAuthorityBoundaryTests
{
    [Fact]
    public async Task MockHost_ReaderAndWriterShareSelectedHomeAuthority_NotProjection()
    {
        var fs = new MockFileSystem();
        var home = fs.Path.Combine(fs.Directory.GetCurrentDirectory(), "selected-home");
        var path = fs.Path.Combine(home, "config.json");
        fs.AddFile(path, new MockFileData("{\"version\":1,\"gateway\":{\"listenUrl\":\"http://localhost:5000\"},\"unknown\":{\"null\":null,\"items\":[],\"empty\":{}}}"));
        var factory = new TestConfigAuthorityFactory();
        var writer = ConfigWriterFactory.Create(path, fs, authorityFactory: factory);
        fs.File.WriteAllText(path, "{\"projectionOnly\":true}");
        using var root = new ConfigurationBuilder().AddPlatformConfiguration(path, fileSystem: fs, authorityFactory: factory)
            .Build().ShouldBeOfType<ConfigurationRoot>();
        root.Providers.ShouldHaveSingleItem().ShouldBeOfType<SqliteConfigurationProvider>();
        root["projectionOnly"].ShouldBeNull();
        root["gateway:listenUrl"].ShouldBe("http://localhost:5000");
        await writer.MutateAsync(document => document["version"] = 2, "injected");
        root.Reload();
        root["version"].ShouldBe("2");
        JsonNode.DeepEquals(JsonNode.Parse(fs.File.ReadAllText(path)), await writer.ReadAsync()).ShouldBeTrue();
        var unknown = (await writer.ReadAsync())["unknown"].ShouldBeOfType<JsonObject>();
        unknown.ContainsKey("null").ShouldBeTrue();
        unknown["items"].ShouldBeOfType<JsonArray>().ShouldBeEmpty();
        unknown["empty"].ShouldBeOfType<JsonObject>().ShouldBeEmpty();
        factory.Opens.ShouldAllBe(open => open.Path == path && ReferenceEquals(open.FileSystem, fs));
        var monitor = PlatformConfigurationSources.BuildMonitor(path, fs, authorityFactory: factory);
        monitor.CurrentValue.Gateway.ShouldNotBeNull().ListenUrl.ShouldBe("http://localhost:5000");
    }

    [Fact]
    public async Task InjectedEmptyAuthority_StaysEmptyAndAbsentProjectionIsNotCreated()
    {
        var fs = new MockFileSystem();
        var path = fs.Path.Combine(fs.Directory.GetCurrentDirectory(), "empty-home", "config.json");
        var factory = new TestConfigAuthorityFactory();
        var writer = ConfigWriterFactory.Create(path, fs, authorityFactory: factory);
        await writer.MutateAsync(root => root["version"] = 1, "fresh");
        fs.File.Exists(path).ShouldBeFalse();
        var authority = await factory.OpenAsync(path, fs);
        await authority.Store.WriteDocumentAsync(new JsonObject());
        fs.AddFile(path, new MockFileData("{\"version\":99}"));
        (await ConfigWriterFactory.Create(path, fs, authorityFactory: factory).ReadAsync()).ShouldBeEmpty();
        using var root = new ConfigurationBuilder().AddPlatformConfiguration(path, fileSystem: fs, authorityFactory: factory)
            .Build().ShouldBeOfType<ConfigurationRoot>();
        root["version"].ShouldBeNull();
    }

    [Fact]
    public async Task InjectedFactory_IsolatesHomesAndPreservesRevisionConflicts()
    {
        var fs = new MockFileSystem();
        var firstPath = fs.Path.Combine(fs.Directory.GetCurrentDirectory(), "first", "config.json");
        var secondPath = fs.Path.Combine(fs.Directory.GetCurrentDirectory(), "second", "config.json");
        var factory = new TestConfigAuthorityFactory();
        var first = ConfigWriterFactory.Create(firstPath, fs, authorityFactory: factory);
        var second = ConfigWriterFactory.Create(secondPath, fs, authorityFactory: factory);
        await first.MutateAsync(root => root["version"] = 1, "first");
        await second.MutateAsync(root => root["version"] = 7, "second");
        var stale = await first.ReadWithRevisionAsync();
        await first.MutateAsync(root => root["version"] = 2, "intervening");
        var patch = new[] { new ConfigPatchOperation("version", JsonValue.Create(3)) };
        await Should.ThrowAsync<PlatformConfigConcurrencyException>(() => first.ApplyPatchAsync(patch, "stale", expectedRevision: stale.Revision));
        (await first.ReadAsync())["version"].ShouldNotBeNull().GetValue<int>().ShouldBe(2);
        (await second.ReadAsync())["version"].ShouldNotBeNull().GetValue<int>().ShouldBe(7);
        fs.File.Exists(firstPath).ShouldBeFalse();
        fs.File.Exists(secondPath).ShouldBeFalse();
    }

    private sealed class RejectingFactory : IConfigAuthorityFactory
    {
        public Task<ConfigAuthorityBinding> OpenAsync(string configPath, IFileSystem fileSystem,
            CancellationToken cancellationToken = default)
            => Task.FromException<ConfigAuthorityBinding>(new IOException("authority-unavailable"));
    }

    [Fact]
    public void InjectedFactoryFailure_DoesNotFallBackToValidJson()
    {
        var fs = new MockFileSystem();
        var path = fs.Path.Combine(fs.Directory.GetCurrentDirectory(), "failed", "config.json");
        const string projection = "{\"version\":99}";
        fs.AddFile(path, new MockFileData(projection));
        var factory = new RejectingFactory();
        Should.Throw<IOException>(() => ConfigWriterFactory.Create(path, fs, authorityFactory: factory));
        Should.Throw<IOException>(() => PlatformConfigurationSources.BuildMonitor(path, fs, authorityFactory: factory));
        fs.File.ReadAllText(path).ShouldBe(projection);
        fs.AllFiles.ShouldBe([path]);
    }

    [Fact]
    public void ExplicitBackendWithoutAuthority_IsRejectedBeforeReadingJson()
    {
        var fs = new MockFileSystem();
        var path = fs.Path.Combine(fs.Directory.GetCurrentDirectory(), "rejected", "config.json");
        fs.AddFile(path, new MockFileData("not-json"));
        var backend = new JsonConfigurationWriter(path, fs);
        Should.Throw<ArgumentNullException>(() => new PlatformConfigWriter(path, fs, null, backend))
            .ParamName.ShouldBe("pristineStore");
        fs.File.ReadAllText(path).ShouldBe("not-json");
    }

    [Fact]
    public async Task DefaultConstructor_IgnoresConflictingProjectionEvenForEmptyAuthority()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bn-constructor-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.json");
        var fs = new FileSystem();
        var storePath = ConfigStoreBootstrap.ResolveStorePath(path, fs);
        try
        {
            await ConfigStoreBootstrap.PopulateAsync(storePath, new JsonObject());
            await File.WriteAllTextAsync(path, "not-json");
            var writer = new PlatformConfigWriter(path, fs, null, writer: null);
            (await writer.ReadAsync()).ShouldBeEmpty();
            await writer.MutateAsync(root => root["version"] = 1, "constructor");
            JsonNode.DeepEquals(JsonNode.Parse(await File.ReadAllTextAsync(path)), await writer.ReadAsync()).ShouldBeTrue();
            writer.Backends.ShouldHaveSingleItem().WinsOnRead.ShouldBeTrue();
        }
        finally
        {
            ConfigStoreBootstrap.ReleaseConnections(storePath);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AuthorityWithoutMatchingBackend_IsRejected()
    {
        var fs = new MockFileSystem();
        var path = fs.Path.Combine(fs.Directory.GetCurrentDirectory(), "invalid-binding", "config.json");
        Should.Throw<ArgumentException>(() => new PlatformConfigWriter(path, fs, null, writer: null,
            pristineStore: new TestConfigStore(new JsonObject()))).ParamName.ShouldBe("writer");
        fs.File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public async Task DefaultConstructor_UsesPhysicalSqliteAuthorityAndNeverCreatesJson()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bn-constructor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.json");
        var fs = new FileSystem();
        var storePath = ConfigStoreBootstrap.ResolveStorePath(path, fs);
        try
        {
            var writer = new PlatformConfigWriter(path, fs);
            writer.Backends.ShouldHaveSingleItem().Name.ShouldBe("sqlite");
            await writer.MutateAsync(root => root["version"] = 1, "constructor");
            File.Exists(path).ShouldBeFalse();
            File.Exists(storePath).ShouldBeTrue();
            (await new SqliteConfigStore($"Data Source={storePath}").ReadEntriesAsync())["version"].Value.ShouldBe("1");
        }
        finally
        {
            ConfigStoreBootstrap.ReleaseConnections(storePath);
            Directory.Delete(directory, recursive: true);
        }
    }
}
