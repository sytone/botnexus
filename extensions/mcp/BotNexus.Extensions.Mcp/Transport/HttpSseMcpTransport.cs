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
    private readonly Uri _endpoint;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _connectTimeout;
    private readonly int _maxReconnectAttempts;
    private readonly IReadOnlyDictionary<string, string>? _headers;

    private string? _sessionId;
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
    /// <param name="maxReconnectAttempts">Maximum SSE reconnection attempts. Default: 3.</param>
    public HttpSseMcpTransport(
        Uri endpoint,
        IReadOnlyDictionary<string, string>? headers = null,
        HttpClient? httpClient = null,
        TimeSpan? connectTimeout = null,
        int maxReconnectAttempts = 3)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        _endpoint = endpoint;
        _headers = headers;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(30);
        _maxReconnectAttempts = maxReconnectAttempts;
    }

    /// <summary>Gets the session ID assigned by the server, if any.</summary>
    internal string? SessionId => _sessionId;

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(_connectTimeout);

        // Try Streamable HTTP first (POST-based, no initial SSE stream needed).
        // Fall back to legacy SSE (GET-based) if POST returns 405 Method Not Allowed.
        try
        {
            await ConnectStreamableHttpAsync(connectCts.Token).ConfigureAwait(false);
            _connected = true;
            return;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
        {
            // 405 = server doesn't accept POST for connect, try legacy GET-based SSE
        }

        try
        {
            await ConnectLegacySseAsync(connectCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Failed to connect to MCP server at {_endpoint} within {_connectTimeout.TotalSeconds}s.");
        }

        _connected = true;
    }

    /// <summary>
    /// Streamable HTTP: send an initialize POST. The server handles everything via POST
    /// responses (JSON or SSE). No persistent SSE listener needed on connect.
    /// </summary>
    private async Task ConnectStreamableHttpAsync(CancellationToken ct)
    {
        // For Streamable HTTP, we just verify the endpoint accepts POST.
        // The actual initialize handshake happens in McpClient.InitializeAsync via SendAsync.
        // We send a lightweight POST to verify connectivity and capture the session ID.
        var request = CreateRequest(HttpMethod.Post);
        request.Content = JsonContent.Create(
            new { jsonrpc = "2.0", method = "ping", id = 0 },
            options: new JsonSerializerOptions());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        // Some servers return 200 with a JSON-RPC error for unknown methods — that's fine,
        // it proves the endpoint is alive and accepts POST.
        if (response.IsSuccessStatusCode)
        {
            CaptureSessionId(response);
            // Consume the response body to release the connection
            await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return;
        }

        response.EnsureSuccessStatusCode(); // throws for non-405 errors
    }

    /// <summary>
    /// Legacy SSE: send a GET to establish a persistent SSE event stream.
    /// </summary>
    private async Task ConnectLegacySseAsync(CancellationToken ct)
    {
        var request = CreateRequest(HttpMethod.Get);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        CaptureSessionId(response);

        _sseCts = new CancellationTokenSource();
        _sseTask = Task.Run(
            () => SseReadLoopAsync(response, _sseCts.Token),
            CancellationToken.None);
    }

    /// <inheritdoc />
    public async Task SendAsync(JsonRpcRequest message, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_connected)
            throw new InvalidOperationException("Transport is not connected.");

        var request = CreateRequest(HttpMethod.Post);
        request.Content = JsonContent.Create(message, JsonContext.Default.JsonRpcRequest);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var response = await _httpClient.SendAsync(
            request,
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

        var request = CreateRequest(HttpMethod.Post);
        request.Content = JsonContent.Create(message, JsonContext.Default.JsonRpcNotification);

        var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
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

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method)
    {
        var request = new HttpRequestMessage(method, _endpoint);

        if (_sessionId is not null)
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
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
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var values))
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

        await ParseSseStreamAsync(reader, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Background loop that reads the persistent SSE connection with auto-reconnect.
    /// </summary>
    private async Task SseReadLoopAsync(HttpResponseMessage initialResponse, CancellationToken ct)
    {
        var attempt = 0;

        try
        {
            using (var stream = await initialResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            using (var reader = new StreamReader(stream))
            {
                await ParseSseStreamAsync(reader, ct).ConfigureAwait(false);
            }

            while (!ct.IsCancellationRequested && attempt < _maxReconnectAttempts)
            {
                attempt++;
                var delay = TimeSpan.FromMilliseconds(Math.Min(1000 * Math.Pow(2, attempt - 1), 30_000));
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
                    await ParseSseStreamAsync(reader, ct).ConfigureAwait(false);

                    attempt = 0;
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
    /// Parses an SSE stream, extracting JSON-RPC response messages from <c>event: message</c> events.
    /// </summary>
    internal async Task ParseSseStreamAsync(TextReader reader, CancellationToken ct)
    {
        string? eventType = null;
        string? data = null;

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
                        TryEnqueueResponse(data);
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
            TryEnqueueResponse(data);
        }
    }

    private void TryEnqueueResponse(string json)
    {
        try
        {
            var response = JsonSerializer.Deserialize(json, JsonContext.Default.JsonRpcResponse);
            if (response is not null)
            {
                _responseChannel.Writer.TryWrite(response);
            }
        }
        catch (JsonException)
        {
            // Skip malformed SSE data
        }
    }
}
