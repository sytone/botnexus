using System.Text;
using System.Text.Json;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Extensions.WebTools.Search;

/// <summary>
/// Tavily Search API provider.
/// Endpoint: POST https://api.tavily.com/search
/// </summary>
internal sealed class TavilySearchProvider : ISearchProvider
{
    private const string ApiEndpoint = "https://api.tavily.com/search";
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public TavilySearchProvider(HttpClient httpClient, string apiKey)
    {
        _httpClient = httpClient;
        _apiKey = apiKey;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        var requestBody = new
        {
            api_key = _apiKey,
            query,
            max_results = maxResults,
            include_answer = false
        };

        var json = JsonSerializer.Serialize(requestBody);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Use SendAsync with ResponseHeadersRead (PostAsync pre-buffers the body) so the bounded
        // reader below sees the raw stream and can abort an unbounded/hostile body before it is
        // materialized.
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiEndpoint) { Content = content };
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // The body is an untrusted external search upstream — cap the read so a hostile or
        // malfunctioning endpoint cannot stream an unbounded body and OOM the gateway.
        var responseJson = await BoundedHttpContent.ReadStringWithLimitAsync(response.Content, cancellationToken: ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(responseJson);

        var results = new List<SearchResult>();

        if (doc.RootElement.TryGetProperty("results", out var resultsArray))
        {
            foreach (var result in resultsArray.EnumerateArray())
            {
                var title = result.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var url = result.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var snippet = result.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";

                if (!string.IsNullOrWhiteSpace(url))
                    results.Add(new SearchResult(title, url, snippet));
            }
        }

        return results;
    }
}
