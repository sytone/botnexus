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

        // SSRF guard: block private/loopback/IMDS addresses and additional blocked hosts
        if (!_config.AllowPrivateNetworks)
            AssertNotPrivateOrImds(uri);

        // Always check additional blocked hosts (even when AllowPrivateNetworks = true)
        if (_config.AdditionalBlockedHosts.Count > 0)
            AssertNotAdditionalBlocked(uri);

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
    /// </summary>
    /// <remarks>
    /// Checked ranges (IPv4):
    /// <list type="bullet">
    ///   <item>Loopback: 127.0.0.0/8</item>
    ///   <item>Any: 0.0.0.0/8</item>
    ///   <item>Link-local / IMDS: 169.254.0.0/16</item>
    ///   <item>RFC-1918 class A: 10.0.0.0/8</item>
    ///   <item>RFC-1918 class B: 172.16.0.0/12</item>
    ///   <item>RFC-1918 class C: 192.168.0.0/16</item>
    ///   <item>Carrier-grade NAT: 100.64.0.0/10</item>
    /// </list>
    /// IPv6 loopback (::1) and hostnames <c>localhost</c> /
    /// <c>metadata.google.internal</c> are also blocked.
    /// </remarks>
    internal static void AssertNotPrivateOrImds(Uri uri)
    {
        var host = uri.Host;

        // Blocked hostnames (exact, case-insensitive)
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"URL host '{host}' is blocked for security reasons (SSRF prevention).");
        }

        // Try to parse as an IP address (handles both bare IPv4 and [IPv6] bracket notation)
        var hostToParse = host.StartsWith('[') && host.EndsWith(']')
            ? host[1..^1]   // strip IPv6 brackets
            : host;

        if (!System.Net.IPAddress.TryParse(hostToParse, out var ip))
            return; // hostname — DNS resolution not performed at prep time

        // IPv6 loopback ::1
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (ip.Equals(System.Net.IPAddress.IPv6Loopback))
                throw new ArgumentException(
                    $"URL host '{host}' is blocked for security reasons (SSRF prevention).");
            return; // other IPv6 addresses: allow (no private-range filtering for IPv6 beyond ::1)
        }

        // IPv4 range checks
        var bytes = ip.GetAddressBytes(); // big-endian: bytes[0] is most-significant
        var b0 = bytes[0];
        var b1 = bytes[1];

        bool isBlocked =
            b0 == 127 ||                                    // 127.0.0.0/8   loopback
            b0 == 0 ||                                      // 0.0.0.0/8     any
            b0 == 10 ||                                     // 10.0.0.0/8    RFC-1918
            (b0 == 169 && b1 == 254) ||                     // 169.254.0.0/16 link-local / IMDS
            (b0 == 172 && b1 >= 16 && b1 <= 31) ||         // 172.16.0.0/12 RFC-1918
            (b0 == 192 && b1 == 168) ||                     // 192.168.0.0/16 RFC-1918
            (b0 == 100 && (b1 & 0xC0) == 64);              // 100.64.0.0/10 CGN

        if (isBlocked)
            throw new ArgumentException(
                $"URL host '{host}' is blocked for security reasons (SSRF prevention).");
    }

    /// <summary>
    /// Throws <see cref="ArgumentException"/> when the URI host exactly matches one of the
    /// <see cref="WebFetchConfig.AdditionalBlockedHosts"/>.
    /// </summary>
    private void AssertNotAdditionalBlocked(Uri uri)
    {
        var host = uri.Host;
        foreach (var blocked in _config.AdditionalBlockedHosts)
        {
            if (host.Equals(blocked, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"URL host '{host}' is blocked by configuration (SSRF prevention).");
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
