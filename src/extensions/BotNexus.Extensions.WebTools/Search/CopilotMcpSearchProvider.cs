using System.Text.Json;
using System.Text.RegularExpressions;
using BotNexus.Extensions.Mcp;
using BotNexus.Extensions.Mcp.Protocol;
using BotNexus.Extensions.Mcp.Transport;

namespace BotNexus.Extensions.WebTools.Search;

internal sealed class CopilotMcpSearchProvider : ISearchProvider, IAsyncDisposable
{
    private const string DefaultEndpoint = "https://api.githubcopilot.com/mcp";
    private const string ServerId = "copilot";
    private static readonly Regex MarkdownLinkRegex = new(@"\[(?<title>[^\]]+)\]\((?<url>https?://[^)]+)\)", RegexOptions.Compiled);
    private static readonly Regex UrlRegex = new(@"https?://[^\s\)>]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex JsonCodeBlockRegex = new("```(?:json)?\\s*(?<json>[\\s\\S]*?)```", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly Func<CancellationToken, Task<string?>> _copilotApiKeyResolver;
    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly SemaphoreSlim _clientGate = new(1, 1);
    private McpClient? _client;
    private bool _disposed;

    public CopilotMcpSearchProvider(
        Func<CancellationToken, Task<string?>> copilotApiKeyResolver,
        HttpClient httpClient,
        string? endpoint = null)
    {
        _copilotApiKeyResolver = copilotApiKeyResolver;
        _httpClient = httpClient;
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await SearchWithRetryAsync(query, maxResults, isRetry: false, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<SearchResult>> SearchWithRetryAsync(string query, int maxResults, bool isRetry, CancellationToken ct)
    {
        var client = await GetOrCreateClientAsync(ct).ConfigureAwait(false);
        var arguments = JsonSerializer.SerializeToElement(new { query });

        McpToolCallResult callResult;
        try
        {
            callResult = await client.CallToolAsync("web_search", arguments, ct).ConfigureAwait(false);
        }
        catch (McpException ex) when (!isRetry)
        {
            // Token may have rotated — invalidate cached client and retry once
            await InvalidateClientAsync().ConfigureAwait(false);
            if (ex.Message.Contains("401", StringComparison.Ordinal) ||
                ex.Message.Contains("400", StringComparison.Ordinal) ||
                ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
                return await SearchWithRetryAsync(query, maxResults, isRetry: true, ct).ConfigureAwait(false);
            throw new InvalidOperationException($"Copilot MCP web_search failed: {ex.Message}", ex);
        }
        catch (McpException ex)
        {
            throw new InvalidOperationException($"Copilot MCP web_search failed: {ex.Message}", ex);
        }
        catch (HttpRequestException ex) when (!isRetry && (ex.StatusCode == System.Net.HttpStatusCode.BadRequest || ex.StatusCode == System.Net.HttpStatusCode.Unauthorized))
        {
            // Token rotation — invalidate and retry once
            await InvalidateClientAsync().ConfigureAwait(false);
            return await SearchWithRetryAsync(query, maxResults, isRetry: true, ct).ConfigureAwait(false);
        }

        if (callResult.IsError)
        {
            var errorText = string.Join(
                Environment.NewLine,
                callResult.Content
                    .Where(c => string.Equals(c.Type, "text", StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.Text)
                    .Where(static text => !string.IsNullOrWhiteSpace(text)));

            var detail = string.IsNullOrWhiteSpace(errorText) ? "no additional details." : errorText;
            throw new InvalidOperationException($"Copilot MCP web_search returned an error: {detail}");
        }

        return ParseResults(callResult.Content, maxResults);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }

        _clientGate.Dispose();
    }

    private async Task InvalidateClientAsync()
    {
        await _clientGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_client is not null)
            {
                await _client.DisposeAsync().ConfigureAwait(false);
                _client = null;
            }
        }
        finally
        {
            _clientGate.Release();
        }
    }

    private async Task<McpClient> GetOrCreateClientAsync(CancellationToken ct)
    {
        if (_client is not null)
            return _client;

        await _clientGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is not null)
                return _client;

            var apiKey = await _copilotApiKeyResolver(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Copilot token unavailable. Sign in again to refresh authentication.");

            var headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Authorization"] = $"Bearer {apiKey}",
                ["X-MCP-Toolsets"] = "web_search",
                ["X-MCP-Host"] = "github-coding-agent",
                ["X-Initiator"] = "agent"
            };

            var transport = new HttpSseMcpTransport(new Uri(_endpoint, UriKind.Absolute), headers, _httpClient);
            var client = new McpClient(transport, ServerId);

            try
            {
                await client.InitializeAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _client = client;
            return _client;
        }
        finally
        {
            _clientGate.Release();
        }
    }

    private static IReadOnlyList<SearchResult> ParseResults(IReadOnlyList<McpContent> content, int maxResults)
    {
        var allText = content
            .Where(c => string.Equals(c.Type, "text", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Text)
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .Select(static text => text!)
            .ToList();

        var results = new List<SearchResult>();
        foreach (var textBlock in allText)
        {
            if (TryParseJsonResults(textBlock, results, maxResults))
                break;

            if (TryParseMarkdownResults(textBlock, results, maxResults))
                break;
        }

        return results.Count > maxResults ? results[..maxResults] : results;
    }

    private static bool TryParseJsonResults(string text, List<SearchResult> results, int maxResults)
    {
        if (TryParseJsonFragment(text, results, maxResults))
            return results.Count > 0;

        foreach (Match match in JsonCodeBlockRegex.Matches(text))
        {
            if (TryParseJsonFragment(match.Groups["json"].Value, results, maxResults) && results.Count > 0)
                return true;
        }

        return false;
    }

    private static bool TryParseJsonFragment(string fragment, List<SearchResult> results, int maxResults)
    {
        try
        {
            using var doc = JsonDocument.Parse(fragment);
            ParseJsonElement(doc.RootElement, results, maxResults);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void ParseJsonElement(JsonElement element, List<SearchResult> results, int maxResults)
    {
        if (results.Count >= maxResults)
            return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    ParseJsonElement(item, results, maxResults);
                    if (results.Count >= maxResults)
                        return;
                }
                 break;
            case JsonValueKind.Object:
                if (TryAddFromObject(element, results))
                    return;

                if (element.TryGetProperty("results", out var resultsNode))
                    ParseJsonElement(resultsNode, results, maxResults);
                else if (element.TryGetProperty("items", out var itemsNode))
                    ParseJsonElement(itemsNode, results, maxResults);
                else if (element.TryGetProperty("value", out var valueNode))
                    ParseJsonElement(valueNode, results, maxResults);
                break;
        }
    }

    private static bool TryAddFromObject(JsonElement element, List<SearchResult> results)
    {
        var title = TryGetString(element, "title")
                    ?? TryGetString(element, "name")
                    ?? TryGetString(element, "headline")
                    ?? string.Empty;

        var url = TryGetString(element, "url")
                  ?? TryGetString(element, "link")
                  ?? TryGetString(element, "href")
                  ?? string.Empty;

        var snippet = TryGetString(element, "snippet")
                      ?? TryGetString(element, "description")
                      ?? TryGetString(element, "content")
                      ?? string.Empty;

        if (string.IsNullOrWhiteSpace(url))
            return false;

        results.Add(new SearchResult(title, url, snippet));
        return true;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool TryParseMarkdownResults(string text, List<SearchResult> results, int maxResults)
    {
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < lines.Length && results.Count < maxResults; i++)
        {
            var line = lines[i];
            var markdownMatch = MarkdownLinkRegex.Match(line);
            if (markdownMatch.Success)
            {
                var linkTitle = markdownMatch.Groups["title"].Value.Trim();
                var linkUrl = markdownMatch.Groups["url"].Value.Trim();
                var snippet = i + 1 < lines.Length ? lines[i + 1].Trim() : string.Empty;
                results.Add(new SearchResult(linkTitle, linkUrl, snippet));
                continue;
            }

            var urlMatch = UrlRegex.Match(line);
            if (!urlMatch.Success)
                continue;

            var fallbackUrl = urlMatch.Value.Trim();
            var titleCandidate = line[..urlMatch.Index].Trim(' ', '-', '*', '.', ':');
            var fallbackTitle = string.IsNullOrWhiteSpace(titleCandidate) ? fallbackUrl : titleCandidate;
            var snippetLine = i + 1 < lines.Length ? lines[i + 1].Trim() : string.Empty;
            results.Add(new SearchResult(fallbackTitle, fallbackUrl, snippetLine));
        }

        return results.Count > 0;
    }
}
