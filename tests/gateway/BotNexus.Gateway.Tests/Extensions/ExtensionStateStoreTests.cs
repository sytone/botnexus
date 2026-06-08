using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Extensions;
using Microsoft.Data.Sqlite;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Tests.Extensions;

public sealed class ExtensionStateStoreTests
{
    #region Test Infrastructure

    private sealed class SqliteTestContext : IAsyncDisposable
    {
        private SqliteTestContext(string tempDirectory, string dbPath, SqliteExtensionStateStore store)
        {
            TempDirectory = tempDirectory;
            DbPath = dbPath;
            Store = store;
        }

        public string TempDirectory { get; }
        public string DbPath { get; }
        public SqliteExtensionStateStore Store { get; }

        public static async Task<SqliteTestContext> CreateAsync()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), "botnexus-ext-state-tests", Guid.NewGuid().ToString("N"));
            var dbPath = Path.Combine(tempDirectory, "extension-state.db");
            var store = new SqliteExtensionStateStore(dbPath, new FileSystem());
            await store.InitializeAsync();
            return new SqliteTestContext(tempDirectory, dbPath, store);
        }

        public async ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (!Directory.Exists(TempDirectory))
                return;

            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.Delete(TempDirectory, recursive: true);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(50);
                }
            }
        }
    }

    #endregion

    #region SQLite Store Tests

    [Fact]
    public async Task Sqlite_InitializeAsync_CreatesDirectoryAndTable()
    {
        await using var context = await SqliteTestContext.CreateAsync();

        Assert.True(Directory.Exists(context.TempDirectory));

        await using var connection = new SqliteConnection($"Data Source={context.DbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";

        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            tables.Add(reader.GetString(0));

        Assert.Contains("extension_state", tables);
    }

    [Fact]
    public async Task Sqlite_SetAndGet_RoundTrips()
    {
        await using var context = await SqliteTestContext.CreateAsync();

        await context.Store.SetAsync("ext-1", "api-key", "secret-123");
        var result = await context.Store.GetAsync("ext-1", "api-key");

        Assert.Equal("secret-123", result);
    }

    [Fact]
    public async Task Sqlite_Get_NonExistentKey_ReturnsNull()
    {
        await using var context = await SqliteTestContext.CreateAsync();

        var result = await context.Store.GetAsync("ext-1", "missing-key");

        Assert.Null(result);
    }

    [Fact]
    public async Task Sqlite_Set_Overwrite_UpdatesValue()
    {
        await using var context = await SqliteTestContext.CreateAsync();

        await context.Store.SetAsync("ext-1", "counter", "1");
        await context.Store.SetAsync("ext-1", "counter", "2");
        var result = await context.Store.GetAsync("ext-1", "counter");

        Assert.Equal("2", result);
    }

    [Fact]
    public async Task Sqlite_Delete_RemovesKey()
    {
        await using var context = await SqliteTestContext.CreateAsync();

        await context.Store.SetAsync("ext-1", "temp", "value");
        await context.Store.DeleteAsync("ext-1", "temp");
        var result = await context.Store.GetAsync("ext-1", "temp");

        Assert.Null(result);
    }

    [Fact]
    public async Task Sqlite_Delete_NonExistentKey_IsNoOp()
    {
        await using var context = await SqliteTestContext.CreateAsync();

        // Should not throw
        await context.Store.DeleteAsync("ext-1", "nonexistent");
    }

    [Fact]
    public async Task Sqlite_ListKeys_ReturnsAllKeysForExtension()
    {
        await using var context = await SqliteTestContext.CreateAsync();

        await context.Store.SetAsync("ext-1", "key-b", "val-b");
        await context.Store.SetAsync("ext-1", "key-a", "val-a");
        await context.Store.SetAsync("ext-2", "other-key", "val-other");

        var keys = await context.Store.ListKeysAsync("ext-1");

        Assert.Equal(2, keys.Count);
        Assert.Equal(["key-a", "key-b"], keys); // alphabetical order
    }

    [Fact]
    public async Task Sqlite_ListKeys_NoKeys_ReturnsEmpty()
    {
        await using var context = await SqliteTestContext.CreateAsync();

        var keys = await context.Store.ListKeysAsync("ext-nonexistent");

        Assert.Empty(keys);
    }

    [Fact]
    public async Task Sqlite_Clear_RemovesAllKeysForExtension()
    {
        await using var context = await SqliteTestContext.CreateAsync();

        await context.Store.SetAsync("ext-1", "key-a", "val-a");
        await context.Store.SetAsync("ext-1", "key-b", "val-b");
        await context.Store.SetAsync("ext-2", "key-c", "val-c");

        await context.Store.ClearAsync("ext-1");

        var ext1Keys = await context.Store.ListKeysAsync("ext-1");
        var ext2Keys = await context.Store.ListKeysAsync("ext-2");

        Assert.Empty(ext1Keys);
        Assert.Single(ext2Keys);
    }

    [Fact]
    public async Task Sqlite_IsolatesBetweenExtensions()
    {
        await using var context = await SqliteTestContext.CreateAsync();

        await context.Store.SetAsync("ext-1", "shared-key", "value-1");
        await context.Store.SetAsync("ext-2", "shared-key", "value-2");

        var result1 = await context.Store.GetAsync("ext-1", "shared-key");
        var result2 = await context.Store.GetAsync("ext-2", "shared-key");

        Assert.Equal("value-1", result1);
        Assert.Equal("value-2", result2);
    }

    [Fact]
    public async Task Sqlite_ConcurrentWrites_AreSafe()
    {
        await using var context = await SqliteTestContext.CreateAsync();

        var tasks = Enumerable.Range(0, 50).Select(i =>
            context.Store.SetAsync("ext-1", $"key-{i}", $"value-{i}"));

        await Task.WhenAll(tasks);

        var keys = await context.Store.ListKeysAsync("ext-1");
        Assert.Equal(50, keys.Count);
    }

    [Fact]
    public async Task Sqlite_InitializeAsync_IsIdempotent()
    {
        await using var context = await SqliteTestContext.CreateAsync();

        // Initialize was already called in CreateAsync; call again
        await context.Store.InitializeAsync();
        await context.Store.InitializeAsync();

        // Should still work
        await context.Store.SetAsync("ext-1", "key", "value");
        var result = await context.Store.GetAsync("ext-1", "key");
        Assert.Equal("value", result);
    }

    #endregion

    #region InMemory Store Tests

    [Fact]
    public async Task InMemory_SetAndGet_RoundTrips()
    {
        var store = new InMemoryExtensionStateStore();

        await store.SetAsync("ext-1", "key", "value");
        var result = await store.GetAsync("ext-1", "key");

        Assert.Equal("value", result);
    }

    [Fact]
    public async Task InMemory_Get_NonExistentKey_ReturnsNull()
    {
        var store = new InMemoryExtensionStateStore();

        var result = await store.GetAsync("ext-1", "missing");

        Assert.Null(result);
    }

    [Fact]
    public async Task InMemory_Set_Overwrite_UpdatesValue()
    {
        var store = new InMemoryExtensionStateStore();

        await store.SetAsync("ext-1", "key", "original");
        await store.SetAsync("ext-1", "key", "updated");
        var result = await store.GetAsync("ext-1", "key");

        Assert.Equal("updated", result);
    }

    [Fact]
    public async Task InMemory_Delete_RemovesKey()
    {
        var store = new InMemoryExtensionStateStore();

        await store.SetAsync("ext-1", "key", "value");
        await store.DeleteAsync("ext-1", "key");
        var result = await store.GetAsync("ext-1", "key");

        Assert.Null(result);
    }

    [Fact]
    public async Task InMemory_Delete_NonExistentKey_IsNoOp()
    {
        var store = new InMemoryExtensionStateStore();

        // Should not throw
        await store.DeleteAsync("ext-1", "missing");
    }

    [Fact]
    public async Task InMemory_ListKeys_ReturnsOrderedKeys()
    {
        var store = new InMemoryExtensionStateStore();

        await store.SetAsync("ext-1", "z-key", "val");
        await store.SetAsync("ext-1", "a-key", "val");
        await store.SetAsync("ext-2", "other", "val");

        var keys = await store.ListKeysAsync("ext-1");

        Assert.Equal(["a-key", "z-key"], keys);
    }

    [Fact]
    public async Task InMemory_ListKeys_NoKeys_ReturnsEmpty()
    {
        var store = new InMemoryExtensionStateStore();

        var keys = await store.ListKeysAsync("ext-unknown");

        Assert.Empty(keys);
    }

    [Fact]
    public async Task InMemory_Clear_RemovesAllKeysForExtension()
    {
        var store = new InMemoryExtensionStateStore();

        await store.SetAsync("ext-1", "a", "1");
        await store.SetAsync("ext-1", "b", "2");
        await store.SetAsync("ext-2", "c", "3");

        await store.ClearAsync("ext-1");

        var ext1Keys = await store.ListKeysAsync("ext-1");
        var ext2Keys = await store.ListKeysAsync("ext-2");

        Assert.Empty(ext1Keys);
        Assert.Single(ext2Keys);
    }

    [Fact]
    public async Task InMemory_IsolatesBetweenExtensions()
    {
        var store = new InMemoryExtensionStateStore();

        await store.SetAsync("ext-1", "key", "val-1");
        await store.SetAsync("ext-2", "key", "val-2");

        Assert.Equal("val-1", await store.GetAsync("ext-1", "key"));
        Assert.Equal("val-2", await store.GetAsync("ext-2", "key"));
    }

    #endregion
}
