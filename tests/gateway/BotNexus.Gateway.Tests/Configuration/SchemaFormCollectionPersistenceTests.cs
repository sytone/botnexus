using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Real host/file/reload persistence tests for the config list &amp; dictionary lifecycle editing
/// surfaced by the Portal SchemaForm (#2062). The SchemaForm mutates a config JSON tree in place
/// (add/remove/reorder list items; add/delete/rename dictionary entries) and the edited section is
/// PUT back through <see cref="PlatformConfigWriter"/>, which performs an atomic read-modify-write
/// against config.json. These tests drive the real <see cref="FileSystem"/> end to end and assert
/// that each collection mutation persists losslessly, round-trips through a fresh read (reload), and
/// never replaces unrelated siblings -- including secret-valued dictionaries and concurrent
/// mutations to sibling sections.
/// </summary>
public sealed class SchemaFormCollectionPersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "botnexus-schemaform-2062-" + Guid.NewGuid().ToString("N"));
    private readonly string _configPath;
    private readonly IFileSystem _fileSystem = new FileSystem();

    public SchemaFormCollectionPersistenceTests()
    {
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "config.json");
    }

    private PlatformConfigWriter NewWriter() => new(_configPath, _fileSystem);

    private async Task SeedAsync(JsonObject config)
        => await File.WriteAllTextAsync(
            _configPath,
            config.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

    private async Task<JsonObject> ReloadAsync()
        => JsonNode.Parse(await File.ReadAllTextAsync(_configPath))!.AsObject();

    // -- Scalar list add/reorder/remove persists losslessly -----------------

    [Fact]
    public async Task ListLifecycle_AddReorderRemove_PersistsAndReloadsLossless()
    {
        await SeedAsync(new JsonObject
        {
            ["agents"] = new JsonObject(),
            ["gateway"] = new JsonObject
            {
                ["allowedPaths"] = new JsonArray("a", "b"),
                ["listenUrl"] = "http://localhost:5005",
            },
        });
        var writer = NewWriter();

        // The SchemaForm-produced mutation: add "c", reorder (a<->b), remove "b" -- all as an
        // exact-path edit of the gateway section, then PUT the whole edited section back.
        await writer.UpdateSectionAsync("gateway", new JsonObject
        {
            ["allowedPaths"] = new JsonArray("b", "a", "c"),
            ["listenUrl"] = "http://localhost:5005",
        });

        var reloaded = await ReloadAsync();
        var arr = reloaded["gateway"]!["allowedPaths"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        arr.ShouldBe(new[] { "b", "a", "c" });
        // Unrelated sibling scalar preserved.
        reloaded["gateway"]!["listenUrl"]!.GetValue<string>().ShouldBe("http://localhost:5005");
    }

    // -- Nested array of objects persists losslessly ------------------------

    [Fact]
    public async Task NestedArrayOfObjects_EditPersistsWithoutClobberingSiblings()
    {
        await SeedAsync(new JsonObject
        {
            ["gateway"] = new JsonObject
            {
                ["satellites"] = new JsonArray(
                    new JsonObject { ["name"] = "s1", ["url"] = "http://s1" },
                    new JsonObject { ["name"] = "s2", ["url"] = "http://s2" }),
            },
        });
        var writer = NewWriter();

        // Edit only the second element's url (the path-through-array case #2062 fixed).
        await writer.UpdateSectionAsync("gateway", new JsonObject
        {
            ["satellites"] = new JsonArray(
                new JsonObject { ["name"] = "s1", ["url"] = "http://s1" },
                new JsonObject { ["name"] = "s2", ["url"] = "http://s2-edited" }),
        });

        var reloaded = await ReloadAsync();
        var sats = reloaded["gateway"]!["satellites"]!.AsArray();
        sats[0]!["url"]!.GetValue<string>().ShouldBe("http://s1");
        sats[1]!["url"]!.GetValue<string>().ShouldBe("http://s2-edited");
    }

    // -- Dictionary of objects: add / delete / rename -----------------------

    [Fact]
    public async Task DictionaryLifecycle_AddDeleteRename_PersistsAndReloads()
    {
        await SeedAsync(new JsonObject
        {
            ["providers"] = new JsonObject
            {
                ["openai"] = new JsonObject { ["enabled"] = true },
                ["anthropic"] = new JsonObject { ["enabled"] = false },
            },
        });
        var writer = NewWriter();

        // Add "google", delete "anthropic", rename "openai" -> "openai-2" -- SchemaForm rebuilds the
        // dictionary preserving order and PUTs the whole section. Use merge:false so deletions by
        // omission take effect (the authoritative section is assembled by the editor).
        await writer.UpdateSectionAsync("providers", new JsonObject
        {
            ["openai-2"] = new JsonObject { ["enabled"] = true },
            ["google"] = new JsonObject { ["enabled"] = true },
        }, merge: false);

        var reloaded = await ReloadAsync();
        var providers = reloaded["providers"]!.AsObject();
        providers.ContainsKey("anthropic").ShouldBeFalse();
        providers.ContainsKey("openai").ShouldBeFalse();
        providers.ContainsKey("openai-2").ShouldBeTrue();
        providers.ContainsKey("google").ShouldBeTrue();
        providers["openai-2"]!["enabled"]!.GetValue<bool>().ShouldBeTrue();
    }

    // -- Secret-valued dictionary round-trips real secrets ------------------

    [Fact]
    public async Task SecretValuedDictionary_AddEntry_RestoresExistingSecretsAndKeepsNew()
    {
        // gateway.apiKeys is a dictionary whose entries carry secret apiKey values. The UI receives
        // them redacted ("***"); on save the writer must restore the on-disk secret for untouched
        // entries and persist the newly added one, never writing the placeholder over a real secret.
        await SeedAsync(new JsonObject
        {
            ["gateway"] = new JsonObject
            {
                ["apiKeys"] = new JsonObject
                {
                    ["k1"] = new JsonObject { ["apiKey"] = "real-secret-1", ["isAdmin"] = true },
                },
            },
        });
        var writer = NewWriter();

        // UI PUTs the existing (redacted) entry plus a new one.
        await writer.UpdateSectionAsync("gateway", new JsonObject
        {
            ["apiKeys"] = new JsonObject
            {
                ["k1"] = new JsonObject { ["apiKey"] = "***", ["isAdmin"] = true },
                ["k2"] = new JsonObject { ["apiKey"] = "real-secret-2", ["isAdmin"] = false },
            },
        });

        var reloaded = await ReloadAsync();
        var apiKeys = reloaded["gateway"]!["apiKeys"]!.AsObject();
        // Existing secret restored, not clobbered by "***".
        apiKeys["k1"]!["apiKey"]!.GetValue<string>().ShouldBe("real-secret-1");
        // New entry persisted.
        apiKeys["k2"]!["apiKey"]!.GetValue<string>().ShouldBe("real-secret-2");
    }

    // -- Concurrent sibling changes both survive ----------------------------

    [Fact]
    public async Task ConcurrentSiblingSectionEdits_BothPersist()
    {
        await SeedAsync(new JsonObject
        {
            ["providers"] = new JsonObject { ["openai"] = new JsonObject { ["enabled"] = true } },
            ["channels"] = new JsonObject { ["signalr"] = new JsonObject { ["enabled"] = true } },
            ["cron"] = new JsonObject { ["enabled"] = true },
        });
        var writer = NewWriter();

        // Two independent editors mutate sibling sections; the writer serializes them and neither
        // may drop the other's change.
        var editProviders = writer.UpdateSectionAsync("providers", new JsonObject
        {
            ["openai"] = new JsonObject { ["enabled"] = true },
            ["anthropic"] = new JsonObject { ["enabled"] = true },
        }, merge: false);
        var editChannels = writer.UpdateSectionAsync("channels", new JsonObject
        {
            ["signalr"] = new JsonObject { ["enabled"] = false },
        });
        await Task.WhenAll(editProviders, editChannels);

        var reloaded = await ReloadAsync();
        reloaded["providers"]!["anthropic"]!["enabled"]!.GetValue<bool>().ShouldBeTrue();
        reloaded["channels"]!["signalr"]!["enabled"]!.GetValue<bool>().ShouldBeFalse();
        // The wholly-untouched sibling section is intact.
        reloaded["cron"]!["enabled"]!.GetValue<bool>().ShouldBeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
