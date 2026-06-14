using System.Text.Json;
using BotNexus.Agent.Core.Types;

namespace BotNexus.Extensions.Qmd.Tests;

public sealed class KnowledgeSearchToolTests
{
    private readonly InMemoryQmdBackend _backend = new();
    private readonly QmdConfig _config = new() { DefaultSearchMode = "hybrid", MaxResults = 10 };

    private KnowledgeSearchTool CreateTool() => new(_backend, _config);

    private static string GetText(AgentToolResult result) =>
        result.Content[0].Value;

    [Fact]
    public void Name_IsKnowledgeSearch()
    {
        var tool = CreateTool();
        Assert.Equal("knowledge_search", tool.Name);
    }

    [Fact]
    public void Definition_HasRequiredQueryParameter()
    {
        var tool = CreateTool();
        var schema = tool.Definition.Parameters;
        Assert.Equal(JsonValueKind.Array, schema.GetProperty("required").ValueKind);
        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("query", required);
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyQuery_ReturnsError()
    {
        var tool = CreateTool();
        var args = new Dictionary<string, object?> { ["query"] = "" };
        var result = await tool.ExecuteAsync("tc1", args);
        Assert.Contains("required", GetText(result));
        Assert.StartsWith("Error:", GetText(result));
    }

    [Fact]
    public async Task ExecuteAsync_WithNullQuery_ReturnsError()
    {
        var tool = CreateTool();
        var args = new Dictionary<string, object?> { ["query"] = null };
        var result = await tool.ExecuteAsync("tc1", args);
        Assert.StartsWith("Error:", GetText(result));
    }

    [Fact]
    public async Task ExecuteAsync_WithValidQuery_ReturnsJsonResults()
    {
        _backend.Documents.Add(
            new QmdDocument("doc1", "test-store", "/path/doc1.md", "Hello World", "hello this is content"));

        var tool = CreateTool();
        var args = new Dictionary<string, object?> { ["query"] = "hello", ["store"] = "test-store" };
        var result = await tool.ExecuteAsync("tc1", args);

        var text = GetText(result);
        Assert.DoesNotContain("Error:", text);
        var parsed = JsonSerializer.Deserialize<QmdSearchResult[]>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(parsed);
        Assert.Single(parsed);
        Assert.Equal("doc1", parsed[0].Id);
    }

    [Fact]
    public async Task ExecuteAsync_WithModeOverride_UsesSpecifiedMode()
    {
        var tool = CreateTool();
        var args = new Dictionary<string, object?>
        {
            ["query"] = "test",
            ["mode"] = "keyword"
        };
        var result = await tool.ExecuteAsync("tc1", args);
        Assert.DoesNotContain("Error:", GetText(result));
    }

    [Fact]
    public async Task ExecuteAsync_WithLimitOverride_ClampsToRange()
    {
        var tool = CreateTool();
        var args = new Dictionary<string, object?>
        {
            ["query"] = "test",
            ["limit"] = 100 // exceeds 50 max
        };
        var result = await tool.ExecuteAsync("tc1", args);
        Assert.DoesNotContain("Error:", GetText(result));
    }

    [Fact]
    public async Task ExecuteAsync_WhenBinaryNotFound_ReturnsErrorMessage()
    {
        var failingBackend = new FailingQmdBackend(new QmdBinaryNotFoundException("/bad/path"));
        var tool = new KnowledgeSearchTool(failingBackend, _config);
        var args = new Dictionary<string, object?> { ["query"] = "test" };
        var result = await tool.ExecuteAsync("tc1", args);
        Assert.Contains("not found", GetText(result));
    }

    [Fact]
    public async Task ExecuteAsync_WhenCliError_ReturnsErrorMessage()
    {
        var failingBackend = new FailingQmdBackend(new QmdCliException(1, "store not found", ["search"]));
        var tool = new KnowledgeSearchTool(failingBackend, _config);
        var args = new Dictionary<string, object?> { ["query"] = "test" };
        var result = await tool.ExecuteAsync("tc1", args);
        Assert.Contains("Search failed", GetText(result));
    }

    [Fact]
    public async Task ExecuteAsync_WhenTimeout_ReturnsErrorMessage()
    {
        var failingBackend = new FailingQmdBackend(new TimeoutException("timed out"));
        var tool = new KnowledgeSearchTool(failingBackend, _config);
        var args = new Dictionary<string, object?> { ["query"] = "test" };
        var result = await tool.ExecuteAsync("tc1", args);
        Assert.Contains("timed out", GetText(result));
    }

    /// <summary>Backend that always throws the configured exception on SearchAsync.</summary>
    private sealed class FailingQmdBackend(Exception ex) : IQmdBackend
    {
        public Task<QmdSearchResult[]> SearchAsync(string query, string? store, QmdSearchMode mode, int limit, CancellationToken ct) =>
            throw ex;
        public Task<QmdDocument?> GetDocumentAsync(string id, CancellationToken ct) => throw ex;
        public Task<QmdStoreInfo[]> GetStoresAsync(CancellationToken ct) => throw ex;
        public Task UpdateIndexAsync(string? store, CancellationToken ct) => throw ex;
        public Task EmbedAsync(string? store, CancellationToken ct) => throw ex;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ── limit parsing (#1363): out-of-range / non-integer JSON numbers must not throw ──

    /// <summary>Records the <c>limit</c> value handed to the backend so clamping can be asserted.</summary>
    private sealed class RecordingQmdBackend : IQmdBackend
    {
        public int? LastLimit { get; private set; }
        public Task<QmdSearchResult[]> SearchAsync(string query, string? store, QmdSearchMode mode, int limit, CancellationToken ct)
        {
            LastLimit = limit;
            return Task.FromResult(Array.Empty<QmdSearchResult>());
        }
        public Task<QmdDocument?> GetDocumentAsync(string id, CancellationToken ct) => Task.FromResult<QmdDocument?>(null);
        public Task<QmdStoreInfo[]> GetStoresAsync(CancellationToken ct) => Task.FromResult(Array.Empty<QmdStoreInfo>());
        public Task UpdateIndexAsync(string? store, CancellationToken ct) => Task.CompletedTask;
        public Task EmbedAsync(string? store, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static JsonElement JsonNumber(string raw) =>
        JsonDocument.Parse(raw).RootElement.Clone();

    [Fact]
    public async Task ExecuteAsync_WithOverlargeJsonLimit_DoesNotThrowAndClampsToMax()
    {
        var backend = new RecordingQmdBackend();
        var tool = new KnowledgeSearchTool(backend, _config);
        var args = new Dictionary<string, object?>
        {
            ["query"] = "test",
            ["limit"] = JsonNumber("9999999999") // overflows int -> would throw FormatException
        };

        var result = await tool.ExecuteAsync("tc1", args);

        Assert.DoesNotContain("Error:", GetText(result));
        Assert.Equal(50, backend.LastLimit); // saturated then clamped to the [1,50] max
    }

    [Fact]
    public async Task ExecuteAsync_WithFractionalJsonLimit_DoesNotThrowAndTruncates()
    {
        var backend = new RecordingQmdBackend();
        var tool = new KnowledgeSearchTool(backend, _config);
        var args = new Dictionary<string, object?>
        {
            ["query"] = "test",
            ["limit"] = JsonNumber("1.5") // non-integer -> would throw InvalidOperationException
        };

        var result = await tool.ExecuteAsync("tc1", args);

        Assert.DoesNotContain("Error:", GetText(result));
        Assert.Equal(1, backend.LastLimit); // truncated to 1, within [1,50]
    }

    [Fact]
    public async Task ExecuteAsync_WithValidJsonLimit_PassesThrough()
    {
        var backend = new RecordingQmdBackend();
        var tool = new KnowledgeSearchTool(backend, _config);
        var args = new Dictionary<string, object?>
        {
            ["query"] = "test",
            ["limit"] = JsonNumber("5")
        };

        var result = await tool.ExecuteAsync("tc1", args);

        Assert.DoesNotContain("Error:", GetText(result));
        Assert.Equal(5, backend.LastLimit);
    }

    [Fact]
    public async Task ExecuteAsync_WithNegativeJsonLimit_ClampsToOne()
    {
        var backend = new RecordingQmdBackend();
        var tool = new KnowledgeSearchTool(backend, _config);
        var args = new Dictionary<string, object?>
        {
            ["query"] = "test",
            ["limit"] = JsonNumber("-5")
        };

        var result = await tool.ExecuteAsync("tc1", args);

        Assert.DoesNotContain("Error:", GetText(result));
        Assert.Equal(1, backend.LastLimit);
    }

    [Fact]
    public async Task ExecuteAsync_WithGarbageLimit_FallsBackToConfigDefault()
    {
        var backend = new RecordingQmdBackend();
        var config = new QmdConfig { DefaultSearchMode = "hybrid", MaxResults = 7 };
        var tool = new KnowledgeSearchTool(backend, config);
        var args = new Dictionary<string, object?>
        {
            ["query"] = "test",
            ["limit"] = "not-a-number"
        };

        var result = await tool.ExecuteAsync("tc1", args);

        Assert.DoesNotContain("Error:", GetText(result));
        Assert.Equal(7, backend.LastLimit); // GetInt returns null -> config.MaxResults
    }
}
