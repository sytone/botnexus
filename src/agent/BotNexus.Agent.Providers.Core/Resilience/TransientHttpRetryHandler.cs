using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.Core.Resilience;

/// <summary>
/// <see cref="DelegatingHandler"/> that retries transient provider HTTP failures with a short
/// backoff and a forced fresh connection.
/// <para>
/// The motivating failure is <strong>HTTP 421 Misdirected Request</strong> returned intermittently
/// by the GitHub Copilot endpoints. Per RFC 7540 §9.1.2 a 421 means the request reached a server
/// that cannot produce a response for the target authority — almost always a side effect of HTTP/2
/// connection coalescing reusing a pooled connection. The RFC-specified remedy is to retry the
/// request <em>on a different connection</em>. This handler does exactly that: on a retriable
/// response it sets <see cref="System.Net.Http.Headers.HttpRequestHeaders.ConnectionClose"/> so the
/// socket pool does not reuse the misdirected connection for the retry.
/// </para>
/// <para>
/// This sits in front of the provider transport, so the retry happens before a failed response is
/// converted into an exception or an empty <c>ErrorEvent</c>. Every provider that flows through the
/// shared provider <see cref="HttpClient"/> benefits — not just session compaction, which was the
/// most visible victim (a 421 on the compaction-summary call aborted compaction, eventually tripping
/// the per-session circuit breaker and leaving the session unable to shed context).
/// </para>
/// <para>
/// Retriable conditions:
/// <list type="bullet">
/// <item><description><c>421 Misdirected Request</c> — connection coalescing; retry on a fresh connection.</description></item>
/// <item><description><c>408 Request Timeout</c>, <c>502 Bad Gateway</c>, <c>503 Service Unavailable</c>, <c>504 Gateway Timeout</c> — transient upstream/gateway hiccups.</description></item>
/// <item><description>Transport faults that surface as <see cref="HttpRequestException"/> wrapping a transient <see cref="SocketException"/> or <see cref="IOException"/>.</description></item>
/// </list>
/// Deliberately <strong>not</strong> retried here:
/// <list type="bullet">
/// <item><description><c>429 Too Many Requests</c> — owned upstream by <see cref="ProviderHttpErrorHelper"/> /
/// <c>ProviderRateLimitException</c> with server-specified <c>Retry-After</c> handling. Retrying it here
/// blindly would ignore the server's backoff hint.</description></item>
/// <item><description>4xx other than 408/421 (e.g. 400/401/403/404) — these are deterministic; a retry
/// would just fail again.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class TransientHttpRetryHandler : DelegatingHandler
{
    private readonly ILogger<TransientHttpRetryHandler>? _logger;
    private readonly int _maxRetries;
    private readonly TimeSpan _baseDelay;

    /// <summary>Default number of retry attempts after the initial request.</summary>
    internal const int DefaultMaxRetries = 3;

    /// <summary>Default base backoff delay; grows linearly with attempt number.</summary>
    internal static readonly TimeSpan DefaultBaseDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Initializes a new instance of the <see cref="TransientHttpRetryHandler"/> class.
    /// </summary>
    /// <param name="logger">Optional logger for retry diagnostics.</param>
    /// <param name="maxRetries">Maximum retry attempts after the initial send. Defaults to <see cref="DefaultMaxRetries"/>.</param>
    /// <param name="baseDelay">Base backoff delay, scaled by attempt number. Defaults to <see cref="DefaultBaseDelay"/>.</param>
    public TransientHttpRetryHandler(
        ILogger<TransientHttpRetryHandler>? logger = null,
        int? maxRetries = null,
        TimeSpan? baseDelay = null)
    {
        _logger = logger;
        _maxRetries = maxRetries is > 0 ? maxRetries.Value : DefaultMaxRetries;
        _baseDelay = baseDelay is { } d && d > TimeSpan.Zero ? d : DefaultBaseDelay;
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Buffer the request body up front so it can be re-sent on retry. Provider request bodies
        // are StringContent (already buffered), but LoadIntoBufferAsync makes re-send safe for any
        // content type and is a no-op for already-buffered content.
        if (request.Content is not null)
        {
            try
            {
                await request.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // If the body can't be buffered we simply won't retry a body-bearing request safely;
                // fall through and let the single attempt proceed.
            }
        }

        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            HttpResponseMessage? response = null;
            try
            {
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < _maxRetries && IsTransientTransport(ex))
            {
                var delay = ComputeDelay(attempt);
                _logger?.LogWarning(
                    "Transient transport error on {Method} {Url} (attempt {Attempt}/{Max}): {Error}. Retrying in {DelayMs}ms on a fresh connection.",
                    request.Method, request.RequestUri, attempt + 1, _maxRetries, ex.Message, delay.TotalMilliseconds);

                await PrepareForRetryAsync(request, delay, cancellationToken).ConfigureAwait(false);
                attempt++;
                continue;
            }

            // Success path or a non-retriable / exhausted-retry response.
            if (attempt >= _maxRetries || !IsRetriableStatus(response.StatusCode))
            {
                return response;
            }

            var statusDelay = ComputeDelay(attempt);
            _logger?.LogWarning(
                "Transient HTTP {Status} on {Method} {Url} (attempt {Attempt}/{Max}). Retrying in {DelayMs}ms on a fresh connection.",
                (int)response.StatusCode, request.Method, request.RequestUri, attempt + 1, _maxRetries, statusDelay.TotalMilliseconds);

            // Dispose the failed response before retrying to release the connection/buffers.
            response.Dispose();
            await PrepareForRetryAsync(request, statusDelay, cancellationToken).ConfigureAwait(false);
            attempt++;
        }
    }

    /// <summary>
    /// Determines whether an HTTP status code represents a transient failure worth retrying.
    /// 429 is intentionally excluded — it is handled upstream with server-provided Retry-After.
    /// </summary>
    public static bool IsRetriableStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.MisdirectedRequest => true,  // 421 — connection coalescing
        HttpStatusCode.RequestTimeout => true,       // 408
        HttpStatusCode.BadGateway => true,           // 502
        HttpStatusCode.ServiceUnavailable => true,   // 503
        HttpStatusCode.GatewayTimeout => true,       // 504
        _ => false
    };

    /// <summary>
    /// Determines whether an <see cref="HttpRequestException"/> wraps a transient transport fault
    /// (socket reset/abort or IO error) that is safe to retry.
    /// </summary>
    public static bool IsTransientTransport(HttpRequestException ex)
    {
        for (Exception? cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is SocketException socket)
            {
                return socket.SocketErrorCode is
                    SocketError.ConnectionReset or
                    SocketError.ConnectionAborted or
                    SocketError.TimedOut or
                    SocketError.HostUnreachable or
                    SocketError.NetworkUnreachable or
                    SocketError.TryAgain;
            }

            if (cur is IOException)
            {
                return true;
            }
        }

        return false;
    }

    private TimeSpan ComputeDelay(int attempt) =>
        TimeSpan.FromMilliseconds(_baseDelay.TotalMilliseconds * (attempt + 1));

    /// <summary>
    /// Prepares a request for retry: waits the backoff, then forces the next send onto a fresh
    /// connection by setting <c>Connection: close</c> so the misdirected (coalesced) connection is
    /// not reused. This is the RFC 7540 §9.1.2 remedy for HTTP 421.
    /// </summary>
    private static async Task PrepareForRetryAsync(
        HttpRequestMessage request, TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

        // Force a new connection for the retry. ConnectionClose tells the socket pool not to reuse
        // the connection that produced the misdirected response.
        request.Headers.ConnectionClose = true;
    }
}
