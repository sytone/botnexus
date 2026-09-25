using System.Net;
using System.Net.Sockets;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Gateway.A2A;

/// <summary>Owns the A2A socket boundary so redirects and DNS rebinding cannot bypass destination policy.</summary>
internal sealed class A2AHttpTransport : DelegatingHandler
{
    private static readonly HttpRequestOptionsKey<CancellationToken> RequestCancellation = new("A2A.RequestCancellation");

    internal A2AHttpTransport()
    {
        InnerHandler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = ConnectAsync
        };
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new HttpRequestException("The A2A request URI is missing.");
        var lexical = SsrfValidator.Validate(uri, additionalBlockedHosts: null);
        if (!lexical.IsSafe)
            throw new HttpRequestException(lexical.Reason);

        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        request.Options.Set(RequestCancellation, cancellationToken);
        return base.SendAsync(request, cancellationToken);
    }

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        context.InitialRequestMessage.Options.TryGetValue(RequestCancellation, out var requestCancellation);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, requestCancellation);
        var token = linked.Token;
        var host = context.DnsEndPoint.Host;
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            throw new HttpRequestException("A2A DNS resolution failed.", ex);
        }

        if (addresses.Length == 0)
            throw new HttpRequestException("A2A DNS resolution returned no addresses.");

        foreach (var address in addresses)
        {
            var verdict = SsrfValidator.ValidateAddress(address);
            if (!verdict.IsSafe)
                throw new HttpRequestException(verdict.Reason);
        }

        Exception? lastFailure = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                lastFailure = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException("Could not connect to an SSRF-validated A2A address.", lastFailure);
    }
}
