using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using BotNexus.Extensions.Mcp.Protocol;

namespace BotNexus.Extensions.Mcp.Transport;

/// <summary>
/// MCP transport using HTTP POST for requests and Server-Sent Events for responses.
/// Implements the MCP Streamable HTTP specification.
/// </summary>
public sealed class HttpSseMcpTransport : IMcpTransport
{
    private const string SessionIdHeader = "Mcp-Session-Id";

    private readonly Uri _endpoint;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _connectTimeout;
    private readonly int _maxReconnectAttempts;
    private readonly TimeSpan _reconnectBaseDelay;
    private readonly TimeSpan _minReconnectDelay;
    private readonly IReadOnlyDictionary<string, string>? _headers;

    private static readonly TimeSpan DefaultReconnectBaseDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultMinReconnectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(30);

    private string? _sessionId;
    private readonly SemaphoreSlim _reinitLock = new(1, 1);
    private int _sessionEpoch;
    private int _reinitRequestId;
    private CancellationTokenSource? _sseCts;
    private Task? _sseTask;
    private readonly Channel<JsonRpcResponse> _responseChannel =
        Channel.CreateUnbounded<JsonRpcResponse>(new UnboundedChannelOptions { SingleReader = false });
    private bool _connected;
    private bool _disposed;

    /// <summary>
    /// Creates a new HTTP/SSE transport for the given MCP server endpoint.
    /// </summary>
    /// <param name="endpoint">The MCP server URL.</param>
    /// <param name="headers">Optional additional HTTP headers.</param>
    /// <param name="httpClient">Optional pre-configured HttpClient. If null, one is created internally.</param>
    /// <param name="connectTimeout">Connection timeout. Default: 30 seconds.</param>
    /// <param name="maxReconnectAttempts">Maximum <em>consecutive zero-progress</em> SSE reconnection attempts. Default: 3.</param>
    /// <param name="reconnectBaseDelay">Base delay for the exponential reconnect backoff. Default: 1 second. Intended for tests.</param>
    /// <param name="minReconnectDelay">Floor applied to every reconnect delay. Default: 1 second. Intended for tests.</param>
    public HttpSseMcpTransport(
        Uri endpoint,
        IReadOnlyDictionary<string, string>? headers = null,
        HttpClient? httpClient = null,
        TimeSpan? connectTimeout = null,
        int maxReconnectAttempts = 3,
        TimeSpan? reconnectBaseDelay = null,
        TimeSpan? minReconnectDelay = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        _endpoint = endpoint;
        _headers = headers;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(30);
        _maxReconnectAttempts = maxReconnectAttempts;
        _reconnectBaseDelay = reconnectBaseDelay ?? DefaultReconnectBaseDelay;
        _minReconnectDelay = minReconnectDelay ?? DefaultMinReconnectDelay;
    }

    /// <summary>Gets the session ID assigned by the server, if any.</summary>
    internal string? SessionId => _sessionId;

    /// <summary>Gets the background SSE read loop task, if the transport opened a persistent stream.</summary>
    internal Task? SseLoopTask => _sseTask;

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(_connectTimeout);

        // Try legacy SSE first (GET-based persistent stream).
        // If server returns 405 (Streamable HTTP servers reject GET),
        // mark as connected — Streamable HTTP needs no initial connection,
        // the initialize handshake happens via POST in SendAsync.
        try
        {
            var request = CreateRequest(HttpMethod.Get);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                connectCts.Token).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();
            CaptureSessionId(response);

            _sseCts = new CancellationTokenSource();
            _sseTask = Task.Run(
                () => SseReadLoopAsync(response, _sseCts.Token),
                CancellationToken.None);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
        {
            // 405 = Streamable HTTP server. No persistent SSE stream needed —
            // all communication happens via POST requests in SendAsync.
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Failed to connect to MCP server at {_endpoint} within {_connectTimeout.TotalSeconds}s.");
        }

