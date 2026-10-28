using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Gateway.Configuration.Writers;

namespace BotNexus.Gateway.Configuration.Tests;

public sealed class MarketplaceRegistryServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "botnexus-marketplaces", Guid.NewGuid().ToString("N"));
    private readonly IFileSystem _fileSystem = new FileSystem();
    private string ConfigPath => Path.Combine(_directory, "config.json");
    private string StorePath => ConfigStoreBootstrap.ResolveStorePath(ConfigPath, _fileSystem);
    private MarketplaceRegistryService Registry() => new(ConfigPath, _fileSystem);
    private static MarketplaceId Id(string id) => MarketplaceId.From(id);

    [Theory]
    [InlineData("{\"Marketplaces\":null}")]
    [InlineData("{\"marketplaces\":{},\"Marketplaces\":null}")]
    [InlineData("{\"marketplaces\":{\"broken\":{\"source\":\"https://example.test/safe.git\",\"Enabled\":\"yes\"}}}")]
    [InlineData("{\"marketplaces\":{\"broken\":{\"source\":\"https://example.test/safe.git\",\"enabled\":false,\"Enabled\":true}}}")]
    [InlineData("{\"marketplaces\":{\"broken\":{\"source\":\"https://example.test/safe.git\",\"Source\":\"https://example.test/other.git\"}}}")]
    public async Task MalformedCasedSettings_AllOperationsFailWithoutMutation(string settings)
    {
        Directory.CreateDirectory(_directory);
        var root = JsonNode.Parse("{\"version\":1,\"world\":{\"extensions\":{}}}").ShouldNotBeNull().AsObject();
        root["world"].ShouldNotBeNull()["extensions"].ShouldNotBeNull()[MarketplaceRegistryService.ExtensionId] = JsonNode.Parse(settings);
        await ConfigStoreBootstrap.PopulateAsync(StorePath, root);
        ConfigStoreBootstrap.ReleaseConnections(StorePath);
        var before = await new SqliteConfigStore($"Data Source={StorePath}").ReadEntriesAsync();
        await Should.ThrowAsync<InvalidDataException>(() => Registry().ListAsync());
        await Should.ThrowAsync<InvalidDataException>(() => Registry().AddAsync(Id("new"), "https://example.test/new.git", null));
        await Should.ThrowAsync<InvalidDataException>(() => Registry().UpdateAsync(Id("broken"), "https://example.test/new.git", null));
        await Should.ThrowAsync<InvalidDataException>(() => Registry().SetEnabledAsync(Id("broken"), true));
        await Should.ThrowAsync<InvalidDataException>(() => Registry().RemoveAsync(Id("broken")));
        var after = await new SqliteConfigStore($"Data Source={StorePath}").ReadEntriesAsync();
        after.Keys.Order().ShouldBe(before.Keys.Order());
        foreach (var key in before.Keys) after[key].Value.ShouldBe(before[key].Value);
    }

    [Fact]
    public async Task CasedSettings_ReadAndMutateTheSameRegistration()
    {
        Directory.CreateDirectory(_directory);
        await ConfigStoreBootstrap.PopulateAsync(StorePath, JsonNode.Parse("""
            {"version":1,"world":{"extensions":{"botnexus-plugins-api":{"Marketplaces":{"community":{"Source":"https://example.test/original.git","Reference":"main","Enabled":false,"other":"keep"}}}}}}
            """).ShouldNotBeNull().AsObject());
        ConfigStoreBootstrap.ReleaseConnections(StorePath);
        (await Registry().GetAsync(Id("community"))).Enabled.ShouldBeFalse();
        await Registry().UpdateAsync(Id("community"), "https://example.test/replaced.git", null);
        await Registry().SetEnabledAsync(Id("community"), true);
        var row = await Registry().GetAsync(Id("community"));
        row.Source.ShouldBe("https://example.test/replaced.git");
        row.Reference.ShouldBeNull();
        row.Enabled.ShouldBeTrue();
        var stored = await new SqliteConfigStore($"Data Source={StorePath}").ReadEntriesAsync();
        stored["world.extensions.botnexus-plugins-api.Marketplaces.community.other"].Value.ShouldBe("\"keep\"");
        await Registry().RemoveAsync(Id("community"));
        (await Registry().ListAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("corrupt")]
    [InlineData("conflicting")]
    public async Task Crud_UsesCanonicalStoreAndPreservesSiblingConfiguration(string projection)
    {
        Directory.CreateDirectory(_directory);
        await ConfigStoreBootstrap.PopulateAsync(StorePath, JsonNode.Parse("""
            {"version":1,"world":{"extensions":{"botnexus-plugins-api":{"otherSetting":"keep","marketplaces":{"sibling":{"source":"https://example.test/sibling.git","enabled":true}}},"another-extension":{"enabled":true}}},"gateway":{"defaultTimezone":"UTC"}}
            """).ShouldNotBeNull().AsObject());
        ConfigStoreBootstrap.ReleaseConnections(StorePath);
        if (projection != "absent")
            await File.WriteAllTextAsync(ConfigPath, projection == "corrupt" ? "not json" : "{\"world\":{\"extensions\":{\"botnexus-plugins-api\":{\"marketplaces\":{\"wrong\":{\"source\":\"https://example.test/wrong.git\"}}}}}}");

        await Registry().AddAsync(Id("community"), "https://example.test/catalog.git", "release/1");
        (await Registry().GetAsync(Id("community"))).Reference.ShouldBe("release/1");
        await Registry().UpdateAsync(Id("community"), "ssh://example.test/catalog.git", null);
        await Registry().SetEnabledAsync(Id("community"), false);
        var row = await Registry().GetAsync(Id("community"));
        row.Source.ShouldBe("ssh://example.test/catalog.git");
        row.Reference.ShouldBeNull();
        row.Enabled.ShouldBeFalse();
        (await Registry().ListAsync()).Select(x => x.Id).ShouldBe(["community", "sibling"]);
        await Registry().RemoveAsync(Id("community"));
        (await Registry().ListAsync()).ShouldHaveSingleItem().Id.ShouldBe("sibling");

        var stored = await new SqliteConfigStore($"Data Source={StorePath}").ReadEntriesAsync();
        stored.ShouldContainKey("world.extensions.botnexus-plugins-api.marketplaces.sibling.source");
        stored["world.extensions.botnexus-plugins-api.otherSetting"].Value.ShouldBe("\"keep\"");
        stored["world.extensions.another-extension.enabled"].Value.ShouldBe("true");
        stored["gateway.defaultTimezone"].Value.ShouldBe("\"UTC\"");
        stored.Keys.ShouldNotContain(x => x.StartsWith("world.extensions.botnexus-plugins-api.marketplaces.community", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("null", "marketplaces")]
    [InlineData("{\"valid\":{\"source\":\"https://example.test/valid.git\"},\"broken\":null}", "marketplaces.broken")]
    [InlineData("{\"valid\":{\"source\":\"https://example.test/valid.git\"},\"broken\":{\"enabled\":false}}", "marketplaces.broken.source")]
    [InlineData("{\"broken\":{\"source\":null}}", "marketplaces.broken.source")]
    [InlineData("{\"broken\":{\"source\":\"https://user:secret@example.test/catalog.git\"}}", "marketplaces.broken.source")]
    [InlineData("{\"broken\":{\"source\":\"https://example.test/catalog.git\",\"enabled\":\"yes\"}}", "marketplaces.broken.enabled")]
    public async Task MalformedConfiguration_FailsClosedWithPathDiagnostic(string marketplaces, string diagnosticPath)
    {
        Directory.CreateDirectory(_directory);
        var root = JsonNode.Parse("{\"version\":1,\"world\":{\"extensions\":{\"botnexus-plugins-api\":{}}}}")
            .ShouldNotBeNull().AsObject();
        root["world"].ShouldNotBeNull()["extensions"].ShouldNotBeNull()[MarketplaceRegistryService.ExtensionId]
            .ShouldNotBeNull()["marketplaces"] = JsonNode.Parse(marketplaces);
        await ConfigStoreBootstrap.PopulateAsync(StorePath, root);
        ConfigStoreBootstrap.ReleaseConnections(StorePath);
        var before = await new SqliteConfigStore($"Data Source={StorePath}").ReadEntriesAsync();

        var error = await Should.ThrowAsync<InvalidDataException>(() => Registry().ListAsync());
        error.Message.ShouldContain($"world.extensions.{MarketplaceRegistryService.ExtensionId}.{diagnosticPath}");
        error.Message.ShouldNotContain("secret");
        await Should.ThrowAsync<InvalidDataException>(() => Registry().GetAsync(Id("valid")));
        await Should.ThrowAsync<InvalidDataException>(() => Registry().SetEnabledAsync(Id("broken"), true));
        var after = await new SqliteConfigStore($"Data Source={StorePath}").ReadEntriesAsync();
        after.Keys.Order().ShouldBe(before.Keys.Order());
        foreach (var key in before.Keys) after[key].Value.ShouldBe(before[key].Value);
    }

    [Fact]
    public async Task ConcurrentInstances_PreserveDistinctRegistrationsAndRejectDuplicate()
    {
        Directory.CreateDirectory(_directory);
        await ConfigStoreBootstrap.PopulateAsync(StorePath,
            JsonNode.Parse("{\"version\":1,\"gateway\":{\"defaultTimezone\":\"UTC\"}}").ShouldNotBeNull().AsObject());
        ConfigStoreBootstrap.ReleaseConnections(StorePath);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Registry().AddAsync(Id($"market-{i}"), "https://example.test/catalog.git", null)));
        (await Registry().ListAsync()).Count.ShouldBe(12);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try { await Registry().AddAsync(Id("duplicate"), "https://example.test/catalog.git", null); return true; }
            catch (InvalidOperationException) { return false; }
        }));
        attempts.Count(x => x).ShouldBe(1);
        (await Registry().ListAsync()).Count.ShouldBe(13);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("UPPER")]
    [InlineData("with.dot")]
    [InlineData("two--hyphens")]
    [InlineData(" spaced ")]
    [InlineData("trailing\n")]
    [InlineData("")]
    public void Identity_RejectsNonCanonicalConfigurationPaths(string id) => MarketplaceId.TryFrom(id, out _).ShouldBeFalse();

    [Theory]
    [InlineData("https://user:secret@example.test/catalog.git")]
    [InlineData("ssh://git@example.test/catalog.git")]
    [InlineData("https://example.test/catalog.git?token=secret")]
    [InlineData("https://example.test/catalog.git#fragment")]
    [InlineData("file:///catalog")]
    [InlineData("git@example.test:catalog.git")]
    [InlineData("-option")]
    [InlineData("https://example.test/")]
    [InlineData("https://example.test/catalog%0a.git")]
    public async Task Add_RejectsUnsafeSourceWithoutMutation(string source)
    {
        await Should.ThrowAsync<ArgumentException>(() => Registry().AddAsync(Id("community"), source, null));
        (await Registry().ListAsync()).ShouldBeEmpty();
        await Registry().AddAsync(Id("existing"), "https://example.test/safe.git", "main");
        await Should.ThrowAsync<ArgumentException>(() => Registry().UpdateAsync(Id("existing"), source, null));
        (await Registry().GetAsync(Id("existing"))).Source.ShouldBe("https://example.test/safe.git");
    }

    [Theory]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("-option")]
    [InlineData("main\n")]
    [InlineData("a..b")]
    [InlineData(".hidden/main")]
    [InlineData("heads/.hidden")]
    [InlineData("heads/release.lock/main")]
    public async Task Add_RejectsInvalidReferenceWithoutMutation(string reference)
    {
        await Should.ThrowAsync<ArgumentException>(() => Registry().AddAsync(Id("community"), "https://example.test/catalog.git", reference));
        (await Registry().ListAsync()).ShouldBeEmpty();
        await Registry().AddAsync(Id("existing"), "https://example.test/safe.git", "main");
        await Should.ThrowAsync<ArgumentException>(() => Registry().UpdateAsync(Id("existing"), "https://example.test/safe.git", reference));
        (await Registry().GetAsync(Id("existing"))).Reference.ShouldBe("main");
    }

    [Fact]
    public async Task MissingAndDuplicateMutations_DoNotChangeExistingRegistrationsOrInstalledFiles()
    {
        Directory.CreateDirectory(_directory);
        var sentinel = Path.Combine(_directory, "installed-plugin.txt");
        await File.WriteAllTextAsync(sentinel, "installed content");
        await Registry().AddAsync(Id("community"), "https://example.test/catalog.git", null);
        await Should.ThrowAsync<InvalidOperationException>(() => Registry().AddAsync(Id("community"), "https://example.test/other.git", null));
        await Should.ThrowAsync<KeyNotFoundException>(() => Registry().GetAsync(Id("missing")));
        await Should.ThrowAsync<KeyNotFoundException>(() => Registry().UpdateAsync(Id("missing"), "https://example.test/other.git", null));
        await Should.ThrowAsync<KeyNotFoundException>(() => Registry().SetEnabledAsync(Id("missing"), true));
        await Should.ThrowAsync<KeyNotFoundException>(() => Registry().RemoveAsync(Id("missing")));
        await Registry().SetEnabledAsync(Id("community"), false);
        await Registry().SetEnabledAsync(Id("community"), true);
        await Registry().RemoveAsync(Id("community"));
        (await File.ReadAllTextAsync(sentinel)).ShouldBe("installed content");
    }

    public void Dispose()
    {
        ConfigStoreBootstrap.ReleaseConnections(StorePath);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
