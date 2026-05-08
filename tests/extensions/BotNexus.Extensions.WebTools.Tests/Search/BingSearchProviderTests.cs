using System.Net;
using BotNexus.Extensions.WebTools.Search;
using BotNexus.Extensions.WebTools.Tests.Helpers;

namespace BotNexus.Extensions.WebTools.Tests.Search;

[Trait("Category", "Unit")]
public class BingSearchProviderTests
{
    [Fact]
    public async Task SearchAsync_WithValidResponse_MapsResults()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, """
            {
              "webPages": {
                "value": [
                  { "name": "A", "url": "https://example.com/a", "snippet": "Alpha" },
                  { "name": "B", "url": "https://example.com/b", "snippet": "Beta" }
                ]
              }
            }
            """);
        var provider = new BingSearchProvider(new HttpClient(handler), "api-key");

        var results = await provider.SearchAsync("query", 5, CancellationToken.None);

        results.Count().ShouldBe(2);
        results[0].Title.ShouldBe("A");
        results[0].Snippet.ShouldBe("Alpha");
    }

    [Fact]
    public async Task SearchAsync_WithHttp401_ThrowsAuthError()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.Unauthorized, """{"error":"bad key"}""");
        var provider = new BingSearchProvider(new HttpClient(handler), "bad-key");

        var act = () => provider.SearchAsync("query", 5, CancellationToken.None);

        var ex = await act.ShouldThrowAsync<HttpRequestException>();
        ex.Message.ShouldContain("401");
    }

    [Fact]
    public async Task SearchAsync_WithHttp429_ThrowsRateLimitError()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse((HttpStatusCode)429, """{"error":"rate limit"}""");
        var provider = new BingSearchProvider(new HttpClient(handler), "api-key");

        var act = () => provider.SearchAsync("query", 5, CancellationToken.None);

        var ex = await act.ShouldThrowAsync<HttpRequestException>();
        ex.Message.ShouldContain("429");
    }

    [Fact]
    public async Task SearchAsync_WithMalformedJson_ThrowsJsonException()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, "{oops");
        var provider = new BingSearchProvider(new HttpClient(handler), "api-key");

        var act = () => provider.SearchAsync("query", 5, CancellationToken.None);

        await act.ShouldThrowAsync<System.Text.Json.JsonException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task SearchAsync_WithMissingApiKey_StillSendsHeader(string? apiKey)
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, """{"webPages":{"value":[]}}""");
        var provider = new BingSearchProvider(new HttpClient(handler), apiKey!);

        var results = await provider.SearchAsync("query", 5, CancellationToken.None);
        results.ShouldBeEmpty();
        handler.Requests.ShouldHaveSingleItem();
    }
}
