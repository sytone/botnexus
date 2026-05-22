using System.Text.Json.Serialization;

namespace BotNexus.Extensions.WebTools;

/// <summary>
/// Configuration for the Web Tools extension.
/// </summary>
public sealed class WebToolsConfig
{
    /// <summary>Search provider configuration.</summary>
    [JsonPropertyName("search")]
    public WebSearchConfig? Search { get; set; }

    /// <summary>Fetch tool configuration.</summary>
    [JsonPropertyName("fetch")]
    public WebFetchConfig? Fetch { get; set; }
}

/// <summary>
/// Configuration for the web search provider.
/// </summary>
public sealed class WebSearchConfig
{
    /// <summary>Search provider: "brave", "tavily", "bing", or "copilot". Default: "brave".</summary>
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "brave";

    /// <summary>API key for the search provider. Supports ${env:VAR} syntax.</summary>
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }

    /// <summary>Maximum number of results to return. Default: 5.</summary>
    [JsonPropertyName("maxResults")]
    public int MaxResults { get; set; } = 5;
}

/// <summary>
/// Configuration for the web fetch tool.
/// </summary>
public sealed class WebFetchConfig
{
    /// <summary>Maximum content length in characters. Default: 20000.</summary>
    [JsonPropertyName("maxLengthChars")]
    public int MaxLengthChars { get; set; } = 20_000;

    /// <summary>HTTP timeout in seconds. Default: 30.</summary>
    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>User-Agent header for HTTP requests.</summary>
    [JsonPropertyName("userAgent")]
    public string UserAgent { get; set; } = "BotNexus/1.0 (compatible; bot)";

    /// <summary>
    /// When <c>false</c> (default), requests to private/loopback/IMDS IP ranges and
    /// known cloud metadata hostnames are blocked to prevent SSRF attacks.
    /// Set to <c>true</c> only in self-hosted environments where agents must reach
    /// internal services intentionally.
    /// </summary>
    [JsonPropertyName("allowPrivateNetworks")]
    public bool AllowPrivateNetworks { get; set; } = false;

    /// <summary>
    /// Additional hostnames to block regardless of <see cref="AllowPrivateNetworks"/>.
    /// Exact case-insensitive match against the request host.
    /// </summary>
    [JsonPropertyName("additionalBlockedHosts")]
    public IReadOnlyList<string> AdditionalBlockedHosts { get; set; } = [];
}
