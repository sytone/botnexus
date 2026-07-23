using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Tools;
using Microsoft.Data.Sqlite;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Tests;

public sealed class SqliteToolStoreTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public SqliteToolStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "botnexus-tools-tests", Guid.NewGuid().ToString("N"));
        _dbPath = Path.Combine(_tempDir, "tools.db");
    }

    private SqliteToolStore NewStore() => new(_dbPath, new FileSystem());

    private static ToolDefinition CreateTool(string id, string name = "Example", int order = 0)
        => new()
        {
            Id = ToolId.From(id),
            Name = name,
            Url = "https://example.com/" + id,
            Icon = "??",
            Order = order,
            SandboxEnabled = true
        };

    [Fact]
    public async Task InitializeAsync_CreatesToolsTable()
    {
        var store = NewStore();
        await store.InitializeAsync();

        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";

        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            tables.Add(reader.GetString(0));

        tables.ShouldContain("tools");
    }

    [Fact]
    public async Task CreateAsync_StoresAndRetrievesById()
    {
        var store = NewStore();
        var tool = CreateTool("tool-1", "My Tool", order: 3);

        await store.CreateAsync(tool);
        var loaded = await store.GetAsync(ToolId.From("tool-1"));

        loaded.ShouldNotBeNull();
        loaded!.Id.Value.ShouldBe("tool-1");
        loaded.Name.ShouldBe("My Tool");
        loaded.Url.ShouldBe("https://example.com/tool-1");
        loaded.Icon.ShouldBe("??");
        loaded.Order.ShouldBe(3);
        loaded.SandboxEnabled.ShouldBeTrue();
        loaded.CreatedAt.ShouldNotBe(default);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenMissing()
    {
        var store = NewStore();
        (await store.GetAsync(ToolId.From("nope"))).ShouldBeNull();
    }

    [Fact]
    public async Task ListAsync_ReturnsToolsOrderedByOrder()
    {
        var store = NewStore();
        await store.CreateAsync(CreateTool("tool-b", "B", order: 2));
        await store.CreateAsync(CreateTool("tool-a", "A", order: 1));
        await store.CreateAsync(CreateTool("tool-c", "C", order: 3));

        var tools = await store.ListAsync();

        tools.Select(t => t.Id.Value).ShouldBe(["tool-a", "tool-b", "tool-c"]);
    }

    [Fact]
    public async Task UpdateAsync_ModifiesFields()
    {
        var store = NewStore();
        await store.CreateAsync(CreateTool("tool-1", "Original", order: 1));

        var stored = await store.GetAsync(ToolId.From("tool-1"));
        var updated = stored! with
        {
            Name = "Renamed",
            Url = "https://changed.example",
            Icon = "?",
            Order = 9,
            SandboxEnabled = false
        };
        await store.UpdateAsync(updated);

        var reloaded = await store.GetAsync(ToolId.From("tool-1"));
        reloaded.ShouldNotBeNull();
        reloaded!.Name.ShouldBe("Renamed");
        reloaded.Url.ShouldBe("https://changed.example");
        reloaded.Icon.ShouldBe("?");
        reloaded.Order.ShouldBe(9);
        reloaded.SandboxEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteAsync_RemovesTool()
    {
        var store = NewStore();
        await store.CreateAsync(CreateTool("tool-1"));

        await store.DeleteAsync(ToolId.From("tool-1"));

        (await store.GetAsync(ToolId.From("tool-1"))).ShouldBeNull();
        (await store.ListAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_IsIdempotent()
    {
        var store = NewStore();
        await Should.NotThrowAsync(async () => await store.DeleteAsync(ToolId.From("missing")));
    }

    [Fact]
    public async Task Tool_SurvivesStoreReopen_OverSameDbFile()
    {
        // Simulates a gateway restart: create with one store instance, then reopen a fresh
        // store over the same database file and confirm the tool is still there.
        var first = NewStore();
        await first.CreateAsync(CreateTool("persisted", "Persisted Tool", order: 5));
        SqliteConnection.ClearAllPools();

        var second = NewStore();
        var loaded = await second.GetAsync(ToolId.From("persisted"));

        loaded.ShouldNotBeNull();
        loaded!.Name.ShouldBe("Persisted Tool");
        loaded.Order.ShouldBe(5);

        var listed = await second.ListAsync();
        listed.ShouldContain(t => t.Id.Value == "persisted");
    }

    public async ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        await Task.Yield();
        if (!Directory.Exists(_tempDir))
            return;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
                break;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(50);
            }
        }
    }
}
