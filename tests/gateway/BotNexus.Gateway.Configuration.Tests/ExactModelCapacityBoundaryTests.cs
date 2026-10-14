using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Text;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Configuration.Tests;

public sealed class ExactModelCapacityBoundaryTests : IDisposable
{
    private const string Map = """
        {"Model.1":{"contextWindow":100000,"maxTokens":1000},
         "model.1":{"contextWindow":200000,"maxTokens":2000},
         "vendor:model":{"contextWindow":300000,"maxTokens":3000}}
        """;
    private static string Document(string map) =>
        "{\"providers\":{\"custom\":{\"chat\":{\"modelCapacities\":" + map + "}}}}";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"botnexus-exact-capacity-{Guid.NewGuid():N}");

    public ExactModelCapacityBoundaryTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task SqliteRoundTrip_PreservesExactIdsAndUnequalValues()
    {
        var root = JsonNode.Parse(Document(Map)).ShouldBeOfType<JsonObject>();
        var storePath = Path.Combine(_directory, "config.db");
        await ConfigStoreBootstrap.PopulateAsync(storePath, root);
        var store = new SqliteConfigStore($"Data Source={storePath}");
        var snapshot = await store.ReadSnapshotAsync();
        var rehydrated = ConfigDocumentRehydrator.Rehydrate(snapshot.Entries);
        JsonNode.DeepEquals(root, rehydrated).ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JsonOptionsMonitor_PreservesExactIdsAndUnequalValues(bool physical)
    {
        var path = Path.Combine(_directory, "config.json");
        IOptionsMonitor<PlatformConfig> monitor;
        if (physical)
        {
            File.WriteAllText(path, Document(Map));
            monitor = PlatformConfigurationSources.BuildMonitor(path);
        }
        else
        {
            var fs = new MockFileSystem();
            fs.AddFile(path, new MockFileData(Document(Map)));
            monitor = PlatformConfigurationSources.BuildMonitor(path, fs);
        }
        AssertExact(monitor.CurrentValue);
    }

    [Fact]
    public async Task SqliteOptionsMonitor_StoreMapWinsOverStaleJson()
    {
        var path = Path.Combine(_directory, "config.json");
        File.WriteAllText(path, Document("""{"stale":{"contextWindow":900000}}"""));
        var storePath = ConfigStoreBootstrap.ResolveStorePath(path, new FileSystem());
        await ConfigStoreBootstrap.PopulateAsync(storePath, JsonNode.Parse(Document(Map)).ShouldBeOfType<JsonObject>());
        AssertExact(PlatformConfigurationSources.BuildMonitor(path).CurrentValue);
    }

    [Fact]
    public async Task SqliteOptionsMonitor_AbsentStoreMapInheritsJsonMap()
    {
        var path = Path.Combine(_directory, "config.json");
        File.WriteAllText(path, Document(Map));
        var storePath = ConfigStoreBootstrap.ResolveStorePath(path, new FileSystem());
        await ConfigStoreBootstrap.PopulateAsync(storePath,
            JsonNode.Parse("""{"providers":{"custom":{"chat":{"defaultModel":"other"}}}}""").ShouldBeOfType<JsonObject>());
        var config = PlatformConfigurationSources.BuildMonitor(path).CurrentValue;
        AssertExact(config);
        config.Providers.ShouldNotBeNull()["custom"].Chat.ShouldNotBeNull().DefaultModel.ShouldBe("other");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    public void LaterRawMap_EmptyOrNullSuppressesEarlierMap(string replacement)
    {
        var root = new ConfigurationBuilder()
            .AddAcceptedRawJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(Document(Map))))
            .AddAcceptedRawJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(Document(replacement))))
            .Build();
        using var rootLifetime = root.ShouldBeAssignableTo<IDisposable>();
        using var services = CreateServices(root);
        Capacities(services.GetRequiredService<IOptionsMonitor<PlatformConfig>>().CurrentValue).ShouldBeEmpty();
    }

    [Fact]
    public void LaterProvider_OverridesWholeMapWithoutCaseOrSeparatorLoss()
    {
        var root = new ConfigurationBuilder()
            .AddAcceptedRawJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(Document("{}"))))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["providers:custom:chat:modelCapacities"] = Map,
                ["providers:custom:chat:defaultModel"] = "overlay"
            }).Build();
        using var rootLifetime = root.ShouldBeAssignableTo<IDisposable>();
        using var services = CreateServices(root);
        var config = services.GetRequiredService<IOptionsMonitor<PlatformConfig>>().CurrentValue;
        AssertExact(config);
        config.Providers.ShouldNotBeNull()["custom"].Chat.ShouldNotBeNull().DefaultModel.ShouldBe("overlay");
    }

    [Fact]
    public async Task SqliteOnlyOptionsMonitor_ReloadReplacesExactMapAndNotifies()
    {
        var path = Path.Combine(_directory, "config.json");
        var storePath = ConfigStoreBootstrap.ResolveStorePath(path, new FileSystem());
        await ConfigStoreBootstrap.PopulateAsync(storePath,
            JsonNode.Parse(Document(Map)).ShouldBeOfType<JsonObject>());
        var store = new SqliteConfigStore($"Data Source={storePath}");
        var root = new ConfigurationBuilder().Add(new SqliteConfigurationSource
        {
            Store = store, StartChangeDetection = false
        }).Build();
        using var rootLifetime = root.ShouldBeAssignableTo<IDisposable>();
        using var services = CreateServices(root);
        var monitor = services.GetRequiredService<IOptionsMonitor<PlatformConfig>>();
        AssertExact(monitor.CurrentValue);
        PlatformConfig? notified = null;
        using var subscription = monitor.OnChange(config => notified = config);
        await store.WriteDocumentAsync(JsonNode.Parse(Document(
            """{"vendor:model":{"contextWindow":400000,"maxTokens":4000}}""")).ShouldBeOfType<JsonObject>());
        var provider = root.Providers.OfType<SqliteConfigurationProvider>().ShouldHaveSingleItem();
        (await provider.CheckForChangesAsync()).ShouldBeTrue();
        var capacities = Capacities(monitor.CurrentValue);
        capacities.Count.ShouldBe(1);
        capacities["vendor:model"].ContextWindow.ShouldBe(400000);
        capacities["vendor:model"].MaxTokens.ShouldBe(4000);
        Capacities(notified.ShouldNotBeNull())["vendor:model"].ContextWindow.ShouldBe(400000);
    }

    [Fact]
    public void LaterProvider_FieldOverlayPreservesOtherExactEntriesAndFields()
    {
        var root = new ConfigurationBuilder()
            .AddAcceptedRawJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(Document(Map))))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["providers:custom:chat:modelCapacities:Model.1:maxTokens"] = "5000"
            }).Build();
        using var rootLifetime = root.ShouldBeAssignableTo<IDisposable>();
        using var services = CreateServices(root);
        var capacities = Capacities(services.GetRequiredService<IOptionsMonitor<PlatformConfig>>().CurrentValue);
        capacities.Count.ShouldBe(3);
        capacities["Model.1"].MaxTokens.ShouldBe(5000);
        capacities["Model.1"].ContextWindow.ShouldBe(100000);
        capacities["model.1"].MaxTokens.ShouldBe(2000);
        capacities["vendor:model"].ContextWindow.ShouldBe(300000);
    }

    [Fact]
    public void RawMaps_MaterializeEachProviderIndependently()
    {
        var json = JsonNode.Parse(Document(Map)).ShouldBeOfType<JsonObject>();
        json["providers"].ShouldBeOfType<JsonObject>()["second"] =
            JsonNode.Parse("""{"chat":{"modelCapacities":{"Model.1":{"contextWindow":700000,"maxTokens":7000}}}}""");
        var root = new ConfigurationBuilder()
            .AddAcceptedRawJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json.ToJsonString()))).Build();
        using var rootLifetime = root.ShouldBeAssignableTo<IDisposable>();
        using var services = CreateServices(root);
        var config = services.GetRequiredService<IOptionsMonitor<PlatformConfig>>().CurrentValue;
        AssertExact(config);
        var second = config.Providers.ShouldNotBeNull()["second"].Chat.ShouldNotBeNull().ModelCapacities;
        second.Count.ShouldBe(1);
        second["Model.1"].ContextWindow.ShouldBe(700000);
        second["Model.1"].MaxTokens.ShouldBe(7000);
    }

    private static ServiceProvider CreateServices(IConfiguration root)
    {
        var services = new ServiceCollection();
        services.Configure<PlatformConfig>(root);
        services.AddSingleton<IPostConfigureOptions<PlatformConfig>>(new PlatformConfigPostConfigure(root));
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, ProviderModelCapacityConfig> Capacities(PlatformConfig config) =>
        config.Providers.ShouldNotBeNull()["custom"].Chat.ShouldNotBeNull().ModelCapacities;

    private static void AssertExact(PlatformConfig config)
    {
        var capacities = Capacities(config);
        capacities.Count.ShouldBe(3);
        capacities["Model.1"].ContextWindow.ShouldBe(100000);
        capacities["Model.1"].MaxTokens.ShouldBe(1000);
        capacities["model.1"].ContextWindow.ShouldBe(200000);
        capacities["model.1"].MaxTokens.ShouldBe(2000);
        capacities["vendor:model"].ContextWindow.ShouldBe(300000);
        capacities["vendor:model"].MaxTokens.ShouldBe(3000);
        capacities.ContainsKey("MODEL.1").ShouldBeFalse();
    }

    public void Dispose()
    {
        var storePath = Path.Combine(_directory, "config.db");
        ConfigStoreBootstrap.ReleaseConnections(storePath);
        ConfigStoreBootstrap.ReleaseConnections(ConfigStoreBootstrap.ResolveStorePath(
            Path.Combine(_directory, "config.json"), new FileSystem()));
        Directory.Delete(_directory, recursive: true);
    }
}
