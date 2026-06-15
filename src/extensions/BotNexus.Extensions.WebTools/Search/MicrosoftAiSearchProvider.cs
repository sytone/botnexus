using System.Text;
using System.Text.Json;

namespace BotNexus.Extensions.WebTools.Search;

/// <summary>
/// Microsoft Web IQ search provider (api.microsoft.ai/v3/search/web).
/// Web IQ is Microsoft's AI-grounding search service (the successor to "Grounding with Bing",
/// built on Bing infrastructure and re-architected for LLMs).
/// </summary>
/// <remarks>
/// <para>
/// API reference: https://webiq.microsoft.ai/documentation/api-reference/web
/// A captured copy of the contract lives in the Farnsworth workspace at
/// <c>playbook/webiq-api-reference.md</c> for traceability.
/// </para>
/// <para>
/// Request: <c>POST</c> with headers <c>x-apikey</c> (or <c>Authorization</c>) and
/// <c>content-type: application/json</c>. Body fields: <c>query</c>, <c>maxResults</c> (max 50),
/// <c>language</c>, <c>region</c>, <c>contentFormat</c> ("passage" | "text" | "html" | "markdown"),
/// and <c>maxLength</c> (max 500000).
/// </para>
/// <para>
/// Response shape: <c>{ "webResults": [ { "title", "url", "content", "crawledAt",
/// "lastUpdatedAt", "language", "isAdult" } ], "traceId": "..." }</c>.
/// IMPORTANT: the web vertical does NOT return a <c>snippet</c> field — the grounding text is in
/// <c>content</c> (a full semantic document in the requested <c>contentFormat</c>). We map
/// <c>content</c> to the result snippet and truncate it per-result so a multi-result search does
/// not flood the model's context window with full page extracts.
/// </para>
/// </remarks>
internal sealed class MicrosoftAiSearchProvider : ISearchProvider
{
    private const string ApiEndpoint = "https://api.microsoft.ai/v3/search/web";

    /// <summary>
    /// Markdown is the cleanest <c>contentFormat</c> for LLM consumption (vs. raw HTML).
    /// </summary>
    private const string ContentFormat = "markdown";

    /// <summary>
    /// Per-result content cap requested from the API (characters). Web IQ defaults to 10000 and
    /// allows up to 500000, but a full-page extract per result is far more than a search snippet
    /// needs and would balloon token usage across <c>maxResults</c> results. We request a modest
    /// amount and then trim further client-side (see <see cref="MaxSnippetChars" />).
    /// </summary>
    private const int RequestMaxLength = 2000;

    /// <summary>
    /// Hard client-side cap on the snippet length per result (characters). Acts as a backstop in
    /// case the API returns more than <see cref="RequestMaxLength" />.
    /// </summary>
    private const int MaxSnippetChars = 1000;

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
            contentFormat = ContentFormat,
            maxLength = RequestMaxLength
        };

        var json = JsonSerializer.Serialize(payload);

        using var request = new HttpRequestMessage(HttpMethod.Post, ApiEndpoint);
        request.Headers.Add("x-apikey", _apiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        var results = new List<SearchResult>();

        // Primary (current) Web IQ shape: { "webResults": [ { title, url, content, ... } ] }.
        if (TryGetArray(root, "webResults", out var webResults))
        {
            foreach (var result in webResults.EnumerateArray())
            {
                AddResult(results, result, titleProperty: "title", snippetProperty: "content");
            }
        }
        // Fallback shape: { "results": [ { title, url, content|snippet } ] }.
        else if (TryGetArray(root, "results", out var resultsArray))
        {
            foreach (var result in resultsArray.EnumerateArray())
            {
                AddResult(results, result, titleProperty: "title", snippetProperty: "content", fallbackSnippetProperty: "snippet");
            }
        }
        // Legacy Bing-style shape: { "webPages": { "value": [ { name, url, snippet } ] } }.
        else if (root.TryGetProperty("webPages", out var webPages) &&
                 TryGetArray(webPages, "value", out var values))
        {
            foreach (var result in values.EnumerateArray())
            {
                AddResult(results, result, titleProperty: "name", snippetProperty: "snippet");
            }
        }

        return results;
    }

    private static bool TryGetArray(JsonElement element, string propertyName, out JsonElement array)
    {
        if (element.TryGetProperty(propertyName, out array) && array.ValueKind == JsonValueKind.Array)
            return true;

        array = default;
        return false;
    }

    private static void AddResult(
        List<SearchResult> results,
        JsonElement result,
        string titleProperty,
        string snippetProperty,
        string? fallbackSnippetProperty = null)
    {
        var title = ReadString(result, titleProperty);
        var url = ReadString(result, "url");

        var snippet = ReadString(result, snippetProperty);
        if (string.IsNullOrWhiteSpace(snippet) && fallbackSnippetProperty is not null)
            snippet = ReadString(result, fallbackSnippetProperty);

        if (!string.IsNullOrWhiteSpace(url))
            results.Add(new SearchResult(title, url, Truncate(snippet)));
    }

    private static string ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string Truncate(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= MaxSnippetChars)
            return value;

        return string.Concat(value.AsSpan(0, MaxSnippetChars).TrimEnd(), "…");
    }
}
