using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using BotNexus.Satellite.Client.Core;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace BotNexus.Satellite.Client.SignalR;

/// <summary>Shared credential containment boundary for negotiation and WebSocket upgrade requests.</summary>
public static class TransportSecurity
{
    public static HttpMessageHandler CreateHandler(ConnectionRequest request, HttpMessageHandler inner)
    {
        ValidateRequest(request);
        if (inner is HttpClientHandler http)
        {
            http.AllowAutoRedirect = false;
            http.UseCookies = false;
            http.UseDefaultCredentials = false;
        }
        if (inner is SocketsHttpHandler sockets)
        {
            sockets.AllowAutoRedirect = false;
            sockets.UseCookies = false;
            sockets.Credentials = null;
        }
        return new OriginHandler(request, inner);
    }

    public static HttpMessageHandler CreateWebSocketHandler(ConnectionRequest request, HttpMessageHandler inner)
    {
        ValidateRequest(request);
        if (inner is HttpClientHandler http) { http.AllowAutoRedirect = false; http.UseCookies = false; http.UseDefaultCredentials = false; }
        if (inner is SocketsHttpHandler sockets) { sockets.AllowAutoRedirect = false; sockets.UseCookies = false; sockets.Credentials = null; }
        return new UpgradeHandler(request, inner);
    }

    private sealed class UpgradeHandler(ConnectionRequest request, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
        {
            RequireOrigin(request.HubUri, message.RequestUri, true);
            foreach (var header in request.Headers) { message.Headers.Remove(header.Key); message.Headers.Add(header.Key, header.Value); }
            var response = await base.SendAsync(message, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.SwitchingProtocols) return response;
            var status = (int)response.StatusCode;
            var failure = status == 403 ? ConnectionFailure.AccessDenied :
                status == 401 || status is >= 300 and < 400 || response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true
                ? ConnectionFailure.AuthenticationRequired : ConnectionFailure.Transient;
            response.Dispose();
            throw new SatelliteConnectionException(failure);
        }
    }

    public static async ValueTask<WebSocket> ConnectWebSocketAsync(ConnectionRequest request, Uri uri,
        Func<Uri, IReadOnlyDictionary<string, string>, CancellationToken, ValueTask<WebSocket>> connector,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        RequireOrigin(request.HubUri, uri, true);
        cancellationToken.ThrowIfCancellationRequested();
        return await connector(uri, request.Headers, cancellationToken).ConfigureAwait(false);
    }

