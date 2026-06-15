using System.Net;
using System.Text.Json;
using BotNexus.Extensions.WebTools.Search;
using BotNexus.Extensions.WebTools.Tests.Helpers;

namespace BotNexus.Extensions.WebTools.Tests.Search;

/// <summary>
/// Tests for <see cref="MicrosoftAiSearchProvider" /> (Microsoft Web IQ, api.microsoft.ai).
/// The response fixtures mirror the real Web IQ contract documented at
/// https://webiq.microsoft.ai/documentation/api-reference/web — in particular the web vertical
/// returns results under a <c>webResults</c> array where the grounding text is in <c>content</c>
/// (there is no <c>snippet</c> field on the web vertical).
/// </summary>
[Trait("Category", "Unit")]
public class MicrosoftAiSearchProviderTests
{
    // Authoritative Web IQ web-search response shape: webResults[] with title/url/content.
    private const string WebIqResponse = """
        {
          "webResults": [
            {
              "title": "List of Countries | Britannica",
              "url": "https://www.britannica.com/topic/list-of-countries-1993160",
              "content": "# List of Countries\nThere are 195 countries in the world today.",
              "crawledAt": "2026-06-15T15:09:00Z",
              "lastUpdatedAt": "2026-06-15T15:09:00Z",
              "language": "en",
              "isAdult": false
            },
            {
              "title": "Countries of the World",
              "url": "https://example.com/countries",
              "content": "A complete reference of sovereign states.",
              "language": "en",
              "isAdult": false
            }
          ],
          "traceId": "abc123"
        }
        """;

    [Fact]
    public async Task SearchAsync_WithWebIqResponse_MapsWebResults()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, WebIqResponse);
        var provider = new MicrosoftAiSearchProvider(new HttpClient(handler), "api-key");

        var results = await provider.SearchAsync("list of countries", 10, CancellationToken.None);

        results.Count.ShouldBe(2);
        results[0].Title.ShouldBe("List of Countries | Britannica");
        results[0].Url.ShouldBe("https://www.britannica.com/topic/list-of-countries-1993160");
        // The grounding text comes from "content" (the web vertical has no "snippet" field).
        results[0].Snippet.ShouldContain("There are 195 countries");
        results[1].Title.ShouldBe("Countries of the World");
        results[1].Snippet.ShouldBe("A complete reference of sovereign states.");
    }

    [Fact]
    public async Task SearchAsync_SendsExpectedRequest()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, WebIqResponse);
        var provider = new MicrosoftAiSearchProvider(new HttpClient(handler), "my-secret-key");

        await provider.SearchAsync("latest AI trends", 7, CancellationToken.None);

        var captured = handler.Requests.ShouldHaveSingleItem();
        captured.Method.ShouldBe(HttpMethod.Post);
        captured.RequestUri!.ToString().ShouldBe("https://api.microsoft.ai/v3/search/web");
        captured.Headers.ShouldContainKey("x-apikey");
        captured.Headers["x-apikey"].ShouldContain("my-secret-key");

        using var body = JsonDocument.Parse(captured.Body!);
        var root = body.RootElement;
        root.GetProperty("query").GetString().ShouldBe("latest AI trends");
        root.GetProperty("maxResults").GetInt32().ShouldBe(7);
        // Markdown is the cleanest format for LLM consumption.
        root.GetProperty("contentFormat").GetString().ShouldBe("markdown");
        // maxLength is kept modest so multi-result searches don't balloon context.
        root.GetProperty("maxLength").GetInt32().ShouldBeLessThanOrEqualTo(2000);
    }

    [Fact]
    public async Task SearchAsync_TruncatesLongContent()
    {
        var longContent = new string('x', 5000);
        var response = $$"""
            { "webResults": [ { "title": "T", "url": "https://example.com", "content": "{{longContent}}" } ] }
            """;
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, response);
        var provider = new MicrosoftAiSearchProvider(new HttpClient(handler), "api-key");

        var results = await provider.SearchAsync("query", 1, CancellationToken.None);

        results.ShouldHaveSingleItem();
        // 1000-char cap + a single ellipsis character.
        results[0].Snippet.Length.ShouldBeLessThanOrEqualTo(1001);
        results[0].Snippet.ShouldEndWith("…");
    }

    [Fact]
    public async Task SearchAsync_SkipsResultsWithoutUrl()
    {
        var response = """
            {
              "webResults": [
                { "title": "No URL", "content": "orphan" },
                { "title": "Has URL", "url": "https://example.com/x", "content": "kept" }
              ]
            }
            """;
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, response);
        var provider = new MicrosoftAiSearchProvider(new HttpClient(handler), "api-key");

        var results = await provider.SearchAsync("query", 5, CancellationToken.None);

        results.ShouldHaveSingleItem();
        results[0].Title.ShouldBe("Has URL");
    }

    [Fact]
    public async Task SearchAsync_WithEmptyWebResults_ReturnsEmpty()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, """{"webResults":[],"traceId":"t"}""");
        var provider = new MicrosoftAiSearchProvider(new HttpClient(handler), "api-key");

        var results = await provider.SearchAsync("query", 5, CancellationToken.None);

        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchAsync_WithLegacyResultsShape_StillMaps()
    {
        // Backwards-compatible fallback: a "results" array with content.
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, """
            { "results": [ { "title": "A", "url": "https://example.com/a", "content": "Alpha" } ] }
            """);
        var provider = new MicrosoftAiSearchProvider(new HttpClient(handler), "api-key");

        var results = await provider.SearchAsync("query", 5, CancellationToken.None);

        results.ShouldHaveSingleItem();
        results[0].Title.ShouldBe("A");
        results[0].Snippet.ShouldBe("Alpha");
    }

    [Fact]
    public async Task SearchAsync_WithLegacyWebPagesShape_StillMaps()
    {
        // Backwards-compatible fallback: the legacy Bing-style "webPages.value" array.
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, """
            { "webPages": { "value": [ { "name": "B", "url": "https://example.com/b", "snippet": "Beta" } ] } }
            """);
        var provider = new MicrosoftAiSearchProvider(new HttpClient(handler), "api-key");

        var results = await provider.SearchAsync("query", 5, CancellationToken.None);

        results.ShouldHaveSingleItem();
        results[0].Title.ShouldBe("B");
        results[0].Snippet.ShouldBe("Beta");
    }

    [Fact]
    public async Task SearchAsync_WithHttp401_ThrowsAuthError()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.Unauthorized, """{"errorCode":"AuthInvalidApiKey"}""");
        var provider = new MicrosoftAiSearchProvider(new HttpClient(handler), "bad-key");

        var act = () => provider.SearchAsync("query", 5, CancellationToken.None);

        var ex = await act.ShouldThrowAsync<HttpRequestException>();
        ex.Message.ShouldContain("401");
    }

    [Fact]
    public async Task SearchAsync_WithMalformedJson_ThrowsJsonException()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, "{oops");
        var provider = new MicrosoftAiSearchProvider(new HttpClient(handler), "api-key");

        var act = () => provider.SearchAsync("query", 5, CancellationToken.None);

        await act.ShouldThrowAsync<JsonException>();
    }
}
