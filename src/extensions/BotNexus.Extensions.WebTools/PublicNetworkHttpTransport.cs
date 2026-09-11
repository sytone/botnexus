using System.Net;
using System.Net.Sockets;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Extensions.WebTools;

/// <summary>
/// Owns the web-fetch socket boundary so DNS policy and numerical connection selection cannot
/// be separated by a second, unrestricted hostname lookup. Internal delegates isolate tests
/// without replacing the production HTTP pipeline.
/// </summary>
internal sealed class PublicNetworkHttpTransport : DelegatingHandler
{
    private static readonly HttpRequestOptionsKey<CancellationToken> RequestCancellation = new("WebFetch.RequestCancellation");
    private readonly bool _allowPrivateNetworks;
    private readonly string[] _blockedHosts;
    private readonly IWebProxy _proxy;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly Func<IPEndPoint, CancellationToken, ValueTask<Stream>> _connect;

    internal PublicNetworkHttpTransport(
        WebFetchConfig config,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
        Func<IPEndPoint, CancellationToken, ValueTask<Stream>>? connect = null,
        IWebProxy? proxy = null)
    {
        // A pooled client has one security policy for its lifetime, not a mutable opt-in that
        // could turn an existing private socket into an apparently public-policy connection.
        _allowPrivateNetworks = config.AllowPrivateNetworks;
        _blockedHosts = config.AdditionalBlockedHosts.ToArray();
        _proxy = proxy ?? HttpClient.DefaultProxy;
        _resolve = resolve ?? Dns.GetHostAddressesAsync;
        _connect = connect ?? ConnectSocketAsync;
        InnerHandler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            Proxy = _proxy,
            // Restricted requests reach this handler ONLY after the framework proxy decision
            // permits direct access. Active proxy routes fail closed, never silently fall back.
            UseProxy = _allowPrivateNetworks,
            ConnectTimeout = TimeSpan.FromSeconds(config.TimeoutSeconds),
            ConnectCallback = ConnectAsync
        };
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var uri = request.RequestUri ?? throw new HttpRequestException("Missing URL (SSRF prevention).");
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new HttpRequestException("Only HTTP and HTTPS are permitted (SSRF prevention).");
        if (_blockedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            throw new HttpRequestException("URL host is blocked by configuration (SSRF prevention).");
        if (!_allowPrivateNetworks)
        {
            var verdict = SsrfValidator.Validate(uri, _blockedHosts);
            if (!verdict.IsSafe)
                throw new HttpRequestException(verdict.Reason);

            // The proxy resolves the origin of a forward request or CONNECT tunnel. Validating
            // just the proxy socket cannot guarantee the origin's destination. Consult the
            // framework's platform/environment/NO_PROXY policy before allowing direct access.
            if (!_proxy.IsBypassed(uri))
            {
                var destination = _proxy.GetProxy(uri);
                if (destination is not null && destination != uri)
                    throw new HttpRequestException("Proxy route cannot enforce the destination address policy (SSRF prevention); no direct fallback is permitted.");
            }
        }
        // Keep the origin URI/Host unchanged: SocketsHttpHandler still owns TLS/SNI and normal
        // certificate verification. HTTP/3 cannot use this TCP callback; do not upgrade to it.
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        request.Options.Set(RequestCancellation, token);
        return base.SendAsync(request, token);
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        // Pool connection attempts have their own cancellation lifetime. Explicitly carry the
        // initiating request's timeout/cancellation into DNS and the numerical socket connect.
        context.InitialRequestMessage.Options.TryGetValue(RequestCancellation, out var requestToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, requestToken);
        var ct = cancellation.Token;
        ct.ThrowIfCancellationRequested();
        var host = context.DnsEndPoint.Host;
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host, out var literal)
                ? [literal]
                : (await _resolve(host, ct).ConfigureAwait(false)).ToArray();
        }
        catch (SocketException ex)
        {
            throw new HttpRequestException("DNS resolution failed (SSRF prevention).", ex);
        }
        if (addresses.Length == 0)
            throw new HttpRequestException("DNS returned no addresses (SSRF prevention).");

        // Reject the ENTIRE answer before connecting, including a private candidate after a
        // public one. Both literals and DNS answers use the exact same shared address table.
        if (!_allowPrivateNetworks)
        {
            foreach (var address in addresses)
            {
                var verdict = SsrfValidator.ValidateAddress(address);
                if (!verdict.IsSafe)
                    throw new HttpRequestException(verdict.Reason);
            }
        }

        Exception? lastFailure = null;
        foreach (var address in addresses)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // IPEndPoint, never DnsEndPoint/string: this cannot trigger another DNS lookup.
                return await _connect(new IPEndPoint(address, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                lastFailure = ex;
            }
        }
        throw new HttpRequestException("Could not connect to any validated destination address.", lastFailure);
    }

    private static async ValueTask<Stream> ConnectSocketAsync(IPEndPoint endpoint, CancellationToken token)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
