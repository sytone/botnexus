using System.Text;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Extensions.WebTools.Search;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Extensions.WebTools;

/// <summary>
/// Web search tool with multi-provider support (Brave, Tavily, Bing, Copilot MCP).
/// Returns formatted markdown results.
/// </summary>
public sealed class WebSearchTool : IAgentTool, IDisposable, IAsyncDisposable
{
    private readonly WebSearchConfig _config;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Func<CancellationToken, Task<string?>>? _copilotApiKeyResolver;
    private readonly string? _copilotApiEndpoint;
    private readonly object _providerGate = new();
    private ISearchProvider? _provider;
    private bool _disposed;

    public WebSearchTool(
        WebSearchConfig config,
        HttpClient? httpClient = null,
        Func<CancellationToken, Task<string?>>? copilotApiKeyResolver = null,
        string? copilotApiEndpoint = null)
    {
        _config = config;
        _copilotApiKeyResolver = copilotApiKeyResolver;
        _copilotApiEndpoint = copilotApiEndpoint;

        if (httpClient is not null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _ownsHttpClient = true;
        }
    }

    /// <inheritdoc />
    public string Name => "web_search";

    /// <inheritdoc />
    public string Label => "Web Search";

    /// <inheritdoc />
    public Tool Definition => new(
        Name,
        "Search the web using a configurable provider and return formatted results.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "query": {
                  "type": "string",
                  "description": "Search query."
                }
              },
              "required": ["query"]
            }
            """).RootElement.Clone());

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var query = ReadString(arguments, "query");
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("query is required.");

        IReadOnlyDictionary<string, object?> prepared = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["query"] = query,
        };

        return Task.FromResult(prepared);
    }

    /// <inheritdoc />
    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var query = (string)arguments["query"]!;
        var providerName = _config.Provider.ToLowerInvariant();
        var isCopilot = string.Equals(providerName, "copilot", StringComparison.Ordinal);
        var apiKey = string.Empty;

        if (!isCopilot)
        {
            apiKey = ResolveEnvValue(_config.ApiKey ?? string.Empty);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return TextResult(
                    "Web search requires an API key. Configure 'apiKey' in the 'botnexus-web' extension config.");
            }
        }

        // Create provider based on config
        var provider = CreateProvider(apiKey);
        if (provider is null)
        {
            return TextResult(
                $"Unknown search provider: '{_config.Provider}'. Supported: brave, tavily, bing, copilot.");
        }

        try
        {
            var results = await provider.SearchAsync(query, _config.MaxResults, cancellationToken)
                .ConfigureAwait(false);

            if (results.Count == 0)
            {
                return TextResult($"No results found for \"{query}\".");
            }

            // Format results as markdown
            var output = new StringBuilder();
            output.AppendLine($"## Search Results for \"{query}\"");
            output.AppendLine();

            for (var i = 0; i < results.Count; i++)
            {
                var result = results[i];
                output.AppendLine($"{i + 1}. **[{result.Title}]({result.Url})**");
                if (!string.IsNullOrWhiteSpace(result.Snippet))
                {
                    output.AppendLine($"   {result.Snippet}");
                }

                output.AppendLine();
            }

            return TextResult(output.ToString().TrimEnd());
        }
        catch (HttpRequestException ex)
        {
            return TextResult($"Search API error: {ex.Message}");
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return TextResult("Search cancelled.");
        }
        catch (Exception ex)
        {
            return TextResult($"Error performing search: {ex.Message}");
        }
    }

    private ISearchProvider? CreateProvider(string? apiKey)
    {
        if (_provider is not null)
            return _provider;

        lock (_providerGate)
        {
            _provider ??= _config.Provider.ToLowerInvariant() switch
            {
                "brave" when !string.IsNullOrWhiteSpace(apiKey) => new BraveSearchProvider(_httpClient, apiKey),
                "tavily" when !string.IsNullOrWhiteSpace(apiKey) => new TavilySearchProvider(_httpClient, apiKey),
                "bing" when !string.IsNullOrWhiteSpace(apiKey) => new BingSearchProvider(_httpClient, apiKey),
                "copilot" when _copilotApiKeyResolver is not null =>
                    new CopilotMcpSearchProvider(_copilotApiKeyResolver, _httpClient, _copilotApiEndpoint),
                _ => null
            };
        }

        return _provider;
    }

    /// <summary>
    /// Resolves environment variable substitution patterns like ${env:VAR_NAME}.
    /// </summary>
    private static string ResolveEnvValue(string value)
    {
        if (!value.StartsWith("${env:", StringComparison.Ordinal) || !value.EndsWith('}'))
            return value;

        var inner = value.AsSpan(6, value.Length - 7); // strip ${env: and }
        var defaultSep = inner.IndexOf(":-", StringComparison.Ordinal);

        if (defaultSep >= 0)
        {
            var varName = inner[..defaultSep].ToString();
            var defaultValue = inner[(defaultSep + 2)..].ToString();
            return Environment.GetEnvironmentVariable(varName) ?? defaultValue;
        }

        return Environment.GetEnvironmentVariable(inner.ToString()) ?? string.Empty;
    }

    #region Argument Helpers

    private static string? ReadString(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return null;
        return value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
            JsonElement el => el.ToString(),
            _ => value.ToString()
        };
    }

    private static AgentToolResult TextResult(string text)
        => new([new AgentToolContent(AgentToolContentType.Text, text)]);

    #endregion

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_provider is IAsyncDisposable asyncDisposableProvider)
        {
            asyncDisposableProvider.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        else if (_provider is IDisposable disposableProvider)
        {
            disposableProvider.Dispose();
        }

        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_provider is IAsyncDisposable asyncDisposableProvider)
        {
            await asyncDisposableProvider.DisposeAsync().ConfigureAwait(false);
        }
        else if (_provider is IDisposable disposableProvider)
        {
            disposableProvider.Dispose();
        }

        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
