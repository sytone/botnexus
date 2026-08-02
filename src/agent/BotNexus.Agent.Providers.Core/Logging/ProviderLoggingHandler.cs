using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.Core.Logging;

/// <summary>
/// <see cref="DelegatingHandler"/> that logs provider HTTP requests and responses at
/// <see cref="LogLevel.Debug"/> level for observability (issue #453).
/// <para>
/// Two layers of secret protection are always applied, regardless of configuration:
/// </para>
/// <list type="bullet">
/// <item>Auth headers (<c>x-api-key</c>, <c>Authorization</c>, <c>Proxy-Authorization</c>) are
/// replaced with <c>[REDACTED]</c> by name before their values are ever formatted.</item>
/// <item>Request/response bodies and the rendered header string are additionally passed through
/// an injected secret redactor delegate (the gateway's shared secret-shaped-value redactor) so
/// any API key or token that leaks into a body is scrubbed too.</item>
/// <item>Request <b>URLs</b> are redacted by query-parameter name before the URI is ever formatted
/// into a log message: the values of <c>sig</c>, <c>key</c>, <c>api_key</c>, <c>apikey</c>,
/// <c>access_token</c>, <c>token</c>, <c>password</c>, <c>secret</c> and any parameter whose name
/// begins with <c>x-</c> are replaced with <c>[REDACTED]</c>, while scheme, host and path are
/// preserved verbatim so the log stays diagnostically useful. Remaining parameter values are also
/// passed through the injected secret redactor.</item>
/// </list>
/// <para>
/// For streaming responses (<c>text/event-stream</c>) the body is <b>never</b> buffered — doing so
/// would break the live stream the provider reads. Only status, headers, and elapsed time are
/// logged for streamed responses. For non-streamed responses the body is buffered
/// non-destructively (via <see cref="HttpContent.LoadIntoBufferAsync()"/>) so the caller can still
/// read it, and a best-effort <c>usage</c> token-count summary is extracted when present.
/// </para>
/// </summary>
public sealed class ProviderLoggingHandler(
    ILogger<ProviderLoggingHandler> logger,
    Func<string, string>? secretRedactor = null) : DelegatingHandler
{
    /// <summary>Header names that are always redacted by name in request logs.</summary>
    private static readonly HashSet<string> RedactedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "x-api-key",
        "Authorization",
        "Proxy-Authorization",
    };

    /// <summary>
    /// Applies the injected secret redactor to arbitrary text (bodies, rendered headers). When no
    /// redactor was supplied the text is returned unchanged — header-name redaction still protects
    /// the obvious credential headers, but wiring a redactor is strongly recommended.
    /// </summary>
    private string Scrub(string text) => secretRedactor is null ? text : secretRedactor(text);

    /// <summary>
    /// Query-parameter names whose values are always replaced with <c>[REDACTED]</c> in logged URLs.
    /// Matching is on the parameter <i>name</i>, never on the value: a base64 SAS signature has no
    /// distinguishing shape, so value-sniffing would both miss real secrets and mangle innocent
    /// parameters. Any name beginning with <c>x-</c> is additionally treated as sensitive.
    /// </summary>
    private static readonly HashSet<string> SensitiveQueryParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "sig",
        "key",
        "api_key",
        "apikey",
        "access_token",
        "token",
        "password",
        "secret",
    };

    private static bool IsSensitiveQueryParameter(string name) =>
        SensitiveQueryParameters.Contains(name) ||
        name.StartsWith("x-", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Redacts credential-bearing query-string parameters from a request URI before it is logged.
    /// Scheme, host, path and fragment are preserved verbatim; a <see langword="null"/> URI and a URI
    /// with no query string both round-trip unchanged. Never throws: a logging handler that fails
    /// while logging an error is strictly worse than the leak it was added to prevent.
    /// </summary>
    private string? ScrubUrl(Uri? uri)
    {
        if (uri is null)
            return null;

        try
        {
            var original = uri.OriginalString;
            var queryStart = original.IndexOf('?');
            if (queryStart < 0)
                return original;

            var prefix = original[..queryStart];
            var rest = original[(queryStart + 1)..];

            var fragment = string.Empty;
            var fragmentStart = rest.IndexOf('#');
            if (fragmentStart >= 0)
            {
                fragment = rest[fragmentStart..];
                rest = rest[..fragmentStart];
            }

            if (rest.Length == 0)
                return original;

            var parts = rest.Split('&');
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (part.Length == 0)
                    continue;

                var eq = part.IndexOf('=');
                if (eq < 0)
                {
                    // Valueless flag - nothing to leak, but the name itself may be secret-shaped.
                    parts[i] = IsSensitiveQueryParameter(part) ? part : Scrub(part);
                    continue;
                }

                var name = part[..eq];
                var value = part[(eq + 1)..];
                parts[i] = IsSensitiveQueryParameter(Uri.UnescapeDataString(name))
                    ? string.Concat(name, "=[REDACTED]")
                    : string.Concat(name, "=", Scrub(value));
            }

            return string.Concat(prefix, "?", string.Join("&", parts), fragment);
        }
        catch
        {
            // Redaction must never break logging; fail closed rather than emitting the raw URL.
            return "[REDACTED-URL]";
        }
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
            return await base.SendAsync(request, cancellationToken);

        var requestBody = Scrub(await ReadRequestBodyAsync(request, cancellationToken));
        var redactedHeaders = Scrub(BuildRedactedHeaderString(request.Headers));

        logger.LogDebug(
            "Provider HTTP request: {Method} {Url} | Headers: {Headers} | Body: {Body}",
            request.Method,
            ScrubUrl(request.RequestUri),
            redactedHeaders,
            requestBody);

        var sw = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogDebug(
                "Provider HTTP error after {ElapsedMs}ms: {Method} {Url} - {Error}",
                sw.ElapsedMilliseconds,
                request.Method,
                ScrubUrl(request.RequestUri),
                ex.Message);
            throw;
        }

        sw.Stop();

        // For streaming responses (text/event-stream) we must NOT buffer the body -
        // the caller reads it as a live stream. Log status + elapsed only.
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (contentType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug(
                "Provider HTTP response: {Method} {Url} | Status: {Status} | Streaming | ElapsedMs: {ElapsedMs}",
                request.Method,
                ScrubUrl(request.RequestUri),
                (int)response.StatusCode,
                sw.ElapsedMilliseconds);
        }
        else
        {
            var rawBody = await ReadResponseBodyAsync(response, cancellationToken);
            var responseBody = Scrub(rawBody);
            var usage = ExtractUsageSummary(rawBody);
            logger.LogDebug(
                "Provider HTTP response: {Method} {Url} | Status: {Status} | ElapsedMs: {ElapsedMs} | Usage: {Usage} | Body: {Body}",
                request.Method,
                ScrubUrl(request.RequestUri),
                (int)response.StatusCode,
                sw.ElapsedMilliseconds,
                usage,
                responseBody);
        }

        return response;
    }

    private static async Task<string> ReadRequestBodyAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is null)
            return string.Empty;

        try
        {
            // Buffer the content so the provider can still read it.
            await request.Content.LoadIntoBufferAsync(cancellationToken);
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            return TruncateIfNeeded(body);
        }
        catch
        {
            return "<unreadable>";
        }
    }

    private static async Task<string> ReadResponseBodyAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            // Buffer so the caller can still read.
            await response.Content.LoadIntoBufferAsync(cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return TruncateIfNeeded(body);
        }
        catch
        {
            return "<unreadable>";
        }
    }

    /// <summary>
    /// Best-effort extraction of the provider-reported token <c>usage</c> object from a non-streamed
    /// JSON response body. Anthropic and OpenAI both surface a top-level <c>usage</c> object at the
    /// HTTP layer for non-streamed calls, so a shallow parse suffices without provider-specific code.
    /// Returns <c>"n/a"</c> when no usage object is present or the body is not parseable JSON.
    /// Streamed responses report usage inside SSE events, so that path is a documented follow-up.
    /// </summary>
    private static string ExtractUsageSummary(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "n/a";

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("usage", out var usage) &&
                usage.ValueKind == JsonValueKind.Object)
            {
                return usage.GetRawText();
            }
        }
        catch (JsonException)
        {
            // Non-JSON or truncated body — usage is simply unavailable at this layer.
        }

        return "n/a";
    }

    private static string BuildRedactedHeaderString(System.Net.Http.Headers.HttpRequestHeaders headers)
    {
        var sb = new StringBuilder();
        foreach (var header in headers)
        {
            if (sb.Length > 0) sb.Append(", ");
            var value = RedactedHeaders.Contains(header.Key)
                ? "[REDACTED]"
                : string.Join("; ", header.Value);
            sb.Append(header.Key).Append('=').Append(value);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Truncates body strings to 4 KB to keep logs manageable.
    /// Provider request bodies (large context windows) can be very large.
    /// </summary>
    private static string TruncateIfNeeded(string body, int maxChars = 4096)
    {
        if (body.Length <= maxChars)
            return body;
        return string.Concat(body.AsSpan(0, maxChars), $"... [truncated, total {body.Length} chars]");
    }
}