    internal static ConnectionRequest Snapshot(ConnectionRequest request)
    {
        ValidateRequest(request);
        return new ConnectionRequest { HubUri = request.HubUri, Headers = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase) };
    }

    private static void ValidateRequest(ConnectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var origin = request.HubUri.GetLeftPart(UriPartial.Authority);
        var options = new SatelliteClientOptions { GatewayEndpoint = origin, AllowLoopbackHttp = true };
        if (options.Validate().Count != 0 || request.HubUri != options.GetHubUri() ||
            !request.Headers.TryGetValue("X-Api-Key", out var key) || !IsToken(key) ||
            request.Headers.Any(h => !h.Key.Equals("X-Api-Key", StringComparison.OrdinalIgnoreCase) && !h.Key.Equals("X-Tunnel-Authorization", StringComparison.OrdinalIgnoreCase)))
            throw new SatelliteConnectionException(ConnectionFailure.AccessDenied);
        if (request.Headers.TryGetValue("X-Tunnel-Authorization", out var tunnel) &&
            (!tunnel.StartsWith("tunnel ", StringComparison.Ordinal) || !IsToken(tunnel[7..])))
            throw new SatelliteConnectionException(ConnectionFailure.AccessDenied);
    }

    private static bool IsToken(string token) => !string.IsNullOrEmpty(token) && !token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    private static void RequireOrigin(Uri origin, Uri? target, bool websocket)
    {
        var scheme = websocket ? (origin.Scheme == "https" ? "wss" : "ws") : origin.Scheme;
        if (target is null || !target.IsAbsoluteUri || target.UserInfo.Length != 0 || target.Fragment.Length != 0 ||
            !target.Scheme.Equals(scheme, StringComparison.OrdinalIgnoreCase) ||
            !target.IdnHost.Equals(origin.IdnHost, StringComparison.OrdinalIgnoreCase) || target.Port != origin.Port)
            throw new SatelliteConnectionException(ConnectionFailure.AccessDenied);
    }

    private sealed class OriginHandler(ConnectionRequest request, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken cancellationToken)
        {
            RequireOrigin(request.HubUri, message.RequestUri, false);
            // Overwrite, never merge, supplied credentials. A negotiate accessToken cannot replace gateway authentication.
            message.Headers.Authorization = null;
            foreach (var header in request.Headers)
            {
                message.Headers.Remove(header.Key);
                message.Headers.Add(header.Key, header.Value);
            }
            var response = await base.SendAsync(message, cancellationToken).ConfigureAwait(false);
            try
            {
                var status = (int)response.StatusCode;
                if (status == 403) throw new SatelliteConnectionException(ConnectionFailure.AccessDenied);
                if (status == 401 || status is >= 300 and < 400 ||
                    response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
                    throw new SatelliteConnectionException(ConnectionFailure.AuthenticationRequired);
                if (!response.IsSuccessStatusCode) throw new SatelliteConnectionException(ConnectionFailure.Transient);
                if (message.RequestUri?.AbsolutePath.EndsWith("/negotiate", StringComparison.Ordinal) == true)
                {
                    await response.Content.LoadIntoBufferAsync(65536, cancellationToken).ConfigureAwait(false);
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    if (body.TrimStart().StartsWith('<')) throw new SatelliteConnectionException(ConnectionFailure.AuthenticationRequired);
                    using var document = JsonDocument.Parse(body);
                    if (document.RootElement.TryGetProperty("url", out var redirect))
                    {
                        if (!Uri.TryCreate(redirect.GetString(), UriKind.Absolute, out var uri))
                            throw new SatelliteConnectionException(ConnectionFailure.AccessDenied);
                        RequireOrigin(request.HubUri, uri, false);
                    }
                }
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
    }
}

/// <summary>Real SignalR data plane, with one runner owning reconnect and parameterless heartbeats.</summary>
public sealed class SignalRSatelliteTransport : ISatelliteTransport
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);
    private readonly Func<HttpMessageHandler> _handlerFactory;
    private readonly Func<Uri, IReadOnlyDictionary<string, string>, CancellationToken, ValueTask<WebSocket>> _connector;

    public SignalRSatelliteTransport(Func<HttpMessageHandler>? handlerFactory = null,
        Func<Uri, IReadOnlyDictionary<string, string>, CancellationToken, ValueTask<WebSocket>>? connector = null)
    {
        _handlerFactory = handlerFactory ?? (() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        _connector = connector ?? ConnectSocketAsync;
    }

    public async Task<ISatelliteConnection> ConnectAsync(ConnectionRequest request, CancellationToken cancellationToken)
    {
        request = TransportSecurity.Snapshot(request);
        var hub = new HubConnectionBuilder().WithUrl(request.HubUri, options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.SkipNegotiation = false;
            options.CloseTimeout = CloseTimeout;
            foreach (var header in request.Headers) options.Headers.Add(header.Key, header.Value);
            options.HttpMessageHandlerFactory = original =>
            {
                original.Dispose();
                return TransportSecurity.CreateHandler(request, _handlerFactory());
            };
            options.WebSocketFactory = (context, token) => TransportSecurity.ConnectWebSocketAsync(request, context.Uri, _connector, token);
        }).Build();
        hub.HandshakeTimeout = OperationTimeout;
        var connection = new Connection(hub);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OperationTimeout);
        try
        {
            await hub.StartAsync(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            return connection;
        }
        catch (Exception error)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new SatelliteConnectionException(Classify(error));
        }
    }

    private static async ValueTask<WebSocket> ConnectSocketAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken token)
    {
        var socket = new ClientWebSocket();
        var hub = new UriBuilder(uri) { Scheme = uri.Scheme == "wss" ? "https" : "http", Path = "/hub/satellite", Query = "", Fragment = "" }.Uri;
        var request = new ConnectionRequest { HubUri = hub, Headers = headers };
        var invoker = new HttpMessageInvoker(TransportSecurity.CreateWebSocketHandler(request, new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }));
        try
        {
            foreach (var header in headers) socket.Options.SetRequestHeader(header.Key, header.Value);
            // Custom factory must apply headers itself. This overload prevents redirects during the upgrade too.
            await socket.ConnectAsync(uri, invoker, token).ConfigureAwait(false);
            return new OwnedWebSocket(socket, invoker);
        }
        catch
        {
            socket.Dispose();
            invoker.Dispose();
            throw;
        }
    }

    internal static ConnectionFailure Classify(Exception? error)
    {
        if (error is SatelliteConnectionException classified) return classified.Failure;
        if (error is HttpRequestException http)
        {
            if (http.StatusCode == HttpStatusCode.Unauthorized) return ConnectionFailure.AuthenticationRequired;
            if (http.StatusCode == HttpStatusCode.Forbidden) return ConnectionFailure.AccessDenied;
        }
        if (error is HubException hub)
        {
            if (hub.Message.Contains("This satellite connection is no longer active.", StringComparison.Ordinal)) return ConnectionFailure.Replaced;
            // Other hub rejections are permissions/authentication failures, not network retry loops.
            return ConnectionFailure.AccessDenied;
        }
        if (error is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                var failure = Classify(inner);
                if (failure != ConnectionFailure.Transient) return failure;
            }
        }
        return error?.InnerException is { } nested ? Classify(nested) : ConnectionFailure.Transient;
    }

    private sealed class Connection : ISatelliteConnection
    {
        private readonly HubConnection _hub;
        private readonly TaskCompletionSource<ConnectionFailure> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;
        public Connection(HubConnection hub)
        {
            _hub = hub;
            _hub.Closed += error => { _closed.TrySetResult(Classify(error)); return Task.CompletedTask; };
        }
        public Task<ConnectionFailure> Closed => _closed.Task;
        public async Task HeartbeatAsync(CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(OperationTimeout);
            try { await _hub.InvokeAsync("Heartbeat", timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (Exception error)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new SatelliteConnectionException(Classify(error));
            }
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _closed.TrySetResult(ConnectionFailure.Transient);
            var disposal = _hub.DisposeAsync().AsTask();
            try { await disposal.WaitAsync(CloseTimeout).ConfigureAwait(false); }
            catch { _ = ObserveAsync(disposal); }
        }
        private static async Task ObserveAsync(Task task) { try { await task.ConfigureAwait(false); } catch { } }
    }

    private sealed class OwnedWebSocket(ClientWebSocket socket, HttpMessageInvoker invoker) : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => socket.CloseStatus;
        public override string? CloseStatusDescription => socket.CloseStatusDescription;
        public override WebSocketState State => socket.State;
        public override string? SubProtocol => socket.SubProtocol;
        public override void Abort() => socket.Abort();
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => socket.CloseAsync(status, description, token);
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => socket.CloseOutputAsync(status, description, token);
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token) => socket.ReceiveAsync(buffer, token);
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token) => socket.SendAsync(buffer, type, end, token);
        public override void Dispose() { socket.Dispose(); invoker.Dispose(); }
    }
}
