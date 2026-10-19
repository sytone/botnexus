using System.IO.Abstractions;
using System.Text;
using System.Text.Json.Nodes;
using BotNexus.Cli.Commands;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Spectre.Console;

namespace BotNexus.Cli.Tests;

[CollectionDefinition("Config get console", DisableParallelization = true)]
public sealed class ConfigGetConsoleCollection;

[Collection("Config get console")]
public sealed class ConfigGetCollectionShapeTests
{
    [Theory]
    [InlineData("gateway.fileAccess.allowedReadPaths", "[\"read-a\",\"read-b\"]", "[]")]
    [InlineData("agents.alpha.fileAccess.allowedReadPaths", "[\"read-a\",\"read-b\"]", "[]")]
    [InlineData("agents.defaults.fileAccess.allowedReadPaths", "[\"read-a\",\"read-b\"]", "[]")]
    [InlineData("agents.alpha.toolIds", "[\"tool-a\",\"tool-b\"]", "[]")]
    [InlineData("agents.defaults.toolIds", "[\"tool-a\",\"tool-b\"]", "[]")]
    [InlineData("world.extensions", "{\"sample\":{\"items\":[],\"settings\":{},\"disabled\":null,\"enabled\":true}}", "{}")]
    [InlineData("gateway.extensions", "{\"sample\":{\"items\":[],\"settings\":{},\"disabled\":null,\"enabled\":true}}", "{}")]
    [InlineData("agents.alpha.extensions", "{\"sample\":{\"items\":[],\"settings\":{},\"disabled\":null,\"enabled\":true}}", "{}")]
    [InlineData("agents.defaults.extensions", "{\"sample\":{\"items\":[],\"settings\":{},\"disabled\":null,\"enabled\":true}}", "{}")]
    public async Task ExecuteSetAsync_SqliteOnly_PreservesShapesAndSiblingsAcrossFreshGets(
        string keyPath, string populated, string empty)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"botnexus-cli-set-shape-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, "config.json");
        var storePath = ConfigStoreBootstrap.ResolveStorePath(configPath, new FileSystem());
        var originalConsole = AnsiConsole.Console;
        using var output = new StringWriter();
        try
        {
            var expected = JsonNode.Parse("""
                {
                  "world": {"futureWorld": {"items":[],"options":{},"disabled":null}},
                  "gateway": {"fileAccess":{"allowedWritePaths":["write-sibling"]},"futureGateway":42},
                  "agents": {
                    "defaults":{"toolTimeoutSeconds":123,"fileAccess":{"allowedWritePaths":["default-write"]}},
                    "alpha":{"provider":"copilot","model":"gpt-4.1","displayName":"Alpha","fileAccess":{"allowedWritePaths":["agent-write"]},"futureAgent":{"keep":true}},
                    "beta":{"provider":"copilot","model":"gpt-4.1","displayName":"Beta","toolIds":["beta-tool"],"extensions":{"opaque":{"items":[],"options":{},"disabled":null}}}
                  },
                  "futureRoot":{"array":[1,2],"object":{},"explicitNull":null}
                }
                """).ShouldBeOfType<JsonObject>();
            await ConfigStoreBootstrap.PopulateAsync(storePath, expected);
            AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Out = new AnsiConsoleOutput(output)
            });
            AnsiConsole.Console.Profile.Width = 300;

            // An unrelated real CLI mutation must not materialize an absent collection.
            (await new ConfigCommands(new ConfigPathResolver()).ExecuteSetAsync(
                "agents.alpha.displayName", "Renamed", configPath, false, CancellationToken.None)).ShouldBe(0, output.ToString());
            expected["agents"].ShouldBeOfType<JsonObject>()["alpha"].ShouldBeOfType<JsonObject>()["displayName"] = "Renamed";
            var absentEntries = await ReadEntriesAsync(storePath);
            absentEntries.Keys.ShouldNotContain(keyPath);
            absentEntries.Keys.Any(key => key.StartsWith(keyPath + ".", StringComparison.Ordinal)).ShouldBeFalse();
            AssertEntries(absentEntries, expected);
            configPath = await ForkSqliteOnlyHomeAsync(directory, configPath, storePath, "agents.alpha.displayName", "\"Renamed\"");
            storePath = ConfigStoreBootstrap.ResolveStorePath(configPath, new FileSystem());
            await AssertFreshGetAsync(keyPath, "null", configPath, storePath, output);

            // Replace populated -> empty -> explicit null -> populated, using a new command and
            // checkpointing and copying the SQLite file into a fresh home before every get.
            // The writer may create JSON, but the fresh home must work without that projection.
            foreach (var shape in new[] { populated, empty, "null", populated })
            {
                output.GetStringBuilder().Clear();
                (await new ConfigCommands(new ConfigPathResolver()).ExecuteSetAsync(
                    keyPath, shape, configPath, false, CancellationToken.None)).ShouldBe(0, output.ToString());
                SetExpected(expected, keyPath, shape);
                var entries = await ReadEntriesAsync(storePath);
                AssertEntries(entries, expected);
                if (shape == "null")
                {
                    entries[keyPath].State.ShouldBe(ConfigValueState.ExplicitNull);
                    entries[keyPath].Value.ShouldBeNull();
                }
                else if (shape == empty || empty == "[]")
                {
                    entries[keyPath].State.ShouldBe(ConfigValueState.Value);
                    JsonNode.DeepEquals(JsonNode.Parse(entries[keyPath].Value.ShouldNotBeNull()), JsonNode.Parse(shape)).ShouldBeTrue();
                }
                configPath = await ForkSqliteOnlyHomeAsync(directory, configPath, storePath, keyPath, shape);
                storePath = ConfigStoreBootstrap.ResolveStorePath(configPath, new FileSystem());
                await AssertFreshGetAsync(keyPath, shape, configPath, storePath, output);
            }
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
            foreach (var database in Directory.EnumerateFiles(directory, Path.GetFileName(storePath), SearchOption.AllDirectories))
                ConfigStoreBootstrap.ReleaseConnections(database);
            // CLI composition owns physical configuration watchers. Windows may still have a
            // projection read in flight; require successful cleanup rather than hiding a lock.
            await TestAwait.EventuallyAsync(() =>
            {
                try
                {
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, recursive: true);
                    return !Directory.Exists(directory);
                }
                catch (IOException)
                {
                    return false;
                }
            }, "CLI configuration homes to be removed after projection readers finish");
        }
    }

    private static async Task<string> ForkSqliteOnlyHomeAsync(string directory, string configPath,
        string storePath, string keyPath, string shape)
    {
        // JsonConfigurationWriter patches only changed keys into an absent JSON backend. The
        // complete document lives in SQLite; assert the JSON delta too, then copy only SQLite
        // into a fresh home so JSON cannot satisfy the following get in place of composition.
        File.Exists(configPath).ShouldBeTrue();
        var expectedDelta = new JsonObject();
        var segments = keyPath.Split('.');
        var parent = expectedDelta;
        foreach (var segment in segments[..^1])
        {
            var child = new JsonObject();
            parent[segment] = child;
            parent = child;
        }
        parent[segments[^1]] = JsonNode.Parse(shape);
        var projection = await File.ReadAllTextAsync(configPath);
        JsonNode.DeepEquals(JsonNode.Parse(projection), expectedDelta).ShouldBeTrue(projection);
        using (var connection = new SqliteConnection($"Data Source={storePath}"))
        {
            await connection.OpenAsync();
            using var checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            using var result = await checkpoint.ExecuteReaderAsync();
            (await result.ReadAsync()).ShouldBeTrue();
            result.GetInt32(0).ShouldBe(0);
        }
        ConfigStoreBootstrap.ReleaseConnections(storePath);
        var freshHome = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(freshHome);
        var freshConfigPath = Path.Combine(freshHome, "config.json");
        var freshStorePath = ConfigStoreBootstrap.ResolveStorePath(freshConfigPath, new FileSystem());
        File.Copy(storePath, freshStorePath);
        File.Exists(freshConfigPath).ShouldBeFalse();
        return freshConfigPath;
    }

    private static async Task<IReadOnlyDictionary<string, ConfigEntry>> ReadEntriesAsync(string storePath)
    {
        ConfigStoreBootstrap.ReleaseConnections(storePath);
        return await new SqliteConfigStore($"Data Source={storePath}").ReadEntriesAsync();
    }

    private static void AssertEntries(IReadOnlyDictionary<string, ConfigEntry> actual, JsonObject expected)
    {
        var expectedEntries = ConfigDocumentFlattener.Flatten(expected);
        actual.Keys.OrderBy(key => key, StringComparer.Ordinal).ShouldBe(
            expectedEntries.Keys.OrderBy(key => key, StringComparer.Ordinal));
        foreach (var (key, entry) in expectedEntries)
            actual[key].ShouldBe(entry);
    }

    private static void SetExpected(JsonObject expected, string keyPath, string shape)
    {
        var segments = keyPath.Split('.');
        var parent = expected;
        foreach (var segment in segments[..^1])
            parent = parent[segment].ShouldBeOfType<JsonObject>();
        parent[segments[^1]] = JsonNode.Parse(shape);
    }

    private static async Task AssertFreshGetAsync(string keyPath, string shape, string configPath,
        string storePath, StringWriter output)
    {
        File.Exists(configPath).ShouldBeFalse();
        ConfigStoreBootstrap.ReleaseConnections(storePath);
        output.GetStringBuilder().Clear();
        // defaults is extracted from the Agents dictionary into the public AgentDefaults
        // property by post-configuration; the CLI resolver reads that typed property directly.
        var readPath = keyPath.StartsWith("agents.defaults.", StringComparison.Ordinal)
            ? "agentDefaults." + keyPath["agents.defaults.".Length..] : keyPath;
        (await new ConfigCommands(new ConfigPathResolver()).ExecuteGetAsync(
            readPath, configPath, false, CancellationToken.None)).ShouldBe(0, output.ToString());
        var expected = JsonNode.Parse(shape);
        // The CLI prints strings unquoted, but serializes other values as JSON.
        if (expected is JsonValue scalar && scalar.TryGetValue<string>(out var text))
            output.ToString().Trim().ShouldBe(text);
        else
            JsonNode.DeepEquals(JsonNode.Parse(output.ToString().Trim()), expected)
                .ShouldBeTrue($"Read {readPath}; expected {shape}; actual {output}");
        File.Exists(configPath).ShouldBeFalse();
    }

    [Theory]
    [InlineData("{\"heartbeat\":{}}", "heartbeat.enabled", "true")]
    [InlineData("{\"heartbeat\":{}}", "heartbeat.intervalMinutes", "30")]
    [InlineData("{\"memory\":{}}", "memory.indexing", "\"auto\"")]
    [InlineData("{\"memory\":{\"search\":{}}}", "memory.search.defaultTopK", "10")]
    [InlineData("{\"memory\":{\"search\":{\"temporalDecay\":{}}}}", "memory.search.temporalDecay.halfLifeDays", "30")]
    public async Task ExecuteGetAsync_SqliteOnly_PreservesAuthoredEmptyDefaultObjects(
        string defaultsJson, string readPath, string expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"botnexus-cli-default-object-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, "config.json");
        var storePath = ConfigStoreBootstrap.ResolveStorePath(configPath, new FileSystem());
        var originalConsole = AnsiConsole.Console;
        using var output = new StringWriter();
        try
        {
            var document = JsonNode.Parse("{\"agents\":{\"defaults\":" + defaultsJson + "}}")
                .ShouldBeOfType<JsonObject>();
            await ConfigStoreBootstrap.PopulateAsync(storePath, document);
            AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Out = new AnsiConsoleOutput(output)
            });
            await AssertFreshGetAsync("agents.defaults." + readPath, expected, configPath, storePath, output);
            AssertEntries(await ReadEntriesAsync(storePath), document);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
            ConfigStoreBootstrap.ReleaseConnections(storePath);
            await TestAwait.EventuallyAsync(() =>
            {
                try
                {
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, recursive: true);
                    return !Directory.Exists(directory);
                }
                catch (IOException)
                {
                    return false;
                }
            }, "CLI empty-default-object home to be removed");
        }
    }

    [Fact]
    public void ApplyAuthoritativeRawShape_DefaultObjectsAndCollections_PreservesProviderOverrides()
    {
        using var raw = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"agents":{"defaults":{"toolIds":["raw-tool"],"fileAccess":{"allowedReadPaths":["raw-read"],"allowedWritePaths":["raw-write"],"deniedPaths":["raw-denied"]},"heartbeat":{},"memory":{"search":{}},"extensions":{"sample":{"items":[],"settings":{},"disabled":null}}}}}
            """));
        using var root = (ConfigurationRoot)new ConfigurationBuilder().AddAcceptedRawJsonStream(raw)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["agents:defaults:toolIds:0"] = "overlay-tool",
                ["agents:defaults:fileAccess:allowedReadPaths:0"] = "overlay-read",
                ["agents:defaults:heartbeat:enabled"] = "false",
                ["agents:defaults:memory:enabled"] = "true"
            }).Build();
        var config = root.Get<PlatformConfig>().ShouldNotBeNull();
        new PlatformConfigPostConfigure(root).PostConfigure(null, config);
        var defaults = config.AgentDefaults.ShouldNotBeNull();
        defaults.Heartbeat.ShouldNotBeNull().Enabled.ShouldBeFalse();
        defaults.Heartbeat.IntervalMinutes.ShouldBe(30);
        defaults.Memory.ShouldNotBeNull().Enabled.ShouldBeTrue();
        defaults.Memory.Search.ShouldNotBeNull().DefaultTopK.ShouldBe(10);
        defaults.ToolIds.ShouldNotBeNull().ShouldBe(new[] { "overlay-tool" });
        defaults.FileAccess.ShouldNotBeNull().AllowedReadPaths.ShouldBe(new[] { "overlay-read" });
        defaults.FileAccess.AllowedWritePaths.ShouldBe(new[] { "raw-write" });
        defaults.FileAccess.DeniedPaths.ShouldBe(new[] { "raw-denied" });
        JsonNode.DeepEquals(JsonNode.Parse(defaults.Extensions.ShouldNotBeNull()["sample"].GetRawText()),
            JsonNode.Parse("{\"items\":[],\"settings\":{},\"disabled\":null}")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("[]", "[]")]
    [InlineData("null", "null")]
    public async Task ExecuteGetAsync_SqliteOnly_PrintsPersistedArrayShape(string shape, string expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"botnexus-cli-shape-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var originalConsole = AnsiConsole.Console;
        try
        {
            var configPath = Path.Combine(directory, "config.json");
            var storePath = ConfigStoreBootstrap.ResolveStorePath(configPath, new FileSystem());
            var document = JsonNode.Parse("{\"gateway\":{\"fileAccess\":{\"allowedReadPaths\":" + shape + "}}}")
                .ShouldBeOfType<JsonObject>();
            await ConfigStoreBootstrap.PopulateAsync(storePath, document);
            File.Exists(configPath).ShouldBeFalse();
            using var output = new StringWriter();
            AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Out = new AnsiConsoleOutput(output)
            });
            var command = new ConfigCommands(new ConfigPathResolver());
            (await command.ExecuteGetAsync("gateway.fileAccess.allowedReadPaths", configPath,
                verbose: false, CancellationToken.None)).ShouldBe(0);
            output.ToString().Trim().ShouldBe(expected);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
            ConfigStoreBootstrap.ReleaseConnections(ConfigStoreBootstrap.ResolveStorePath(
                Path.Combine(directory, "config.json"), new FileSystem()));
            Directory.Delete(directory, recursive: true);
        }
    }
}
