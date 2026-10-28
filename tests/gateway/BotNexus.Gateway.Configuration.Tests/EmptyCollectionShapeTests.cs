using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Configuration.Tests;

public sealed class EmptyCollectionShapeTests
{
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task SqliteOnly_FreshCompositionPreservesArrayShape(string shape)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"botnexus-empty-shape-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var storePath = Path.Combine(directory, "config.sqlite");
            var json = "{\"gateway\":{\"fileAccess\":{\"allowedReadPaths\":" + shape + "}}}";
            await ConfigStoreBootstrap.PopulateAsync(storePath, JsonNode.Parse(json).ShouldBeOfType<JsonObject>());
            var store = new SqliteConfigStore($"Data Source={storePath};Pooling=False");
            using var root = new ConfigurationRoot([new SqliteConfigurationProvider(store, startChangeDetection: false)]);
            using var services = Services(root);
            AssertShape(services.GetRequiredService<IOptionsMonitor<PlatformConfig>>().CurrentValue,
                "gateway.fileAccess.allowedReadPaths", shape);
        }
        finally
        {
            ConfigStoreBootstrap.ReleaseConnections(Path.Combine(directory, "config.sqlite"));
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("{\"gateway\":{\"fileAccess\":{\"allowedReadPaths\":[]}}}", "gateway.fileAccess.allowedReadPaths", "[]")]
    [InlineData("{\"gateway\":{\"apiKeys\":{}}}", "gateway.apiKeys", "{}")]
    [InlineData("{\"agents\":{\"worker\":{\"toolIds\":[]}}}", "agents.worker.toolIds", "[]")]
    [InlineData("{\"agents\":{\"worker\":{\"extensions\":{}}}}", "agents.worker.extensions", "{}")]
    [InlineData("{\"agents\":{\"worker\":{\"extensions\":null}}}", "agents.worker.extensions", "null")]
    [InlineData("{\"agents\":{\"worker\":{\"fileAccess\":{\"allowedReadPaths\":[]}}}}", "agents.worker.fileAccess.allowedReadPaths", "[]")]
    [InlineData("{\"agents\":{\"defaults\":{\"fileAccess\":{\"allowedReadPaths\":[]}}}}", "agentDefaults.fileAccess.allowedReadPaths", "[]")]
    [InlineData("{\"gateway\":{\"extensions\":{}}}", "gateway.extensions", "{}")]
    [InlineData("{\"world\":{\"extensions\":{}}}", "world.extensions", "{}")]
    [InlineData("{\"agents\":{\"defaults\":{\"toolIds\":[],\"extensions\":{}}}}", "agentDefaults.toolIds", "[]")]
    [InlineData("{\"agents\":{\"defaults\":{\"extensions\":{}}}}", "agentDefaults.extensions", "{}")]
    [InlineData("{\"world\":{\"extensions\":{\"sample\":{\"paths\":[],\"map\":{},\"missing\":null}}}}", "world.extensions.sample", "{\"paths\":[],\"map\":{},\"missing\":null}")]
    public void OptionsAndCliResolver_PreserveNestedShapes(string json, string path, string expected)
    {
        using var root = Root(json);
        using var services = Services(root);
        AssertShape(services.GetRequiredService<IOptionsMonitor<PlatformConfig>>().CurrentValue, path, expected);
    }

    [Theory]
    [InlineData("[]", "[]")]
    [InlineData("null", "null")]
    public void LaterRawShape_SuppressesEarlierCollection(string replacement, string expected)
    {
        using var root = Root("{\"gateway\":{\"fileAccess\":{\"allowedReadPaths\":[\"old\"]}}}",
            "{\"gateway\":{\"fileAccess\":{\"allowedReadPaths\":" + replacement + "}}}");
        using var services = Services(root);
        AssertShape(services.GetRequiredService<IOptionsMonitor<PlatformConfig>>().CurrentValue,
            "gateway.fileAccess.allowedReadPaths", expected);
    }

    [Theory]
    [InlineData("{}", "{\"key\":{\"name\":\"old\"}}", "gateway.apiKeys", "{\"gateway\":{\"apiKeys\":")]
    [InlineData("null", "{\"key\":{\"name\":\"old\"}}", "gateway.apiKeys", "{\"gateway\":{\"apiKeys\":")]
    public void LaterRawDictionary_SuppressesEarlierEntries(string shape, string original, string path, string prefix)
    {
        using var root = Root(prefix + original + "}}", prefix + shape + "}}");
        using var services = Services(root);
        AssertShape(services.GetRequiredService<IOptionsMonitor<PlatformConfig>>().CurrentValue, path, shape);
    }

    [Fact]
    public void LaterRawAbsentCollection_DoesNotEraseEarlierEmptyArray()
    {
        using var root = Root("{\"gateway\":{\"fileAccess\":{\"allowedReadPaths\":[]}}}",
            "{\"gateway\":{\"fileAccess\":{\"allowedWritePaths\":[\"write\"]}}}");
        using var services = Services(root);
        AssertShape(services.GetRequiredService<IOptionsMonitor<PlatformConfig>>().CurrentValue,
            "gateway.fileAccess.allowedReadPaths", "[]");
    }

    [Fact]
    public void StartupRawShapeEntryPoint_RestoresEmptyArray()
    {
        using var root = Root("{\"gateway\":{\"fileAccess\":{\"allowedReadPaths\":[]}}}");
        var config = root.Get<PlatformConfig>().ShouldNotBeNull();
        PlatformConfigPostConfigure.ApplyAuthoritativeRawShape(root, config);
        AssertShape(config, "gateway.fileAccess.allowedReadPaths", "[]");
    }

    [Fact]
    public void LaterProvider_ArrayChildWinsOverRawEmptyArray()
    {
        var builder = new ConfigurationBuilder().AddAcceptedRawJsonStream(Stream(
            "{\"gateway\":{\"fileAccess\":{\"allowedReadPaths\":[]}}}"));
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["gateway:fileAccess:allowedReadPaths:0"] = "overlay"
        });
        using var root = (ConfigurationRoot)builder.Build();
        using var services = Services(root);
        AssertShape(services.GetRequiredService<IOptionsMonitor<PlatformConfig>>().CurrentValue,
            "gateway.fileAccess.allowedReadPaths", "[\"overlay\"]");
    }

    [Theory]
    [InlineData("[]", "0")]
    [InlineData("null", "0")]
    [InlineData("[]", "2")]
    [InlineData("null", "2")]
    public void RawResetThenArrayChildOverlay_DropsEarlierSiblings(string shape, string childIndex)
    {
        var builder = new ConfigurationBuilder()
            .AddAcceptedRawJsonStream(Stream("{\"gateway\":{\"fileAccess\":{\"allowedReadPaths\":[\"old-0\",\"old-1\"]}}}"))
            .AddAcceptedRawJsonStream(Stream("{\"gateway\":{\"fileAccess\":{\"allowedReadPaths\":" + shape + "}}}"));
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["gateway:fileAccess:allowedReadPaths:" + childIndex] = "overlay"
        });
        using var root = (ConfigurationRoot)builder.Build();
        using var services = Services(root);
        AssertShape(services.GetRequiredService<IOptionsMonitor<PlatformConfig>>().CurrentValue,
            "gateway.fileAccess.allowedReadPaths", "[\"overlay\"]");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    public void RawResetThenDictionaryChildOverlay_DropsEarlierEntries(string shape)
    {
        var builder = new ConfigurationBuilder()
            .AddAcceptedRawJsonStream(Stream("{\"gateway\":{\"apiKeys\":{\"old\":{\"name\":\"stale\"}}}}"))
            .AddAcceptedRawJsonStream(Stream("{\"gateway\":{\"apiKeys\":" + shape + "}}"));
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["gateway:apiKeys:new:name"] = "overlay"
        });
        using var root = (ConfigurationRoot)builder.Build();
        using var services = Services(root);
        var keys = services.GetRequiredService<IOptionsMonitor<PlatformConfig>>().CurrentValue
            .Gateway.ShouldNotBeNull().ApiKeys.ShouldNotBeNull();
        keys.Keys.ShouldBe(["new"]);
    }

    [Theory]
    [InlineData("{}", false, false)]
    [InlineData("null", false, false)]
    [InlineData("{}", false, true)]
    [InlineData("null", false, true)]
    [InlineData("{}", true, false)]
    [InlineData("null", true, false)]
    [InlineData("{}", true, true)]
    [InlineData("null", true, true)]
    public async Task EarlierResetThenPopulatedExactMap_PreservesLaterRawMap(
        string shape, bool firstSqlite, bool secondSqlite)
    {
        const string map = "{\"Model.1\":{\"contextWindow\":100000,\"maxTokens\":1000},"
            + "\"model.1\":{\"contextWindow\":200000,\"maxTokens\":2000},"
            + "\"vendor:model\":{\"contextWindow\":300000,\"maxTokens\":3000}}";
        static string Document(string value) =>
            "{\"providers\":{\"custom\":{\"chat\":{\"modelCapacities\":" + value + "}}}}";
        var directory = Path.Combine(Path.GetTempPath(), $"botnexus-map-shape-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var builder = new ConfigurationBuilder();
            foreach (var (json, sqlite, name) in new[]
            {
                (Document(shape), firstSqlite, "first"), (Document(map), secondSqlite, "second")
            })
            {
                if (!sqlite)
                {
                    builder.AddAcceptedRawJsonStream(Stream(json));
                    continue;
                }
                var path = Path.Combine(directory, name + ".sqlite");
                await ConfigStoreBootstrap.PopulateAsync(path, JsonNode.Parse(json).ShouldBeOfType<JsonObject>());
                builder.Add(new FixedSqliteSource(new SqliteConfigStore($"Data Source={path};Pooling=False")));
            }
            using var root = (ConfigurationRoot)builder.Build();
            using var services = Services(root);
            var config = services.GetRequiredService<IOptionsMonitor<PlatformConfig>>().CurrentValue;
            var capacities = config.Providers.ShouldNotBeNull()["custom"].Chat.ShouldNotBeNull().ModelCapacities;
            capacities.Count.ShouldBe(3);
            capacities["Model.1"].ContextWindow.ShouldBe(100000);
            capacities["Model.1"].MaxTokens.ShouldBe(1000);
            capacities["model.1"].ContextWindow.ShouldBe(200000);
            capacities["model.1"].MaxTokens.ShouldBe(2000);
            capacities["vendor:model"].ContextWindow.ShouldBe(300000);
            capacities["vendor:model"].MaxTokens.ShouldBe(3000);
            capacities.ContainsKey("MODEL.1").ShouldBeFalse();
        }
        finally
        {
            foreach (var name in new[] { "first", "second" })
                ConfigStoreBootstrap.ReleaseConnections(Path.Combine(directory, name + ".sqlite"));
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    public void EarlierExtensionReset_PreservesLaterOpaqueJson(string shape)
    {
        const string payload = "{\"paths\":[],\"map\":{},\"missing\":null,\"literal\":\"true\"}";
        using var root = Root("{\"world\":{\"extensions\":" + shape + "}}",
            "{\"world\":{\"extensions\":{\"sample\":" + payload + "}}}");
        using var services = Services(root);
        AssertShape(services.GetRequiredService<IOptionsMonitor<PlatformConfig>>().CurrentValue,
            "world.extensions.sample", payload);
    }

    [Fact]
    public void LaterWholeNull_AfterRawResetSuppressesEarlierList()
    {
        var builder = new ConfigurationBuilder()
            .AddAcceptedRawJsonStream(Stream("{\"gateway\":{\"fileAccess\":{\"allowedReadPaths\":[\"old\"]}}}"))
            .AddAcceptedRawJsonStream(Stream("{\"gateway\":{\"fileAccess\":{\"allowedReadPaths\":[]}}}"));
        builder.AddInMemoryCollection(new Dictionary<string, string?> { ["gateway:fileAccess:allowedReadPaths"] = null });
        using var root = (ConfigurationRoot)builder.Build();
        using var services = Services(root);
        AssertShape(services.GetRequiredService<IOptionsMonitor<PlatformConfig>>().CurrentValue,
            "gateway.fileAccess.allowedReadPaths", "null");
    }

    [Fact]
    public void AbsentCollection_RemainsNull()
    {
        using var root = Root("{\"gateway\":{\"fileAccess\":{\"allowedWritePaths\":[\"write\"]}}}");
        using var services = Services(root);
        AssertShape(services.GetRequiredService<IOptionsMonitor<PlatformConfig>>().CurrentValue,
            "gateway.fileAccess.allowedReadPaths", "null");
    }

    [Fact]
    public async Task SqliteReload_PopulatedThenEmptyThenNullPreservesShapeAndNotifies()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"botnexus-shape-reload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var storePath = Path.Combine(directory, "config.sqlite");
            static JsonObject Document(string value) => JsonNode.Parse(
                "{\"gateway\":{\"fileAccess\":{\"allowedReadPaths\":" + value + "}}}").ShouldBeOfType<JsonObject>();
            await ConfigStoreBootstrap.PopulateAsync(storePath, Document("[\"original\"]"));
            var store = new SqliteConfigStore($"Data Source={storePath};Pooling=False");
            using var provider = new SqliteConfigurationProvider(store, startChangeDetection: false);
            using var root = new ConfigurationRoot([provider]);
            using var services = Services(root);
            var monitor = services.GetRequiredService<IOptionsMonitor<PlatformConfig>>();
            AssertShape(monitor.CurrentValue, "gateway.fileAccess.allowedReadPaths", "[\"original\"]");
            var notifications = new List<PlatformConfig>();
            using var subscription = monitor.OnChange(config => notifications.Add(config));
            foreach (var shape in new[] { "[]", "null", "[\"new\"]" })
            {
                await store.WriteDocumentAsync(Document(shape));
                (await provider.CheckForChangesAsync()).ShouldBeTrue();
                AssertShape(monitor.CurrentValue, "gateway.fileAccess.allowedReadPaths", shape);
                AssertShape(notifications.Last(), "gateway.fileAccess.allowedReadPaths", shape);
            }
            notifications.Count.ShouldBe(3);
        }
        finally
        {
            ConfigStoreBootstrap.ReleaseConnections(Path.Combine(directory, "config.sqlite"));
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ConfigurationRoot Root(params string[] documents)
    {
        var builder = new ConfigurationBuilder();
        foreach (var json in documents)
            builder.AddAcceptedRawJsonStream(Stream(json));
        return (ConfigurationRoot)builder.Build();
    }

    private sealed class FixedSqliteSource(IConfigStore store) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder)
            => new SqliteConfigurationProvider(store, startChangeDetection: false);
    }

    private static MemoryStream Stream(string json) => new(Encoding.UTF8.GetBytes(json));

    private static ServiceProvider Services(IConfiguration root)
    {
        var services = new ServiceCollection();
        services.Configure<PlatformConfig>(root);
        services.AddSingleton<IPostConfigureOptions<PlatformConfig>>(new PlatformConfigPostConfigure(root));
        return services.BuildServiceProvider();
    }

    private static void AssertShape(PlatformConfig config, string path, string expected)
    {
        var resolver = new ConfigPathResolver();
        resolver.TryGetValue(config, path, out var value, out var error).ShouldBeTrue(error);
        var actual = value is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(value);
        JsonNode.DeepEquals(JsonNode.Parse(actual), JsonNode.Parse(expected)).ShouldBeTrue(
            $"{path}: expected {expected}, actual {actual}");
    }
}
