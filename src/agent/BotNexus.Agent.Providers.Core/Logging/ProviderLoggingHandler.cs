using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.Core.Logging;

/// <summary>
/// <see cref="DelegatingHandler"/> that logs provider HTTP requests and responses at
/// <see cref="LogLevel.Debug"/> level.
/// <para>
/// Auth headers (<c>x-api-key</c>, <c>Authorization</c>) are always redacted to <c>[REDACTED]</c>
/// before logging, regardless of configuration.
/// </para>
/// <para>
/// For streaming responses the response body is unavailable after headers — only the status code
/// and elapsed time are logged on the response side. The caller (provider) owns the stream and
/// must log its own usage data from SSE events.
/// </para>
/// </summary>
public sealed class ProviderLoggingHandler(ILogger<ProviderLoggingHandler> logger) : DelegatingHandler
{
    /// <summary>Header names that are always redacted in request logs.</summary>
    private static readonly HashSet<string> RedactedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "x-api-key",
        "Authorization",
    };

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
            return await base.SendAsync(request, cancellationToken);

        var requestBody = await ReadRequestBodyAsync(request, cancellationToken);
        var redactedHeaders = BuildRedactedHeaderString(request.Headers);

        logger.LogDebug(
            "Provider HTTP request: {Method} {Url} | Headers: {Headers} | Body: {Body}",
            request.Method,
            request.RequestUri,
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
                request.RequestUri,
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
                request.RequestUri,
                (int)response.StatusCode,
                sw.ElapsedMilliseconds);
        }
        else
        {
            var responseBody = await ReadResponseBodyAsync(response, cancellationToken);
            logger.LogDebug(
                "Provider HTTP response: {Method} {Url} | Status: {Status} | ElapsedMs: {ElapsedMs} | Body: {Body}",
                request.Method,
                request.RequestUri,
                (int)response.StatusCode,
                sw.ElapsedMilliseconds,
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
