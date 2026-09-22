using System.Net;
using System.Net.Sockets;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Gateway.Webhooks;

/// <summary>
/// HTTP transport that binds each webhook callback connection to an address that passed the
/// shared SSRF policy immediately before the numerical socket connection is opened.
/// </summary>
public sealed class WebhookCallbackHttpTransport : DelegatingHandler
{
    private readonly IWebProxy _proxy;

    /// <summary>
    /// Creates the production callback transport using system DNS, sockets, and proxy policy.
    /// </summary>
    public WebhookCallbackHttpTransport()
        : this(ResolveAsync, ConnectAsync, HttpClient.DefaultProxy)
    {
    }

    /// <summary>
    /// Creates a callback transport with deterministic network seams for security verification.
    /// </summary>
    internal WebhookCallbackHttpTransport(
        Func<string, CancellationToken, Task<IPAddress[]>> resolver,
        Func<IPEndPoint, CancellationToken, ValueTask<Stream>> connector,
        IWebProxy proxy)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(proxy);

        _proxy = proxy;
        InnerHandler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var endpoint = context.DnsEndPoint;
                IPAddress[] addresses;
                try
                {
                    addresses = IPAddress.TryParse(endpoint.Host, out var literal)
                        ? [literal]
                        : await resolver(endpoint.Host, cancellationToken).ConfigureAwait(false);
                }
                catch (SocketException ex)
                {
                    throw new HttpRequestException(
                        "Webhook callback blocked because DNS resolution failed (SSRF prevention).", ex);
                }

                if (addresses.Length == 0)
                {
                    throw new HttpRequestException(
                        $"Webhook callback blocked by SSRF protection because '{endpoint.Host}' resolved to no addresses.");
                }

                foreach (var address in addresses)
                {
                    var validation = SsrfValidator.ValidateAddress(address);
                    if (!validation.IsSafe)
                    {
                        throw new HttpRequestException(
                            $"Webhook callback blocked by SSRF protection: {validation.Reason}");
                    }
                }

                Exception? lastFailure = null;
                foreach (var address in addresses)
                {
                    try
                    {
                        return await connector(
                            new IPEndPoint(address, endpoint.Port),
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (SocketException ex)
                    {
                        lastFailure = ex;
                    }
                }

                throw new HttpRequestException(
                    "Webhook callback could not connect to any validated destination address.",
                    lastFailure);
            }
        };
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var destination = request.RequestUri
            ?? throw new HttpRequestException("Webhook callback blocked by SSRF protection because its destination is missing.");
        var validation = SsrfValidator.Validate(destination, additionalBlockedHosts: null);
        if (!validation.IsSafe)
        {
            throw new HttpRequestException(
                $"Webhook callback blocked by SSRF protection: {validation.Reason}");
        }

        if (!_proxy.IsBypassed(destination))
        {
            throw new HttpRequestException(
                "Webhook callback blocked by SSRF protection because active proxy routes are not permitted.");
        }

        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        return base.SendAsync(request, cancellationToken);
    }

    private static Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        => Dns.GetHostAddressesAsync(host, cancellationToken);

    private static async ValueTask<Stream> ConnectAsync(
        IPEndPoint endpoint,
        CancellationToken cancellationToken)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
