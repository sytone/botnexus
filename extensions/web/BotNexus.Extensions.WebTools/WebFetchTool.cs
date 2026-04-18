using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Extensions.WebTools;

/// <summary>
/// Fetches URLs and returns content as readable text or raw HTML.
/// Supports pagination via start_index and max_length.
/// </summary>
public sealed class WebFetchTool : IAgentTool, IDisposable
{
    private readonly WebFetchConfig _config;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public WebFetchTool(WebFetchConfig config, HttpClient? httpClient = null)
    {
        _config = config;

        if (httpClient is not null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds)
            };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", _config.UserAgent);
            _ownsHttpClient = true;
        }
    }

    /// <inheritdoc />
    public string Name => "web_fetch";

    /// <inheritdoc />
    public string Label => "Web Fetch";

    /// <inheritdoc />
    public Tool Definition => new(
        Name,
        "Fetch a URL and return content as readable text or raw HTML. Supports pagination.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "url": {
                  "type": "string",
                  "description": "URL to fetch."
                },
                "max_length": {
                  "type": "integer",
                  "description": "Maximum characters to return. Default: 5000, max: 20000."
                },
                "raw": {
                  "type": "boolean",
                  "description": "If true, return raw HTML; if false, convert to readable text. Default: false."
                },
                "start_index": {
                  "type": "integer",
                  "description": "Character offset for pagination. Default: 0."
                }
              },
              "required": ["url"]
            }
            """).RootElement.Clone());

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var url = ReadString(arguments, "url");
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("url is required.");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("url must be a valid HTTP or HTTPS URL.");
        }

        var maxLength = ReadOptionalInt(arguments, "max_length") ?? 5000;
        if (maxLength < 1 || maxLength > _config.MaxLengthChars)
            throw new ArgumentOutOfRangeException(
                nameof(arguments),
                $"max_length must be between 1 and {_config.MaxLengthChars}.");

        var raw = ReadOptionalBool(arguments, "raw") ?? false;
        var startIndex = ReadOptionalInt(arguments, "start_index") ?? 0;
        if (startIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(arguments), "start_index must be >= 0.");

        IReadOnlyDictionary<string, object?> prepared = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["url"] = url,
            ["max_length"] = maxLength,
            ["raw"] = raw,
            ["start_index"] = startIndex,
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
        var url = (string)arguments["url"]!;
        var maxLength = (int)arguments["max_length"]!;
        var raw = (bool)arguments["raw"]!;
        var startIndex = (int)arguments["start_index"]!;

        try
        {
            var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return TextResult(
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase} when fetching {url}");
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var content = raw ? html : HtmlToText.Convert(html);

            // Apply pagination
            if (startIndex >= content.Length)
            {
                return TextResult("[No content at this offset]");
            }

            var remaining = content.Length - startIndex;
            var outputLength = Math.Min(maxLength, remaining);
            var output = content.Substring(startIndex, outputLength);

            if (remaining > maxLength)
            {
                var nextIndex = startIndex + maxLength;
                output += $"\n\n[Content truncated. Use start_index={nextIndex} to continue reading.]";
            }

            return TextResult(output);
        }
        catch (HttpRequestException ex)
        {
            return TextResult($"HTTP error: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            return TextResult($"Request timed out after {_config.TimeoutSeconds}s.");
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return TextResult("Request cancelled.");
        }
        catch (Exception ex)
        {
            return TextResult($"Error fetching URL: {ex.Message}");
        }
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

    private static int? ReadOptionalInt(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return null;
        return value switch
        {
            int i => i,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            JsonElement { ValueKind: JsonValueKind.Number } el when el.TryGetInt32(out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.Number } element => (int)element.GetDouble(),
            JsonElement { ValueKind: JsonValueKind.String } el when int.TryParse(el.GetString(), out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.String } element when double.TryParse(element.GetString(), out var d) => (int)d,
            double d => (int)d,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => throw new ArgumentException($"Argument '{key}' must be an integer.")
        };
    }

    private static bool? ReadOptionalBool(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return null;
        return value switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => throw new ArgumentException($"Argument '{key}' must be a boolean.")
        };
    }

    private static AgentToolResult TextResult(string text)
        => new([new AgentToolContent(AgentToolContentType.Text, text)]);

    #endregion

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
