using System.Text.Json;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Extensions.WebTools.Search;

/// <summary>
/// Bing Web Search API provider.
/// Endpoint: GET https://api.bing.microsoft.com/v7.0/search
/// </summary>
internal sealed class BingSearchProvider : ISearchProvider
{
    private const string ApiEndpoint = "https://api.bing.microsoft.com/v7.0/search";
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public BingSearchProvider(HttpClient httpClient, string apiKey)
    {
        _httpClient = httpClient;
        _apiKey = apiKey;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        var requestUrl = $"{ApiEndpoint}?q={Uri.EscapeDataString(query)}&count={maxResults}";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        request.Headers.Add("Ocp-Apim-Subscription-Key", _apiKey);

        // ResponseHeadersRead so the body is NOT pre-buffered by HttpClient — the bounded reader
        // below must see the raw stream to abort an unbounded/hostile body before it is materialized.
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // The body is an untrusted external search upstream — cap the read so a hostile or
        // malfunctioning endpoint cannot stream an unbounded body and OOM the gateway.
        var json = await BoundedHttpContent.ReadStringWithLimitAsync(response.Content, cancellationToken: ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var results = new List<SearchResult>();

        if (doc.RootElement.TryGetProperty("webPages", out var webPages) &&
            webPages.TryGetProperty("value", out var resultsArray))
        {
            foreach (var result in resultsArray.EnumerateArray())
            {
                var name = result.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var url = result.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var snippet = result.TryGetProperty("snippet", out var s) ? s.GetString() ?? "" : "";

                if (!string.IsNullOrWhiteSpace(url))
                    results.Add(new SearchResult(name, url, snippet));
            }
        }

        return results;
    }
}
