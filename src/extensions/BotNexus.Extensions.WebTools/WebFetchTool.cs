using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Utilities;
using BotNexus.Domain.Text;
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
    /// Optional secret redactor (#3360). Consumed at exactly one place -- <see cref="ErrorResult"/> --
    /// so a future <c>catch</c> branch cannot reach an unredacted message by forgetting to call it,
    /// mirroring the single-choke-point discipline
    /// <see cref="ProviderHttpErrorHelper.ThrowForFailedResponse"/> established for the provider
    /// path in #2881.
    /// </summary>
    private readonly ISecretRedactor? _secretRedactor;

    /// <summary>
    /// Maximum number of redirects the tool will follow before giving up. Each hop is
    /// re-validated against the SSRF policy, so a bounded count also caps redirect loops.
    /// </summary>
    private const int MaxRedirects = 5;

    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Creates an outbound fetch tool. Without an injected client, owns a destination-validated
    /// transport. A supplied client remains caller-owned and must enforce equivalent DNS and
    /// connection policy; the production contributor uses the owned path.
    /// </summary>
    public WebFetchTool(
        WebFetchConfig config,
        HttpClient? httpClient = null,
        ISecretRedactor? secretRedactor = null)
    {
        _config = config;
        _secretRedactor = secretRedactor;

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
            var handler = new PublicNetworkHttpTransport(_config);
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds)
            };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", _config.UserAgent);
            _ownsHttpClient = true;
        }
    }

    // Tests supply DNS/socket seams, not a replacement HTTP handler. The tool still owns the
    // same production pipeline used by default construction and WebToolsContributor.
    internal WebFetchTool(
        WebFetchConfig config,
        PublicNetworkHttpTransport transport,
        ISecretRedactor? secretRedactor = null)
        : this(config, new HttpClient(transport) { Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds) }, secretRedactor)
    {
        _httpClient.DefaultRequestHeaders.Add("User-Agent", config.UserAgent);
        _ownsHttpClient = true;
    }

    /// <inheritdoc />
    public string Name => "web_fetch";

    /// <inheritdoc />
    public string Label => "Web Fetch";

    /// <summary>Content source classification for turn-taint accumulation (#2519). Every byte returned is the remote page body, authored by whoever controls the URL.</summary>
    public string ContentSource => ToolContentSource.Network;

    /// <inheritdoc />
    public Tool Definition => new(
        Name,
        ToolDescription,
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
    /// Throws <see cref="ArgumentException"/> when <paramref name="uri"/> text identifies a
    /// private, loopback, link-local, IMDS, or otherwise blocked literal address/hostname.
    /// Delegates lexical policy to <see cref="SsrfValidator"/>; the owned transport separately
    /// validates resolved destinations at connection establishment.
    /// </summary>
    internal static void AssertNotPrivateOrImds(Uri uri)
    {
        AssertSafeWithGuidance(uri, null);
    }

    /// <summary>
    /// Appended to loopback rejections only. Issue #2418: a terminal policy error with no
    /// alternative caused one agent to retry the identical blocked gateway URL 116 times in a
    /// week. The block itself is correct and unchanged - this only tells the caller where to go
    /// instead, so the rejection is self-correcting rather than a retry trap.
    /// </summary>
    internal const string LoopbackGuidance =
        " web_fetch is a generic OUTBOUND fetch tool and cannot be used to inspect this gateway "
        + "or other services on this machine, so retrying this URL will always fail. To inspect "
        + "the local gateway (for example /health or /api/logs/recent), " + LocalEndpointRemedy;

    /// <summary>
    /// The single canonical wording of the supported path to local endpoints. Issue #2691 AC1:
    /// the restriction must be knowable from the tool description, not only at call time, so this
    /// sentence is shared by <see cref="LoopbackGuidance"/> and <see cref="ToolDescription"/>.
    /// Keeping one constant is deliberate - two hand-written copies of the same advice drift.
    /// </summary>
    internal const string LocalEndpointRemedy =
        "use a sanctioned local mechanism instead: issue the request from the shell/exec tool "
        + "against the local API.";

    /// <summary>
    /// Tool description surfaced in the schema. Issue #2691: 99.2% of this tool's failures were
    /// the loopback SSRF refusal discovered at call time, because nothing in the schema said the
    /// restriction existed. The restriction and its remedy are stated up front so the cost is
    /// paid at decision time rather than one burned turn at a time. Behaviour is unchanged.
    /// </summary>
    internal const string ToolDescription =
        "Fetch a URL and return content as readable text or raw HTML. Supports pagination. "
        + "Outbound public hosts only: loopback/localhost targets (localhost, 127.0.0.0/8, ::1) "
        + "and private-range, link-local, and cloud-metadata addresses are refused by the SSRF "
        + "guard, so this gateway and other services on this machine cannot be reached here - "
        + LocalEndpointRemedy;

    /// <summary>
    /// Runs the shared SSRF policy and, when the rejected host is loopback, appends actionable
    /// guidance to the message. Security behaviour is identical to calling
    /// <see cref="SsrfValidator.AssertSafe"/> directly -- every URL blocked before is still
    /// blocked, and non-loopback rejections keep their message byte-for-byte. The guidance lives
    /// here rather than in <see cref="SsrfValidator"/> because only this tool knows it is an
    /// outbound fetch tool; the same validator also guards webhooks where the advice is wrong.
    /// </summary>
    private static void AssertSafeWithGuidance(Uri uri, IReadOnlyList<string>? additionalBlockedHosts)
    {
        var result = SsrfValidator.Validate(uri, additionalBlockedHosts);
        if (result.IsSafe)
            return;

        throw new ArgumentException(
            IsLoopbackHost(uri.Host) ? result.Reason + LoopbackGuidance : result.Reason);
    }

    /// <summary>
    /// True when <paramref name="host"/> is a loopback target: the literal name <c>localhost</c>,
    /// any address in 127.0.0.0/8, or the IPv6 loopback <c>::1</c> (with or without brackets).
    /// </summary>
    private static bool IsLoopbackHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        var hostToParse = host.StartsWith('[') && host.EndsWith(']')
            ? host[1..^1]
            : host;

        return System.Net.IPAddress.TryParse(hostToParse, out var ip)
            && System.Net.IPAddress.IsLoopback(ip);
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
            AssertSafeWithGuidance(uri, _config.AdditionalBlockedHosts);
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
                // NOT a catch branch, and the reason it must still be redacted: ReasonPhrase is
                // written by the SERVER and `url` is caller-supplied, so this is the one error path
                // an attacker can drive on demand. A fix that only wrapped the catch blocks would
                // leave the most reachable leak in place.
                return ErrorResult(
                    $"{errorJson}\n\nHTTP {statusCode} {response.ReasonPhrase} when fetching {url}");
            }

            var html = await BoundedHttpContent.ReadStringWithLimitAsync(
                response.Content,
                _config.MaxResponseBytes,
                cancellationToken).ConfigureAwait(false);

            // THE untrusted-content boundary for this tool (#2813). Everything below this line is
            // fully attacker-controlled: whoever owns the URL owns the bytes. The size cap above
            // bounds only how MUCH hostile content arrives; it says nothing about what that content
            // can DO once it is spliced into the turn - a page can embed <|im_start|> or a <system>
            // block and, because tool output lands in the transcript that MemoryIndexer persists,
            // reach durable memory through a second door.
            //
            // Applied EXACTLY ONCE, here, and specifically BEFORE HtmlToText.Convert. Order is
            // load-bearing, not incidental:
            //   * HtmlToText strips tag delimiters, so sanitizing afterwards would see a <system>
            //     block already reduced to its bare inner text - the delimiters that identify it as
            //     an injection block are gone, and the injected INSTRUCTIONS survive intact. The
            //     block-form patterns can only remove inner content while the block is still whole.
            //   * HtmlToText also HTML-decodes entities, which would turn an inert &lt;|im_start|&gt;
            //     into a live marker AFTER any later pass. Sanitizing first means
            //     EscapedMarkupNormalizer sees the escaped spelling and deletes it at source (#2808).
            //   * raw mode bypasses HtmlToText entirely, so this position is also the only one that
            //     covers the rawest path - the one returning verbatim attacker HTML.
            var sanitizedBody = UntrustedContentSanitizer.Sanitize(html);
            var content = raw ? sanitizedBody : HtmlToText.Convert(sanitizedBody);

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
        catch (ResponseContentTooLargeException ex)
        {
            return ErrorResult(
                $"Response body exceeded the {ex.MaxBytes}-byte limit and was discarded to protect the gateway from excessive memory use.");
        }
        catch (HttpRequestException ex)
        {
            return ErrorResult($"HTTP error: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            return ErrorResult($"Request timed out after {_config.TimeoutSeconds}s.");
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ErrorResult("Request cancelled.");
        }
        catch (RedirectBlockedException ex)
        {
            return ErrorResult(ex.Message);
        }
        catch (Exception ex)
        {
            return ErrorResult($"Error fetching URL: {ex.Message}");
        }
    }

    /// <summary>
    /// THE model-visible error boundary for this tool (#3360). Every failure path -- the
    /// non-success-status branch and all six <c>catch</c> branches -- returns through here, so
    /// redaction is one decision rather than one-per-branch.
    ///
    /// <para>
    /// <b>Why error text needs this when success bodies already pass
    /// <c>UntrustedContentSanitizer</c> (#2813).</b> That sanitizer is a prompt-injection markup
    /// filter, not a secret redactor, and it never ran on the error paths. An intermediary proxy
    /// can embed a credential in an exception message or a reflected header in a
    /// <c>ReasonPhrase</c>; the result is persisted to the transcript, which the memory indexer
    /// reads, so a leak here survives session deletion.
    /// </para>
    ///
    /// <para>
    /// <b>A null redactor is a deliberate pass-through, not a blanket drop</b> -- the same contract
    /// as <c>ProviderHttpErrorHelper.Redact</c>, so a host that has not wired the redactor keeps
    /// its diagnostics rather than silently losing them.
    /// </para>
    /// </summary>
    private AgentToolResult ErrorResult(string message)
        => TextResult(_secretRedactor is null || string.IsNullOrEmpty(message)
            ? message
            : _secretRedactor.Redact(message));

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
