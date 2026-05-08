using System.Reflection;
using BotNexus.Agent.Core.Types;
using BotNexus.Extensions.WebTools.Search;
using BotNexus.Extensions.WebTools.Tests.Helpers;

namespace BotNexus.Extensions.WebTools.Tests;

[Trait("Category", "Unit")]
public class WebSearchToolTests
{
    [Fact]
    public async Task ExecuteAsync_WithValidQuery_ReturnsFormattedSearchResults()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(System.Net.HttpStatusCode.OK, """
            {
              "web": {
                "results": [
                  { "title": "Result A", "url": "https://example.com/a", "description": "Snippet A" },
                  { "title": "Result B", "url": "https://example.com/b", "description": "Snippet B" }
                ]
              }
            }
            """);

        using var tool = new WebSearchTool(
            new WebSearchConfig { Provider = "brave", ApiKey = "token", MaxResults = 5 },
            new HttpClient(handler));
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["query"] = "botnexus" });

        var result = await tool.ExecuteAsync("call-1", args);

        result.Content[0].Value.ShouldContain("## Search Results for \"botnexus\"");
        result.Content[0].Value.ShouldContain("**[Result A](https://example.com/a)**");
        result.Content[0].Value.ShouldContain("Snippet B");
    }

    [Theory]
    [InlineData("brave")]
    [InlineData("tavily")]
    [InlineData("bing")]
    public async Task ExecuteAsync_WithSupportedProvider_CreatesExpectedProvider(string providerName)
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(System.Net.HttpStatusCode.OK, providerName switch
        {
            "brave" => """{"web":{"results":[{"title":"R","url":"https://example.com","description":"S"}]}}""",
            "tavily" => """{"results":[{"title":"R","url":"https://example.com","content":"S"}]}""",
            _ => """{"webPages":{"value":[{"name":"R","url":"https://example.com","snippet":"S"}]}}"""
        });

        using var tool = new WebSearchTool(
            new WebSearchConfig { Provider = providerName, ApiKey = "token", MaxResults = 3 },
            new HttpClient(handler));
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["query"] = "query" });

        _ = await tool.ExecuteAsync("call-1", args);
        var provider = GetProvider(tool);

        provider.ShouldNotBeNull();
        provider!.GetType().Name.ToLowerInvariant().ShouldContain(providerName);
    }

    [Fact]
    public async Task ExecuteAsync_WithCopilotProvider_SkipsApiKeyValidation()
    {
        using var tool = new WebSearchTool(
            new WebSearchConfig { Provider = "copilot", ApiKey = null, MaxResults = 3 },
            new HttpClient(new MockHttpMessageHandler()),
            _ => Task.FromResult<string?>("resolver-token"));

        SetProvider(tool, new StubSearchProvider([new SearchResult("Title", "https://example.com", "Snippet")]));
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["query"] = "copilot" });

        var result = await tool.ExecuteAsync("call-1", args);

        result.Content[0].Value.ShouldContain("Title");
        result.Content[0].Value.ShouldNotContain("requires an API key");
    }

    [Fact]
    public async Task ExecuteAsync_WithResults_ContainsTitleUrlAndSnippetFields()
    {
        using var tool = new WebSearchTool(
            new WebSearchConfig { Provider = "brave", ApiKey = "token", MaxResults = 2 },
            new HttpClient(new MockHttpMessageHandler()));
        SetProvider(tool, new StubSearchProvider([new SearchResult("Doc", "https://example.com/doc", "Doc snippet")]));
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["query"] = "docs" });

        var result = await tool.ExecuteAsync("call-1", args);

        result.Content.ShouldHaveSingleItem().Type.ShouldBe(AgentToolContentType.Text);
        result.Content[0].Value.ShouldContain("[Doc](https://example.com/doc)");
        result.Content[0].Value.ShouldContain("Doc snippet");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PrepareArgumentsAsync_WithNullOrEmptyQuery_Throws(string? query)
    {
        using var tool = new WebSearchTool(new WebSearchConfig { Provider = "brave", ApiKey = "token" });

        var act = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["query"] = query });

        (await act.ShouldThrowAsync<ArgumentException>()).Message.ShouldContain("query is required");
    }

    [Fact]
    public async Task ExecuteAsync_WithUnknownProvider_ReturnsError()
    {
        using var tool = new WebSearchTool(new WebSearchConfig { Provider = "unknown", ApiKey = "token" });
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["query"] = "anything" });

        var result = await tool.ExecuteAsync("call-1", args);

        result.Content[0].Value.ShouldContain("Unknown search provider");
    }

    [Fact]
    public async Task ExecuteAsync_WhenProviderThrowsHttpRequestException_ReturnsError()
    {
        using var tool = new WebSearchTool(new WebSearchConfig { Provider = "brave", ApiKey = "token" });
        SetProvider(tool, new ThrowingSearchProvider(new HttpRequestException("network boom")));
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["query"] = "failing" });

        var result = await tool.ExecuteAsync("call-1", args);

        result.Content[0].Value.ShouldContain("Search API error: network boom");
    }

    [Fact]
    public async Task ExecuteAsync_WhenProviderReturnsEmptyResults_ReturnsGracefulMessage()
    {
        using var tool = new WebSearchTool(new WebSearchConfig { Provider = "brave", ApiKey = "token" });
        SetProvider(tool, new StubSearchProvider([]));
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["query"] = "none" });

        var result = await tool.ExecuteAsync("call-1", args);

        result.Content[0].Value.ShouldContain("No results found");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ExecuteAsync_WithJsonInjectionQuery_EscapesQueryInApiRequest()
    {
        const string query = "\"}; malicious";
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(System.Net.HttpStatusCode.OK, """{"web":{"results":[]}}""");
        using var tool = new WebSearchTool(
            new WebSearchConfig { Provider = "brave", ApiKey = "token", MaxResults = 5 },
            new HttpClient(handler));
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["query"] = query });

        _ = await tool.ExecuteAsync("call-1", args);

        handler.Requests.ShouldHaveSingleItem();
        handler.Requests[0].RequestUri!.Query.ShouldContain(Uri.EscapeDataString(query));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ExecuteAsync_WithSqlLikeInjectionQuery_TreatsAsLiteralText()
    {
        const string query = "' OR 1=1 --";
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(System.Net.HttpStatusCode.OK, """{"web":{"results":[]}}""");
        using var tool = new WebSearchTool(
            new WebSearchConfig { Provider = "brave", ApiKey = "token", MaxResults = 5 },
            new HttpClient(handler));
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["query"] = query });

        _ = await tool.ExecuteAsync("call-1", args);

        handler.Requests[0].RequestUri!.Query.ShouldContain(Uri.EscapeDataString(query));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ExecuteAsync_WithExtremelyLongQuery_ReturnsErrorWithoutCrashing()
    {
        var longQuery = new string('q', 100_000);
        using var tool = new WebSearchTool(new WebSearchConfig { Provider = "brave", ApiKey = "token", MaxResults = 1 });
        SetProvider(tool, new ThrowingSearchProvider(new InvalidOperationException("payload too large")));
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["query"] = longQuery });

        var result = await tool.ExecuteAsync("call-1", args);

        result.Content[0].Value.ShouldContain("Error performing search");
    }

    [Fact]
    public async Task ExecuteAsync_WithConcurrentCalls_IsThreadSafe()
    {
        using var tool = new WebSearchTool(new WebSearchConfig { Provider = "brave", ApiKey = "token", MaxResults = 1 });
        var provider = new DelaySearchProvider(TimeSpan.FromMilliseconds(10));
        SetProvider(tool, provider);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["query"] = "parallel" });

        var tasks = Enumerable.Range(0, 20)
            .Select(i => tool.ExecuteAsync($"call-{i}", args))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.ShouldAllBe(r => r.Content[0].Value.Contains("parallel"));
        provider.CallCount.ShouldBe(20);
    }

    private static ISearchProvider? GetProvider(WebSearchTool tool)
    {
        var field = typeof(WebSearchTool).GetField("_provider", BindingFlags.Instance | BindingFlags.NonPublic);
        return (ISearchProvider?)field?.GetValue(tool);
    }

    private static void SetProvider(WebSearchTool tool, ISearchProvider provider)
    {
        typeof(WebSearchTool)
            .GetField("_provider", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(tool, provider);
    }

    private sealed class StubSearchProvider(IReadOnlyList<SearchResult> results) : ISearchProvider
    {
        public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
            => Task.FromResult(results);
    }

    private sealed class ThrowingSearchProvider(Exception exception) : ISearchProvider
    {
        public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
            => Task.FromException<IReadOnlyList<SearchResult>>(exception);
    }

    private sealed class DelaySearchProvider(TimeSpan delay) : ISearchProvider
    {
        private int _callCount;

        public int CallCount => _callCount;

        public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            await Task.Delay(delay, ct);
            return [new SearchResult("Title", "https://example.com", query)];
        }
    }
}
