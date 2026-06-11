using System.IO.Abstractions.TestingHelpers;
using Xunit;

namespace BotNexus.Memory.Tests;

public sealed class SharedMemoryStoreRegistryTests : IAsyncDisposable
{
    private readonly MockFileSystem _fileSystem = new();
    private readonly List<SharedMemoryStoreConfig> _configs;
    private readonly SharedMemoryStoreRegistry _registry;

    public SharedMemoryStoreRegistryTests()
    {
        _configs =
        [
            new SharedMemoryStoreConfig
            {
                Name = "platform-knowledge",
                Description = "Platform decisions",
                Writers = ["farnsworth", "nova"],
                Readers = ["*"],
                RetentionDays = null
            },
            new SharedMemoryStoreConfig
            {
                Name = "team-procedures",
                Description = "SOPs",
                Writers = ["*"],
                Readers = ["*"],
                RetentionDays = 365
            },
            new SharedMemoryStoreConfig
            {
                Name = "restricted",
                Description = "Admin only",
                Writers = ["admin"],
                Readers = ["admin", "farnsworth"]
            }
        ];

        _registry = new SharedMemoryStoreRegistry(_configs, "/memory", _fileSystem);
    }

    public ValueTask DisposeAsync() => _registry.DisposeAsync();

    [Fact]
    public void GetStore_ReturnsStoreForConfiguredName()
    {
        var store = _registry.GetStore("platform-knowledge");
        Assert.NotNull(store);
    }

    [Fact]
    public void GetStore_ReturnsNullForUnknownName()
    {
        var store = _registry.GetStore("nonexistent");
        Assert.Null(store);
    }

    [Fact]
    public void GetStore_CaseInsensitive()
    {
        var store = _registry.GetStore("Platform-Knowledge");
        Assert.NotNull(store);
    }

    [Fact]
    public void GetStore_ReturnsSameInstanceOnRepeatedCalls()
    {
        var store1 = _registry.GetStore("platform-knowledge");
        var store2 = _registry.GetStore("platform-knowledge");
        Assert.Same(store1, store2);
    }

    [Fact]
    public void CanRead_WildcardAllowsAnyAgent()
    {
        Assert.True(_registry.CanRead("random-agent", "platform-knowledge"));
        Assert.True(_registry.CanRead("random-agent", "team-procedures"));
    }

    [Fact]
    public void CanRead_ExplicitListEnforced()
    {
        Assert.True(_registry.CanRead("admin", "restricted"));
        Assert.True(_registry.CanRead("farnsworth", "restricted"));
        Assert.False(_registry.CanRead("nova", "restricted"));
    }

    [Fact]
    public void CanRead_ReturnsFalseForUnknownStore()
    {
        Assert.False(_registry.CanRead("farnsworth", "nonexistent"));
    }

    [Fact]
    public void CanWrite_WildcardAllowsAnyAgent()
    {
        Assert.True(_registry.CanWrite("random-agent", "team-procedures"));
    }

    [Fact]
    public void CanWrite_ExplicitListEnforced()
    {
        Assert.True(_registry.CanWrite("farnsworth", "platform-knowledge"));
        Assert.True(_registry.CanWrite("nova", "platform-knowledge"));
        Assert.False(_registry.CanWrite("random-agent", "platform-knowledge"));
    }

    [Fact]
    public void CanWrite_ReturnsFalseForUnknownStore()
    {
        Assert.False(_registry.CanWrite("farnsworth", "nonexistent"));
    }

    [Fact]
    public void CanWrite_AdminOnlyStore()
    {
        Assert.True(_registry.CanWrite("admin", "restricted"));
        Assert.False(_registry.CanWrite("farnsworth", "restricted"));
        Assert.False(_registry.CanWrite("nova", "restricted"));
    }

    [Fact]
    public void GetReadableStores_WildcardReturnsAll()
    {
        var stores = _registry.GetReadableStores("random-agent");
        // platform-knowledge and team-procedures have Readers=["*"]
        Assert.Contains("platform-knowledge", stores);
        Assert.Contains("team-procedures", stores);
        Assert.DoesNotContain("restricted", stores);
    }

    [Fact]
    public void GetReadableStores_ExplicitAgent()
    {
        var stores = _registry.GetReadableStores("farnsworth");
        Assert.Contains("platform-knowledge", stores);
        Assert.Contains("team-procedures", stores);
        Assert.Contains("restricted", stores);
    }

    [Fact]
    public void GetWritableStores_ExplicitAgent()
    {
        var stores = _registry.GetWritableStores("farnsworth");
        Assert.Contains("platform-knowledge", stores);
        Assert.Contains("team-procedures", stores);
        Assert.DoesNotContain("restricted", stores);
    }

    [Fact]
    public void GetWritableStores_AdminAgent()
    {
        var stores = _registry.GetWritableStores("admin");
        Assert.DoesNotContain("platform-knowledge", stores); // admin not in Writers
        Assert.Contains("team-procedures", stores); // wildcard
        Assert.Contains("restricted", stores); // explicit
    }

    [Fact]
    public void GetAllConfigs_ReturnsAllConfigured()
    {
        var configs = _registry.GetAllConfigs();
        Assert.Equal(3, configs.Count);
    }

    [Fact]
    public void CanRead_CaseInsensitiveAgentId()
    {
        Assert.True(_registry.CanRead("FARNSWORTH", "restricted"));
        Assert.True(_registry.CanWrite("NOVA", "platform-knowledge"));
    }

    [Fact]
    public async Task EmptyAccessList_DeniesAll()
    {
        var configs = new List<SharedMemoryStoreConfig>
        {
            new() { Name = "empty", Writers = [], Readers = [] }
        };
        await using var registry = new SharedMemoryStoreRegistry(configs, "/memory", _fileSystem);
        Assert.False(registry.CanRead("anyone", "empty"));
        Assert.False(registry.CanWrite("anyone", "empty"));
    }
}
