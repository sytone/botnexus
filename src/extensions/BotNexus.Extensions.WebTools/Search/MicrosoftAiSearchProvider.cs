using System.Text;
using System.Text.Json;

namespace BotNexus.Extensions.WebTools.Search;

/// <summary>
/// Microsoft AI Search provider (api.microsoft.ai/v3/search/web).
/// Returns rich markdown-formatted results with configurable content length.
/// </summary>
internal sealed class MicrosoftAiSearchProvider : ISearchProvider
{
    private const string ApiEndpoint = "https://api.microsoft.ai/v3/search/web";
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public MicrosoftAiSearchProvider(HttpClient httpClient, string apiKey)
    {
        _httpClient = httpClient;
        _apiKey = apiKey;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        var payload = new
        {
            query,
            maxResults,
            language = "en",
            region = "US",
            contentFormat = "markdown",
            maxLength = 10000
        };

        var json = JsonSerializer.Serialize(payload);

        using var request = new HttpRequestMessage(HttpMethod.Post, ApiEndpoint);
        request.Headers.Add("x-apikey", _apiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(responseJson);

        var results = new List<SearchResult>();

        // The API may return results in a "results" array or "webPages" structure.
        // Try common response shapes.
        if (doc.RootElement.TryGetProperty("results", out var resultsArray) &&
            resultsArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var result in resultsArray.EnumerateArray())
            {
                var title = result.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var url = result.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var snippet = result.TryGetProperty("content", out var c) ? c.GetString() ?? ""
                    : result.TryGetProperty("snippet", out var s) ? s.GetString() ?? "" : "";

                if (!string.IsNullOrWhiteSpace(url))
                    results.Add(new SearchResult(title, url, snippet));
            }
        }
        else if (doc.RootElement.TryGetProperty("webPages", out var webPages) &&
                 webPages.TryGetProperty("value", out var values) &&
                 values.ValueKind == JsonValueKind.Array)
        {
            foreach (var result in values.EnumerateArray())
            {
                var title = result.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var url = result.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var snippet = result.TryGetProperty("snippet", out var s) ? s.GetString() ?? "" : "";

                if (!string.IsNullOrWhiteSpace(url))
                    results.Add(new SearchResult(title, url, snippet));
            }
        }

        return results;
    }
}
