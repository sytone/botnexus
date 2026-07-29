using BotNexus.Gateway.Nav;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="SqliteNavOrderStore"/>: the server-side persistence for per-user
/// portal nav-order overrides (#2236, slice 5 of #2231). Verifies defaults, override round-trips
/// across a fresh store instance (i.e. survives a restart), and reset-to-default.
/// </summary>
public sealed class SqliteNavOrderStoreTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public SqliteNavOrderStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "botnexus-navorder-tests", Guid.NewGuid().ToString("N"));
        _dbPath = Path.Combine(_tempDir, "nav-order.db");
    }

    private SqliteNavOrderStore NewStore() => new(_dbPath, new FileSystem());

    [Fact]
    public async Task ListAsync_WithNoOverrides_ReturnsAllBuiltinDefaults()
    {
        var store = NewStore();

        var items = await store.ListAsync();

        Assert.Equal(NavOrderDefaults.Defaults.Count, items.Count);
        foreach (var (key, order) in NavOrderDefaults.Defaults)
        {
            var match = items.Single(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(order, match.Order);
        }
    }

    [Fact]
    public async Task ListAsync_ReturnsItemsSortedByEffectiveOrder()
    {
        var store = NewStore();

        var items = await store.ListAsync();

        var orders = items.Select(i => i.Order).ToList();
        Assert.Equal(orders.OrderBy(o => o).ToList(), orders);
    }

    [Fact]
    public async Task DefaultOrder_PlacesToolsAboveChat()
    {
        var store = NewStore();

        var items = await store.ListAsync();

        var toolsIndex = items.ToList().FindIndex(i => i.Key == NavOrderDefaults.Tools);
        var chatIndex = items.ToList().FindIndex(i => i.Key == NavOrderDefaults.Chat);
        Assert.True(toolsIndex >= 0 && chatIndex >= 0);
        Assert.True(toolsIndex < chatIndex, "Tools default order must sit above Chat.");
    }

    [Fact]
    public async Task SetOrderAsync_OverridesEffectiveOrderForKey()
    {
        var store = NewStore();

        await store.SetOrderAsync(NavOrderDefaults.Tools, 5);

        var items = await store.ListAsync();
        var tools = items.Single(i => i.Key == NavOrderDefaults.Tools);
        Assert.Equal(5, tools.Order);
        // Lowering Tools to 5 (below Activity's default 10) moves it above Activity. Home (#2535)
        // defaults to 5 and sorts first on the key tie-break, so assert the relative order rather
        // than an absolute index.
        var toolsIndex = items.ToList().FindIndex(i => i.Key == NavOrderDefaults.Tools);
        var activityIndex = items.ToList().FindIndex(i => i.Key == NavOrderDefaults.Activity);
        Assert.True(toolsIndex < activityIndex, "Lowering Tools order must move it above Activity.");
    }

    [Fact]
    public async Task SetOrderAsync_PersistsAcrossStoreInstances()
    {
        // A fresh store instance over the same database simulates a gateway restart: the override
        // must roam with the user and survive the restart.
        var first = NewStore();
        await first.SetOrderAsync(NavOrderDefaults.Chat, 1);

        var second = NewStore();
        var items = await second.ListAsync();

        var chat = items.Single(i => i.Key == NavOrderDefaults.Chat);
        Assert.Equal(1, chat.Order);
        Assert.Equal(NavOrderDefaults.Chat, items[0].Key);
    }

    [Fact]
    public async Task SetOrderAsync_SecondCallUpdatesExistingOverride()
    {
        var store = NewStore();

        await store.SetOrderAsync(NavOrderDefaults.Tools, 5);
        await store.SetOrderAsync(NavOrderDefaults.Tools, 99);

        var items = await store.ListAsync();
        var tools = items.Single(i => i.Key == NavOrderDefaults.Tools);
        Assert.Equal(99, tools.Order);
    }

    [Fact]
    public async Task ResetAsync_RevertsKeyToBuiltinDefault()
    {
        var store = NewStore();
        await store.SetOrderAsync(NavOrderDefaults.Tools, 5);

        await store.ResetAsync(NavOrderDefaults.Tools);

        var items = await store.ListAsync();
        var tools = items.Single(i => i.Key == NavOrderDefaults.Tools);
        Assert.Equal(NavOrderDefaults.Defaults[NavOrderDefaults.Tools], tools.Order);
    }

    [Fact]
    public async Task ResetAsync_ForKeyWithoutOverride_IsIdempotent()
    {
        var store = NewStore();

        await store.ResetAsync(NavOrderDefaults.Cron);

        var items = await store.ListAsync();
        Assert.Equal(NavOrderDefaults.Defaults.Count, items.Count);
    }

    public async ValueTask DisposeAsync()
    {
        await Task.CompletedTask;
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; a locked SQLite file on Windows must not fail the test run.
        }
    }
}
