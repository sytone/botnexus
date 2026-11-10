using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Cli.Commands;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Configuration.Store;
using Spectre.Console;

namespace BotNexus.Cli.Tests;

[Collection("AnsiConsole")]
public sealed class ExtensionConfigPathCommandTests
{
    [Theory]
    [InlineData("metadata")]
    [InlineData("isolationOptions")]
    public async Task ExecuteSetAsync_NullableOpaqueWholeProperty_ReplacesAndClears(string property)
    {
        var directory = Path.Combine(Path.GetTempPath(), "botnexus-4540-nullable", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.json");
        var originalConsole = AnsiConsole.Console;
        using var output = new StringWriter();
        try
        {
            await File.WriteAllTextAsync(path,
                """{"agents":{"alpha":{"displayName":"sibling","provider":"copilot","model":"gpt-4.1"}}}""");
            AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(output), Ansi = AnsiSupport.No
            });
            var commands = new ConfigCommands(new ConfigPathResolver());
            var key = "agents.alpha." + property;
            foreach (var raw in new[] { "{\"keep\":17,\"parent\":{}}", "null", "{\"replacement\":true}" })
            {
                output.GetStringBuilder().Clear();
                (await commands.ExecuteSetAsync(key, raw, path, false, CancellationToken.None)).ShouldBe(0, output.ToString());
                var root = JsonNode.Parse(await File.ReadAllTextAsync(path)).ShouldBeOfType<JsonObject>();
                var agent = root["agents"].ShouldBeOfType<JsonObject>()["alpha"].ShouldBeOfType<JsonObject>();
                agent.ContainsKey(property).ShouldBeTrue();
                JsonNode.DeepEquals(agent[property], JsonNode.Parse(raw)).ShouldBeTrue();
                agent["displayName"].ShouldNotBeNull().GetValue<string>().ShouldBe("sibling");
            }
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("{\"parent\":false}", "parent.child")]
    [InlineData("{\"items\":[]}", "items[1].name")]
    [InlineData("{\"items\":[false]}", "items[0].name")]
    public async Task ExecuteSetAsync_LatestShapeChangesAfterPreflight_RejectsWithoutWriting(string latest, string suffix)
    {
        var directory = Path.Combine(Path.GetTempPath(), "botnexus-4540-race", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.json");
        var originalConsole = AnsiConsole.Console;
        using var output = new StringWriter();
        try
        {
            await File.WriteAllTextAsync(path,
                """{"gateway":{"extensions":{"sample":{"parent":{},"items":[{"name":"a"},{"name":"b"}]}}}}""");
            var latestDocument = "{\"gateway\":{\"extensions\":{\"sample\":" + latest + "}}}";
            var resolver = new BeforePersistResolver(() => File.WriteAllText(path, latestDocument));
            AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(output), Ansi = AnsiSupport.No
            });
            var key = "gateway.extensions.sample." + suffix;
            (await new ConfigCommands(resolver).ExecuteSetAsync(key, "changed", path, false,
                CancellationToken.None)).ShouldBe(1, output.ToString());
            resolver.Replaced.ShouldBeTrue();
            output.ToString().ShouldContain(key);
            (await File.ReadAllTextAsync(path)).ShouldBe(latestDocument);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class BeforePersistResolver(Action replace) : IConfigPathResolver
    {
        private readonly ConfigPathResolver _inner = new();
        public bool Replaced { get; private set; }

        public bool TrySetValue(object config, string path, object? value, out string error)
            => _inner.TrySetValue(config, path, value, out error);

        public bool TryGetValue(object config, string path, out object? value, out string error)
        {
            var result = _inner.TryGetValue(config, path, out value, out error);
            if (result)
            {
                replace();
                Replaced = true;
            }
            return result;
        }

        public IReadOnlyList<string> GetAvailablePaths(object config) => _inner.GetAvailablePaths(config);
    }

    [Fact]
    public async Task ExecuteSetAsync_RootExtensionArray_PersistsAddressedElement()
    {
        var directory = Path.Combine(Path.GetTempPath(), "botnexus-4540-array", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.json");
        var originalConsole = AnsiConsole.Console;
        using var output = new StringWriter();
        try
        {
            await File.WriteAllTextAsync(path,
                """{"gateway":{"extensions":{"sample":[{"name":"old","keep":17}]}}}""");
            AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(output), Ansi = AnsiSupport.No
            });
            var commands = new ConfigCommands(new ConfigPathResolver());
            const string key = "gateway.extensions.sample[0].name";
            (await commands.ExecuteSetAsync(key, "new", path, false, CancellationToken.None)).ShouldBe(0, output.ToString());
            var root = JsonNode.Parse(await File.ReadAllTextAsync(path)).ShouldBeOfType<JsonObject>();
            var item = root["gateway"].ShouldBeOfType<JsonObject>()["extensions"].ShouldBeOfType<JsonObject>()
                ["sample"].ShouldBeOfType<JsonArray>()[0].ShouldBeOfType<JsonObject>();
            item["name"].ShouldNotBeNull().GetValue<string>().ShouldBe("new");
            item["keep"].ShouldNotBeNull().GetValue<int>().ShouldBe(17);
            output.GetStringBuilder().Clear();
            (await commands.ExecuteGetAsync(key, path, false, CancellationToken.None)).ShouldBe(0);
            JsonNode.Parse(output.ToString()).ShouldNotBeNull().GetValue<string>().ShouldBe("new");
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("world.extensions.sample", false)]
    [InlineData("gateway.extensions.sample", false)]
    [InlineData("agents.alpha.extensions.sample", false)]
    [InlineData("agents.defaults.extensions.sample", false)]
    [InlineData("world.extensions.sample", true)]
    [InlineData("gateway.extensions.sample", true)]
    [InlineData("agents.alpha.extensions.sample", true)]
    [InlineData("agents.defaults.extensions.sample", true)]
    public async Task ExecuteSetAsync_NestedJson_PreservesCanonicalDocument(string prefix, bool fileOnly)
    {
        var directory = Path.Combine(Path.GetTempPath(), "botnexus-4540", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, "config.json");
        var storePath = ConfigStoreBootstrap.ResolveStorePath(configPath, new FileSystem());
        var originalConsole = AnsiConsole.Console;
        using var output = new StringWriter();
        try
        {
            var expected = JsonNode.Parse("""
                {"gateway":{"listenUrl":"http://localhost:5099"},
                 "world":{"extensions":{"sample":{"Settings":{"enabled":true,"keep":17},"items":[{"name":"old","keep":true}]}}},
                 "agents":{"alpha":{"provider":"copilot","model":"gpt-4.1"},"defaults":{"toolTimeoutSeconds":123}},
                 "futureRoot":{"empty":[],"explicitNull":null}}
                """).ShouldBeOfType<JsonObject>();
            // Every scope receives the same opaque extension fixture; whole-document equality
            // below detects sibling loss, key recasing, shape drift and unrelated defaults.
            var segments = prefix.Split('.');
            var parent = expected;
            foreach (var segment in segments[..^1])
            {
                if (parent[segment] is not JsonObject child)
                {
                    child = new JsonObject();
                    parent[segment] = child;
                }
                parent = child;
            }
            parent[segments[^1]] = JsonNode.Parse("""{"Settings":{"enabled":true,"keep":17},"items":[{"name":"old","keep":true}]}""");
            if (fileOnly)
                await File.WriteAllTextAsync(configPath, expected.ToJsonString());
            else
                await ConfigStoreBootstrap.PopulateAsync(storePath, expected);
            File.Exists(configPath).ShouldBe(fileOnly);
            AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(output),
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors
            });
            AnsiConsole.Console.Profile.Width = 300;
            var commands = new ConfigCommands(new ConfigPathResolver());
            var key = prefix + ".settings.enabled";
            (await commands.ExecuteGetAsync(key, configPath, false, CancellationToken.None)).ShouldBe(0, output.ToString());
            output.ToString().Trim().ShouldBe("true");
            File.Exists(configPath).ShouldBe(fileOnly);
            output.GetStringBuilder().Clear();
            (await commands.ExecuteSetAsync(key, "false", configPath, false, CancellationToken.None)).ShouldBe(0, output.ToString());
            parent[segments[^1]].ShouldBeOfType<JsonObject>()["Settings"].ShouldBeOfType<JsonObject>()["enabled"] = false;
            await AssertPersistedAsync();
            output.GetStringBuilder().Clear();
            (await new ConfigCommands(new ConfigPathResolver()).ExecuteGetAsync(
                key, configPath, false, CancellationToken.None)).ShouldBe(0, output.ToString());
            output.ToString().Trim().ShouldBe("false");
            File.Exists(configPath).ShouldBe(fileOnly);

            var sample = parent[segments[^1]].ShouldBeOfType<JsonObject>();
            foreach (var (suffix, raw) in new[]
            {
                ("items[0].name", "updated"),
                ("Settings.added", "{\"nested\":[]}"),
                ("Settings.enabled", "null"),
                ("newParent.child", "true")
            })
            {
                output.GetStringBuilder().Clear();
                (await commands.ExecuteSetAsync(prefix + "." + suffix, raw,
                    configPath, false, CancellationToken.None)).ShouldBe(0, output.ToString());
                switch (suffix)
                {
                    case "items[0].name":
                        sample["items"].ShouldBeOfType<JsonArray>()[0].ShouldBeOfType<JsonObject>()["name"] = "updated";
                        break;
                    case "Settings.added":
                        sample["Settings"].ShouldBeOfType<JsonObject>()["added"] = JsonNode.Parse(raw);
                        break;
                    case "Settings.enabled":
                        sample["Settings"].ShouldBeOfType<JsonObject>()["enabled"] = null;
                        break;
                    case "newParent.child":
                        sample["newParent"] = JsonNode.Parse("{\"child\":true}");
                        break;
                }
                await AssertPersistedAsync();
            }
            var beforeJson = File.Exists(configPath) ? await File.ReadAllTextAsync(configPath) : null;
            output.GetStringBuilder().Clear();
            var invalid = prefix + ".items[4].name";
            (await commands.ExecuteSetAsync(invalid, "rejected", configPath, false, CancellationToken.None)).ShouldBe(1);
            output.ToString().ShouldContain(invalid);
            await AssertPersistedAsync();
            (File.Exists(configPath) ? await File.ReadAllTextAsync(configPath) : null).ShouldBe(beforeJson);

            if (!fileOnly)
            {
                using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={storePath}"))
                {
                    await connection.OpenAsync();
                    using var checkpoint = connection.CreateCommand();
                    checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                    using var result = await checkpoint.ExecuteReaderAsync();
                    (await result.ReadAsync()).ShouldBeTrue();
                    result.GetInt32(0).ShouldBe(0);
                }
                ConfigStoreBootstrap.ReleaseConnections(storePath);
                var freshHome = Path.Combine(directory, "fresh");
                Directory.CreateDirectory(freshHome);
                var freshConfig = Path.Combine(freshHome, "config.json");
                var freshStore = ConfigStoreBootstrap.ResolveStorePath(freshConfig, new FileSystem());
                try
                {
                    File.Copy(storePath, freshStore);
                    output.GetStringBuilder().Clear();
                    (await new ConfigCommands(new ConfigPathResolver()).ExecuteGetAsync(prefix + ".newParent.child",
                        freshConfig, false, CancellationToken.None)).ShouldBe(0, output.ToString());
                    output.ToString().Trim().ShouldBe("true");
                    File.Exists(freshConfig).ShouldBeFalse();
                    File.Exists(configPath).ShouldBeFalse();
                }
                finally
                {
                    ConfigStoreBootstrap.ReleaseConnections(freshStore);
                }
            }

            async Task AssertPersistedAsync()
            {
                if (fileOnly)
                {
                    JsonNode.DeepEquals(JsonNode.Parse(await File.ReadAllTextAsync(configPath)), expected).ShouldBeTrue();
                    File.Exists(storePath).ShouldBeFalse();
                    return;
                }
                File.Exists(configPath).ShouldBeFalse();
                var entries = await new SqliteConfigStore($"Data Source={storePath}").ReadEntriesAsync();
                var expectedEntries = ConfigDocumentFlattener.Flatten(expected);
                entries.Keys.Order(StringComparer.Ordinal).ShouldBe(expectedEntries.Keys.Order(StringComparer.Ordinal));
                foreach (var (path, entry) in expectedEntries)
                    entries[path].ShouldBe(entry);
            }
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
            ConfigStoreBootstrap.ReleaseConnections(storePath);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
