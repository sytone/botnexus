using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Gateway.Abstractions.Security;

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

    /// <summary>
    /// Maximum number of redirects the tool will follow before giving up. Each hop is
    /// re-validated against the SSRF policy, so a bounded count also caps redirect loops.
    /// </summary>
    private const int MaxRedirects = 5;

    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

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
            // Disable automatic redirect following: the tool follows redirects itself so it
            // can re-validate every hop against the SSRF policy. Auto-redirect would let a
            // safe public URL bounce to a private/IMDS address with no further checks.
            var handler = new HttpClientHandler { AllowAutoRedirect = false };
            _httpClient = new HttpClient(handler)
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

        // SSRF guard: block private/loopback/IMDS addresses and additional blocked hosts
        ValidateUrlOrThrow(uri);

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

    /// <summary>
    /// Throws <see cref="ArgumentException"/> when <paramref name="uri"/> resolves to a
    /// private, loopback, link-local, IMDS, or otherwise reserved address.
    /// Delegates to <see cref="SsrfValidator"/> for the actual validation logic.
    /// </summary>
    internal static void AssertNotPrivateOrImds(Uri uri)
    {
        SsrfValidator.AssertSafe(uri);
    }

    /// <summary>
    /// Applies the configured SSRF policy to <paramref name="uri"/> and throws
    /// <see cref="ArgumentException"/> when the target is blocked. Used for both the
    /// initial URL and every redirect hop so a redirect cannot smuggle a request to an
    /// internal address. When <see cref="WebFetchConfig.AllowPrivateNetworks"/> is set,
    /// only the explicit <see cref="WebFetchConfig.AdditionalBlockedHosts"/> list is enforced.
    /// </summary>
    private void ValidateUrlOrThrow(Uri uri)
    {
        if (!_config.AllowPrivateNetworks)
        {
            SsrfValidator.AssertSafe(uri, _config.AdditionalBlockedHosts);
            return;
        }

        // Even when private networks are permitted, honour the explicit block list.
        foreach (var blocked in _config.AdditionalBlockedHosts)
        {
            if (uri.Host.Equals(blocked, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"URL host '{uri.Host}' is blocked by configuration (SSRF prevention).");
        }
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
            var response = await SendWithRedirectsAsync(url, cancellationToken).ConfigureAwait(false);
            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
            var statusCode = (int)response.StatusCode;
            var contentType = response.Content.Headers.ContentType?.ToString();

            if (!response.IsSuccessStatusCode)
            {
                var errorMetadata = new Dictionary<string, object?>
                {
                    ["url"] = finalUrl,
                    ["status"] = statusCode,
                    ["content_type"] = contentType
                };
                var errorJson = JsonSerializer.Serialize(errorMetadata, MetadataJsonOptions);
                return TextResult(
                    $"{errorJson}\n\nHTTP {statusCode} {response.ReasonPhrase} when fetching {url}");
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var content = raw ? html : HtmlToText.Convert(html);

            var totalLength = content.Length;
            var endIndex = Math.Min(startIndex + maxLength, totalLength);
            var hasMore = endIndex < totalLength;

            var metadata = new Dictionary<string, object?>
            {
                ["url"] = finalUrl,
                ["status"] = statusCode,
                ["content_type"] = contentType,
                ["total_length"] = totalLength,
                ["start_index"] = startIndex,
                ["end_index"] = endIndex,
                ["has_more"] = hasMore
            };
            var metadataJson = JsonSerializer.Serialize(metadata, MetadataJsonOptions);

            // Apply pagination
            if (startIndex >= content.Length)
            {
                return TextResult($"{metadataJson}\n\n[No content at this offset]");
            }

            var remaining = content.Length - startIndex;
            var outputLength = Math.Min(maxLength, remaining);
            var output = content.Substring(startIndex, outputLength);

            if (remaining > maxLength)
            {
                var nextIndex = startIndex + maxLength;
                output += $"\n\n[Content truncated. Use start_index={nextIndex} to continue reading.]";
            }

            return TextResult($"{metadataJson}\n\n{output}");
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
        catch (RedirectBlockedException ex)
        {
            return TextResult(ex.Message);
        }
        catch (Exception ex)
        {
            return TextResult($"Error fetching URL: {ex.Message}");
        }
    }

    /// <summary>
    /// Issues a GET for <paramref name="url"/> and follows redirects manually, re-validating
    /// every hop against the SSRF policy. Returns the first non-redirect response (or the final
    /// redirect response if the hop budget is exhausted on a non-redirect). Throws
    /// <see cref="RedirectBlockedException"/> when a redirect target is blocked or the hop limit
    /// is reached.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRedirectsAsync(string url, CancellationToken ct)
    {
        var currentUri = new Uri(url, UriKind.Absolute);

        for (int hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!IsRedirect(response.StatusCode))
                return response;

            // It is a redirect. Pull the target, validate, and continue.
            var location = response.Headers.Location;
            if (location is null)
            {
                // Malformed redirect with no Location -- treat as a terminal response so the
                // caller surfaces the status code rather than looping.
                return response;
            }

            // Resolve relative redirects against the current absolute URL.
            var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
            response.Dispose();

            if (hop >= MaxRedirects)
                throw new RedirectBlockedException(
                    $"Too many redirects (>{MaxRedirects}) when fetching {url}.");

            if (nextUri.Scheme != Uri.UriSchemeHttp && nextUri.Scheme != Uri.UriSchemeHttps)
                throw new RedirectBlockedException(
                    $"Redirect to non-HTTP(S) scheme '{nextUri.Scheme}' was blocked (SSRF prevention).");

            try
            {
                ValidateUrlOrThrow(nextUri);
            }
            catch (ArgumentException ex)
            {
                throw new RedirectBlockedException(
                    $"Redirect to '{nextUri}' was blocked: {ex.Message}");
            }

            currentUri = nextUri;
        }
    }

    private static bool IsRedirect(System.Net.HttpStatusCode status) => (int)status switch
    {
        301 or 302 or 303 or 307 or 308 => true,
        _ => false
    };

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

/// <summary>
/// Raised when a redirect target is blocked by the SSRF policy or the redirect hop limit is
/// exceeded. Caught inside <see cref="WebFetchTool.ExecuteAsync"/> and surfaced to the agent as a
/// clear, non-fatal tool result rather than a generic fetch error.
/// </summary>
internal sealed class RedirectBlockedException(string message) : Exception(message);