        _connected = true;
    }

    /// <inheritdoc />
    public async Task SendAsync(JsonRpcRequest message, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_connected)
            throw new InvalidOperationException("Transport is not connected.");

        var response = await SendWithSessionRecoveryAsync(
            () =>
            {
                var request = CreateRequest(HttpMethod.Post);
                request.Content = JsonContent.Create(message, JsonContext.Default.JsonRpcRequest);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                return request;
            },
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        CaptureSessionId(response);

        var contentType = response.Content.Headers.ContentType?.MediaType;

        if (string.Equals(contentType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            await ReadSseResponseAsync(response, ct).ConfigureAwait(false);
        }
        else
        {
            var jsonResponse = await response.Content.ReadFromJsonAsync(
                JsonContext.Default.JsonRpcResponse, ct).ConfigureAwait(false);

            if (jsonResponse is not null)
            {
                await _responseChannel.Writer.WriteAsync(jsonResponse, ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task SendNotificationAsync(JsonRpcNotification message, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_connected)
            throw new InvalidOperationException("Transport is not connected.");

        using var response = await SendWithSessionRecoveryAsync(
            () =>
            {
                var request = CreateRequest(HttpMethod.Post);
                request.Content = JsonContent.Create(message, JsonContext.Default.JsonRpcNotification);
                return request;
            },
            HttpCompletionOption.ResponseContentRead,
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        CaptureSessionId(response);
    }

    /// <inheritdoc />
    public async Task<JsonRpcResponse> ReceiveAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await _responseChannel.Reader.ReadAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        _connected = false;

        if (_sseCts is not null)
        {
            await _sseCts.CancelAsync().ConfigureAwait(false);
        }

        if (_sseTask is not null)
        {
            try
            {
                await _sseTask.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { }
        }

        // Attempt session termination per MCP spec
        if (_sessionId is not null)
        {
            try
            {
                var request = CreateRequest(HttpMethod.Delete);
                await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort cleanup
            }
        }

        _responseChannel.Writer.TryComplete();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _connected = false;

        if (_sseCts is not null)
        {
            await _sseCts.CancelAsync().ConfigureAwait(false);
        }

        if (_sseTask is not null)
        {
            try
            {
                await _sseTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch { }
        }

        _responseChannel.Writer.TryComplete();
        _sseCts?.Dispose();
        _reinitLock.Dispose();

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    /// <summary>
    /// Sends a request, recovering from an expired MCP session.
    /// Per the MCP Streamable HTTP spec, HTTP 404 in response to a request that carried an
    /// <c>Mcp-Session-Id</c> means the session expired; the client MUST start a new session by
    /// re-sending <c>InitializeRequest</c> without a session id. The original request is then
    /// replayed exactly once. Any other status code is returned unchanged to the caller so that
    /// existing behaviour (EnsureSuccessStatusCode) is preserved.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithSessionRecoveryAsync(
        Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption completionOption,
        CancellationToken ct)
    {
        var epoch = Volatile.Read(ref _sessionEpoch);
        var request = requestFactory();
        var carriedSessionId = request.Headers.Contains(SessionIdHeader);

        var response = await _httpClient.SendAsync(request, completionOption, ct).ConfigureAwait(false);

        // Fail closed: only an expired-session 404 is special-cased.
        if (response.StatusCode != HttpStatusCode.NotFound || !carriedSessionId)
        {
            return response;
        }

        response.Dispose();

        if (!await TryReinitializeSessionAsync(epoch, ct).ConfigureAwait(false))
        {
            throw new HttpRequestException(
                $"MCP session expired at {_endpoint} and re-initialization failed.",
                inner: null,
                statusCode: HttpStatusCode.NotFound);
        }

        // Exactly one replay. Whatever comes back - including another 404 - is returned
        // verbatim, so a server that keeps 404ing surfaces an error instead of looping.
        return await _httpClient.SendAsync(requestFactory(), completionOption, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Clears the expired session and performs a fresh <c>initialize</c> handshake.
    /// Serialised so concurrent in-flight requests cannot race two initializes; a caller whose
    /// epoch is stale simply adopts the session another caller already established.
    /// </summary>
    private async Task<bool> TryReinitializeSessionAsync(int epoch, CancellationToken ct)
    {
        await _reinitLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _sessionEpoch) != epoch)
            {
                // Another request already re-initialized while we waited.
                return true;
            }

            _sessionId = null;

            var initRequest = new JsonRpcRequest
            {
                Id = Interlocked.Decrement(ref _reinitRequestId),
                Method = "initialize",
                Params = JsonSerializer.SerializeToElement(
                    new McpInitializeParams(), JsonContext.Default.McpInitializeParams),
            };

            var request = CreateRequest(HttpMethod.Post);
            request.Content = JsonContent.Create(initRequest, JsonContext.Default.JsonRpcRequest);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            CaptureSessionId(response);

            // Drain the initialize result. It is deliberately NOT enqueued on the response
            // channel - the caller is waiting for the reply to its own request id.
            await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            Interlocked.Increment(ref _sessionEpoch);

            // notifications/initialized per MCP spec. Best-effort: a failure here must not
            // mask the recovered session.
            try
            {
                var notifyRequest = CreateRequest(HttpMethod.Post);
                notifyRequest.Content = JsonContent.Create(
                    new JsonRpcNotification { Method = "notifications/initialized" },
                    JsonContext.Default.JsonRpcNotification);
                using var notifyResponse = await _httpClient.SendAsync(notifyRequest, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                // Best-effort.
            }

            return true;
        }
        finally
        {
            _reinitLock.Release();
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method)
    {
        var request = new HttpRequestMessage(method, _endpoint);

        // Force HTTP/1.1 — many MCP servers don't flush SSE events properly
        // over HTTP/2, causing .NET's response stream to hang indefinitely.
        request.Version = HttpVersion.Version11;

        var sessionId = _sessionId;
        if (sessionId is not null)
        {
            request.Headers.TryAddWithoutValidation(SessionIdHeader, sessionId);
        }

        if (_headers is not null)
        {
            foreach (var (key, value) in _headers)
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }

        return request;
    }

    private void CaptureSessionId(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues(SessionIdHeader, out var values))
        {
            var id = values.FirstOrDefault();
            if (!string.IsNullOrEmpty(id))
            {
                _sessionId = id;
            }
        }
    }

    private async Task ReadSseResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        // For single request/response exchanges, read until we get one complete
        // SSE event then return. Many MCP servers keep the SSE stream open after
        // sending the response, so ParseSseStreamAsync would hang forever.
        string? data = null;

        while (!ct.IsCancellationRequested)
        {
            // Per-line timeout prevents hanging if the server keeps the stream open
            // without sending more data after the response.
            using var lineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lineCts.CancelAfter(TimeSpan.FromSeconds(10));

            string? line;
            try
            {
                line = await reader.ReadLineAsync(lineCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Line read timed out — flush any buffered data and return
                break;
            }

            if (line is null) break; // EOF

            if (line.Length == 0)
            {
                // Blank line = end of SSE event
                if (data is not null)
                {
                    TryEnqueueResponse(data);
                    return; // Got our response, done
                }
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line.Length > 5 ? line[5..].Trim() : string.Empty;
                data = data is null ? value : $"{data}\n{value}";
            }
            // Ignore event:, id:, retry:, and comment lines
        }

        // Flush trailing data without a final blank line
        if (data is not null)
        {
            TryEnqueueResponse(data);
        }
    }

    /// <summary>
    /// Background loop that reads the persistent SSE connection with auto-reconnect.
    /// </summary>
    private async Task SseReadLoopAsync(HttpResponseMessage initialResponse, CancellationToken ct)
    {
        // `attempt` counts CONSECUTIVE ZERO-PROGRESS reconnects. It is reset only when a
        // connection actually delivered at least one SSE event to _responseChannel, so a
        // server that returns 200 text/event-stream with an instantly-closed body can no
        // longer be scored as success and looped on forever (issue #2564). A healthy
        // long-lived stream that delivered events and then closed still resets the counter.
        var attempt = 0;

        try
        {
            using (var stream = await initialResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            using (var reader = new StreamReader(stream))
            {
                var initialEvents = await ParseSseStreamAsync(reader, ct).ConfigureAwait(false);
                if (initialEvents > 0)
                {
                    attempt = 0;
                }
            }

            while (!ct.IsCancellationRequested && attempt < _maxReconnectAttempts)
            {
                attempt++;
                var delay = ComputeReconnectDelay(attempt);
                await Task.Delay(delay, ct).ConfigureAwait(false);

                try
                {
                    var request = CreateRequest(HttpMethod.Get);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

                    var response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        ct).ConfigureAwait(false);

                    response.EnsureSuccessStatusCode();
                    CaptureSessionId(response);

                    using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    using var reader = new StreamReader(stream);
                    var eventCount = await ParseSseStreamAsync(reader, ct).ConfigureAwait(false);

                    if (eventCount > 0)
                    {
                        attempt = 0;
                    }
                }
                catch (HttpRequestException) when (!ct.IsCancellationRequested)
                {
                    // Will retry
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    /// <summary>
    /// Computes the delay before reconnect number <paramref name="attempt"/>: exponential from the
    /// base delay, clamped below by the reconnect floor and above by 30 seconds.
    /// </summary>
    internal TimeSpan ComputeReconnectDelay(int attempt)
    {
        var exponential = _reconnectBaseDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, attempt - 1));
        var clamped = Math.Min(exponential, MaxReconnectDelay.TotalMilliseconds);
        clamped = Math.Max(clamped, _minReconnectDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(Math.Min(clamped, MaxReconnectDelay.TotalMilliseconds));
    }

    /// <summary>
    /// Parses an SSE stream, extracting JSON-RPC response messages from <c>event: message</c> events.
    /// </summary>
    /// <returns>The number of SSE events successfully delivered to the response channel.</returns>
    internal async Task<int> ParseSseStreamAsync(TextReader reader, CancellationToken ct)
    {
        string? eventType = null;
        string? data = null;
        var delivered = 0;

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;

            if (line.Length == 0)
            {
                if (data is not null)
                {
                    if (eventType is null or "message")
                    {
                        if (TryEnqueueResponse(data))
                        {
                            delivered++;
                        }
                    }

                    eventType = null;
                    data = null;
                }

                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventType = line.Length > 6 ? line[6..].Trim() : string.Empty;
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line.Length > 5 ? line[5..].Trim() : string.Empty;
                data = data is null ? value : $"{data}\n{value}";
            }
            // Ignore id:, retry:, and comment lines (starting with :)
        }

        // Flush any trailing event without a final blank line
        if (data is not null && eventType is null or "message")
        {
            if (TryEnqueueResponse(data))
            {
                delivered++;
            }
        }

        return delivered;
    }

    private bool TryEnqueueResponse(string json)
    {
        try
        {
            var response = JsonSerializer.Deserialize(json, JsonContext.Default.JsonRpcResponse);
            if (response is not null)
            {
                return _responseChannel.Writer.TryWrite(response);
            }
        }
        catch (JsonException)
        {
            // Skip malformed SSE data
        }

        return false;
    }
}
