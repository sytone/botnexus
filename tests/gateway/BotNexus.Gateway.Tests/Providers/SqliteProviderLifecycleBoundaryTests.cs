using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Gateway.Configuration.Writers;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Shouldly;
using Xunit;

namespace BotNexus.Gateway.Tests.Providers;

/// <summary>
/// Pins dependent-agent checks to the canonical SQLite before-document, including cleanup of
/// legacy disabled providers. A conflicting generated projection must not hide assignments.
/// </summary>
public sealed class SqliteProviderLifecycleBoundaryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteSectionEntry_AssignedProvider_RejectsBeforeSqliteMutation(bool initiallyEnabled)
    {
        using var fixture = await Fixture.CreateAsync(initiallyEnabled);
        var before = await fixture.ReadStoreAsync();
        var result = await fixture.Controller.DeleteSectionEntry(
            "providers", "retiring", fixture.Writer, fixture.Loader, CancellationToken.None);

        AssertDependencyError(result.ShouldBeOfType<BadRequestObjectResult>().Value.ShouldBeOfType<string>());
        JsonNode.DeepEquals(await fixture.ReadStoreAsync(), before).ShouldBeTrue();
        await fixture.AssertProjectionUnchangedAsync();
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PatchConfig_AssignedProvider_RejectsWholeBatchBeforeSqliteMutation(
        bool initiallyEnabled, bool remove)
    {
        using var fixture = await Fixture.CreateAsync(initiallyEnabled);
        var before = await fixture.ReadStoreAsync();
        var (_, revision) = await fixture.Writer.ReadWithRevisionAsync();
        var result = await fixture.Controller.PatchConfig(new ConfigPatchRequest(
        [
            new("gateway.port", JsonValue.Create(6200)),
            remove ? new("providers.retiring", Remove: true)
                : new("providers.retiring.enabled", JsonValue.Create(false))
        ], revision), fixture.Writer, fixture.Loader, CancellationToken.None);

        var response = result.Result.ShouldBeOfType<BadRequestObjectResult>().Value
            .ShouldBeOfType<ConfigPatchResponse>();
        response.Success.ShouldBeFalse();
        response.Revision.ShouldBeNull();
        AssertDependencyError(response.Errors.ShouldHaveSingleItem());
        JsonNode.DeepEquals(await fixture.ReadStoreAsync(), before).ShouldBeTrue();
        (await fixture.Writer.ReadWithRevisionAsync()).Revision.ShouldBe(revision);
        await fixture.AssertProjectionUnchangedAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateSection_AssignedProvider_DisableRejectedBeforeSqliteMutation(bool entryOnly)
    {
        using var fixture = await Fixture.CreateAsync(initiallyEnabled: true);
        var before = await fixture.ReadStoreAsync();
        var result = entryOnly
            ? await fixture.Controller.UpdateSectionEntry("providers", "retiring",
                new JsonObject { ["enabled"] = false }, fixture.Writer, fixture.Loader, CancellationToken.None)
            : await fixture.Controller.UpdateSection("providers",
                new JsonObject { ["retiring"] = new JsonObject { ["enabled"] = false } },
                fixture.Writer, fixture.Loader, CancellationToken.None);

        AssertDependencyError(result.ShouldBeOfType<BadRequestObjectResult>().Value.ShouldBeOfType<string>());
        JsonNode.DeepEquals(await fixture.ReadStoreAsync(), before).ShouldBeTrue();
        await fixture.AssertProjectionUnchangedAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteSectionEntry_AfterCanonicalReassignment_RemovesOnlyRetiredProvider(bool initiallyEnabled)
    {
        using var fixture = await Fixture.CreateAsync(initiallyEnabled);
        // Agent management owns assignments; config PATCH deliberately rejects edits to agents.
        // Reassign both dependencies in one canonical mutation before requesting provider removal.
        await fixture.Writer.MutateAsync(root =>
        {
            var agents = root["agents"].ShouldBeOfType<JsonObject>();
            agents["alpha"].ShouldBeOfType<JsonObject>()["provider"] = "replacement";
            agents["zeta"].ShouldBeOfType<JsonObject>()["Provider"] = "replacement";
        }, "test-reassign", namedSections: ["agents"]);
        var expected = await fixture.ReadStoreAsync();
        expected["providers"].ShouldBeOfType<JsonObject>().Remove("retiring").ShouldBeTrue();

        var result = await fixture.Controller.DeleteSectionEntry(
            "providers", "retiring", fixture.Writer, fixture.Loader, CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
        JsonNode.DeepEquals(await fixture.ReadStoreAsync(), expected).ShouldBeTrue();
        await fixture.AssertProjectionUnchangedAsync();
    }

    private static void AssertDependencyError(string error)
    {
        error.ShouldBe("Provider 'retiring' cannot be removed or disabled while assigned to 2 dependent agents: " +
            "alpha, zeta. Reassign the agents before changing the provider.");
    }

    private sealed class Fixture(string root, SqliteConfigStore store, PlatformConfigWriter writer) : IDisposable
    {
        private const string Projection = """
            { "providers": { "retiring": { "enabled": true } }, "agents": {}, "gateway": { "port": 5100 } }
            """;
        public ConfigController Controller { get; } = new();
        public PlatformConfigWriter Writer { get; } = writer;
        public IExtensionLoader Loader { get; } = CreateLoader();

        public static async Task<Fixture> CreateAsync(bool initiallyEnabled)
        {
            var root = Path.Combine(Path.GetTempPath(), "botnexus-provider-lifecycle", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var storePath = Path.Combine(root, ConfigStoreBootstrap.StoreFileName);
            var configPath = Path.Combine(root, "config.json");
            var store = new SqliteConfigStore($"Data Source={storePath};Pooling=False");
            var document = JsonNode.Parse("""
                {
                  "gateway": { "port": 5100 },
                  "authorityProbe": "sqlite",
                  "providers": {
                    "retiring": { "enabled": true, "apiKey": "canonical-secret" },
                    "replacement": { "enabled": true }
                  },
                  "agents": {
                    "zeta": { "Provider": "RETIRING", "model": "test-model" },
                    "unrelated": { "provider": "replacement", "model": "test-model" },
                    "alpha": { "provider": "retiring", "model": "test-model" }
                  }
                }
                """).ShouldBeOfType<JsonObject>();
            document["providers"].ShouldBeOfType<JsonObject>()["retiring"].ShouldBeOfType<JsonObject>()["enabled"] = initiallyEnabled;
            await store.WriteDocumentAsync(document);
            await File.WriteAllTextAsync(configPath, Projection);
            var writer = new PlatformConfigWriter(configPath, new FileSystem(), backup: null,
                writer: new SqliteConfigurationWriter(store, storePath), pristineStore: store);
            writer.Backends.ShouldHaveSingleItem().Name.ShouldBe("sqlite");
            writer.Backends.ShouldHaveSingleItem().WinsOnRead.ShouldBeTrue();
            return new Fixture(root, store, writer);
        }

        public async Task<JsonObject> ReadStoreAsync() =>
            ConfigDocumentRehydrator.Rehydrate(await store.ReadEntriesAsync());

        public async Task AssertProjectionUnchangedAsync() =>
            (await File.ReadAllTextAsync(Path.Combine(root, "config.json"))).ShouldBe(Projection);

        public void Dispose() => Directory.Delete(root, recursive: true);

        private static IExtensionLoader CreateLoader()
        {
            var loader = new Mock<IExtensionLoader>();
            loader.Setup(value => value.GetLoaded()).Returns([]);
            return loader.Object;
        }
    }
}
