using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using BotNexus.Gateway.Webhooks;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Gateway.Tests.Controllers;

/// <summary>
/// #4269: callback URL admission and the numerical endpoint used by the socket must be one
/// production-owned operation. Reflection intentionally lets these RED tests compile before the
/// transport exists. A missing transport or seam is a named assertion failure, never a skip.
/// Except for deterministic DNS, numerical-connect, and proxy seams, requests pass through the
/// real SocketsHttpHandler pipeline. The connector maps every requested endpoint to an owned
/// loopback server, so a broken guard cannot contact the example address, a private service, or
/// the Internet.
/// </summary>
public sealed class WebhookCallbackTransportSecurityTests
{
    private const string TransportName = "BotNexus.Gateway.Webhooks.WebhookCallbackHttpTransport";
    private const string Origin = "http://callback.policy.test:8087/result";
    private const string PublicAddress = "203.0.113.7";
    private const string ResponseBody = "callback-policy-fixture";

    public static IEnumerable<object[]> ForbiddenResolvedAddresses()
    {
        yield return ["10.0.0.1"];
        yield return ["100.64.0.1"];
        yield return ["fc00::1"];
        yield return ["::ffff:169.254.169.254"];
        yield return ["2002:a9fe:a9fe::1"];
    }

    [Theory]
    [MemberData(nameof(ForbiddenResolvedAddresses))]
    public async Task SendAsync_ForbiddenResolvedAddress_IsRejectedBeforeNumericalConnect(string address)
    {
        await using var harness = new Harness((_, _) => Answer(address));

        var exception = await Should.ThrowAsync<HttpRequestException>(() => harness.PostAsync());

        exception.Message.ShouldContain("SSRF", Case.Insensitive);
        harness.Resolutions.ShouldBe(["callback.policy.test"]);
        harness.Fixture.Endpoints.ShouldBeEmpty();
        harness.Fixture.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task NamedWebhookCallbackClient_UsesGuardedProductionTransport()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBotNexusWebhooks(Path.Combine(Path.GetTempPath(), $"webhook-callback-{Guid.NewGuid():N}.db"));
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        using var client = factory.CreateClient("WebhookCallback");

        var exception = await Should.ThrowAsync<HttpRequestException>(() =>
            client.PostAsync("http://127.0.0.1/callback", new StringContent("{}")));

        exception.Message.ShouldContain("SSRF", Case.Insensitive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendAsync_MixedDnsAnswersEitherOrder_RejectsEntireSetBeforeNumericalConnect(
        bool publicFirst)
    {
        var answers = publicFirst
            ? new[] { PublicAddress, "169.254.169.254" }
            : new[] { "169.254.169.254", PublicAddress };
        await using var harness = new Harness((_, _) => Answer(answers));

        var exception = await Should.ThrowAsync<HttpRequestException>(() => harness.PostAsync());

        exception.Message.ShouldContain("SSRF", Case.Insensitive);
        harness.Resolutions.ShouldBe(["callback.policy.test"]);
        harness.Fixture.Endpoints.ShouldBeEmpty();
        harness.Fixture.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(PublicAddress)]
    [InlineData("2001:db8::7")]
    [InlineData("::ffff:203.0.113.7")]
    [InlineData("2002:cb00:7107::1")]
    public async Task SendAsync_PublicDnsAnswer_BindsSocketToValidatedNumericalEndpoint(string address)
    {
        await using var harness = new Harness((_, _) => Answer(address));

        using var response = await harness.PostAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        harness.Resolutions.ShouldBe(["callback.policy.test"]);
        var endpoint = harness.Fixture.Endpoints.ShouldHaveSingleItem();
        endpoint.Address.ShouldBe(IPAddress.Parse(address));
        endpoint.Port.ShouldBe(8087);
        var request = harness.Fixture.Requests.ShouldHaveSingleItem();
        request.ShouldStartWith("POST /result HTTP/1.1\r\n");
        request.ShouldContain("Host: callback.policy.test:8087\r\n");
    }

    [Fact]
    public async Task SendAsync_DnsChangesOnFreshConnection_RevalidatesAndNeverConnectsToPrivateAnswer()
    {
        var calls = 0;
        await using var harness = new Harness((_, _) =>
            Interlocked.Increment(ref calls) == 1 ? Answer(PublicAddress) : Answer("10.0.0.1"));

        using (var first = await harness.PostAsync())
            first.StatusCode.ShouldBe(HttpStatusCode.OK);

        var exception = await Should.ThrowAsync<HttpRequestException>(() => harness.PostAsync());

        exception.Message.ShouldContain("SSRF", Case.Insensitive);
        calls.ShouldBe(2, "the fixture closes each response, requiring guarded DNS for the fresh connection");
        harness.Resolutions.ShouldBe(["callback.policy.test", "callback.policy.test"]);
        harness.Fixture.Endpoints.ShouldHaveSingleItem().Address.ShouldBe(IPAddress.Parse(PublicAddress));
        harness.Fixture.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task SendAsync_RedirectResponse_IsNotFollowedAutomatically()
    {
        await using var harness = new Harness((_, _) => Answer(PublicAddress));
        harness.Fixture.Response = _ => Redirect("http://169.254.169.254/latest/meta-data");

        using var response = await harness.PostAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location.ShouldBe(new Uri("http://169.254.169.254/latest/meta-data"));
        harness.Resolutions.ShouldBe(["callback.policy.test"]);
        harness.Fixture.Endpoints.ShouldHaveSingleItem().Address.ShouldBe(IPAddress.Parse(PublicAddress));
        harness.Fixture.Requests.ShouldHaveSingleItem();
        GetSocketsHandler(harness.Transport).AllowAutoRedirect.ShouldBeFalse(
            "callback redirects must be refused unless each hop is explicitly revalidated");
    }

    [Fact]
    public async Task SendAsync_ActiveProxy_IsRejectedWithoutDnsOrDirectFallback()
    {
        var proxy = new DeterministicProxy(new Uri("http://proxy.policy.test:3128"), bypass: false);
        await using var harness = new Harness((_, _) => Answer(PublicAddress), proxy);

        var exception = await Should.ThrowAsync<HttpRequestException>(() => harness.PostAsync());

        exception.Message.ShouldContain("SSRF", Case.Insensitive);
        proxy.Decisions.ShouldContain(new Uri(Origin));
        harness.Resolutions.ShouldBeEmpty();
        harness.Fixture.Endpoints.ShouldBeEmpty();
        harness.Fixture.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task SendAsync_BypassedProxy_RetainsGuardedDirectNumericalConnection()
    {
        var proxy = new DeterministicProxy(new Uri("http://proxy.policy.test:3128"), bypass: true);
        await using var harness = new Harness((_, _) => Answer(PublicAddress), proxy);

        using var response = await harness.PostAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        proxy.Decisions.ShouldContain(new Uri(Origin));
        harness.Resolutions.ShouldBe(["callback.policy.test"]);
        harness.Fixture.Endpoints.ShouldHaveSingleItem().Address.ShouldBe(IPAddress.Parse(PublicAddress));
        harness.Fixture.Requests.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("http://127.0.0.1/callback")]
    [InlineData("http://100.64.0.1/callback")]
    [InlineData("http://[fc00::1]/callback")]
    [InlineData("http://[::ffff:169.254.169.254]/callback")]
    [InlineData("http://[2002:a9fe:a9fe::1]/callback")]
    [InlineData("https://203.0.113.7/callback")]
    [InlineData("https://[2001:db8::7]/callback")]
    public void WebhookCallbackValidator_MatchesSharedSsrfValidatorForLiteralDestinations(string url)
    {
        var uri = new Uri(url);
        var shared = BotNexus.Gateway.Abstractions.Security.SsrfValidator.Validate(uri);
        var callback = WebhookCallbackValidator.IsCallbackUrlSafe(url);

        callback.IsSafe.ShouldBe(shared.IsSafe,
            $"webhook callback policy must reuse the shared SsrfValidator classification for {uri.Host}");
    }

    private static Task<IPAddress[]> Answer(params string[] addresses)
        => Task.FromResult(addresses.Select(IPAddress.Parse).ToArray());

    private static string Ok()
        => $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {Encoding.UTF8.GetByteCount(ResponseBody)}\r\nConnection: close\r\n\r\n{ResponseBody}";

    private static string Redirect(string location)
        => $"HTTP/1.1 302 Found\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

    private static Type RequireTransportType()
    {
        var type = typeof(WebhookCallbackValidator).Assembly.GetType(TransportName);
        type.ShouldNotBeNull(
            "#4269 requires a webhook-owned production transport that binds callbacks to validated addresses");
        return type ?? throw new InvalidOperationException("Missing #4269 webhook callback transport.");
    }

    private static SocketsHttpHandler GetSocketsHandler(DelegatingHandler transport)
    {
        var sockets = transport.InnerHandler.ShouldBeOfType<SocketsHttpHandler>(
            "callback bytes must flow through the production SocketsHttpHandler pipeline");
        sockets.ConnectCallback.ShouldNotBeNull(
            "the validated numerical connector must be installed on the real socket pipeline");
        return sockets;
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly HttpClient _client;

        public ConcurrentQueue<string> Resolutions { get; } = new();
        public LoopbackFixture Fixture { get; } = new();
        public DelegatingHandler Transport { get; }

        public Harness(
            Func<string, CancellationToken, Task<IPAddress[]>> resolver,
            IWebProxy? proxy = null)
        {
            // Resolve the production contract before any listener is opened. Missing production
            // work therefore REDs as a precise assertion rather than an infrastructure failure.
            var type = RequireTransportType();
            Func<string, CancellationToken, Task<IPAddress[]>> resolve = (host, token) =>
            {
                Resolutions.Enqueue(host);
                return resolver(host, token);
            };
            Func<IPEndPoint, CancellationToken, ValueTask<Stream>> connect = Fixture.ConnectAsync;
            var constructor = type.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: [resolve.GetType(), connect.GetType(), typeof(IWebProxy)],
                modifiers: null);
            constructor.ShouldNotBeNull(
                "#4269 transport must expose deterministic DNS, numerical connector, and proxy seams");
            Transport = constructor?.Invoke([
                    resolve,
                    connect,
                    proxy ?? new DeterministicProxy(new Uri("http://unused.proxy.test:3128"), bypass: true)
                ]).ShouldBeAssignableTo<DelegatingHandler>()
                ?? throw new InvalidOperationException("Missing #4269 transport seam constructor.");
            var sockets = GetSocketsHandler(Transport);
            sockets.AllowAutoRedirect.ShouldBeFalse(
                "callback redirects must not escape destination validation");
            _client = new HttpClient(Transport, disposeHandler: false);
        }

        public Task<HttpResponseMessage> PostAsync(CancellationToken token = default)
            => _client.PostAsync(Origin, new StringContent("{}", Encoding.UTF8, "application/json"), token);

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            Transport.Dispose();
            await Fixture.DisposeAsync();
        }
    }

    private sealed class DeterministicProxy(Uri address, bool bypass) : IWebProxy
    {
        public ConcurrentQueue<Uri> Decisions { get; } = new();
        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination)
        {
            Decisions.Enqueue(destination);
            return bypass ? destination : address;
        }

        public bool IsBypassed(Uri host)
        {
            Decisions.Enqueue(host);
            return bypass;
        }
    }

    private sealed class LoopbackFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentQueue<Task> _servers = new();

        public ConcurrentQueue<IPEndPoint> Endpoints { get; } = new();
        public ConcurrentQueue<string> Requests { get; } = new();
        public Func<string, string> Response { get; set; } = _ => Ok();

        public async ValueTask<Stream> ConnectAsync(IPEndPoint requested, CancellationToken token)
        {
            Endpoints.Enqueue(requested);
            token.ThrowIfCancellationRequested();
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, token);
                var server = await listener.AcceptTcpClientAsync(token);
                _servers.Enqueue(ServeAsync(server));
                return new NetworkStream(client, ownsSocket: true);
            }
            catch
            {
                client.Dispose();
                throw;
            }
            finally
            {
                listener.Stop();
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var request = new StringBuilder();
                var contentLength = 0;
                while (true)
                {
                    var line = await reader.ReadLineAsync(_stop.Token);
                    if (line is null)
                        throw new IOException("Fixture connection closed before complete HTTP headers.");
                    request.Append(line).Append("\r\n");
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        contentLength = int.Parse(line["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    if (line.Length == 0)
                        break;
                    if (request.Length > 32_768)
                        throw new IOException("Fixture request headers exceeded diagnostic limit.");
                }

                if (contentLength > 0)
                {
                    var body = new char[contentLength];
                    var read = 0;
                    while (read < body.Length)
                    {
                        var count = await reader.ReadAsync(body.AsMemory(read), _stop.Token);
                        if (count == 0)
                            throw new IOException("Fixture connection closed before complete HTTP body.");
                        read += count;
                    }
                }

                var received = request.ToString();
                Requests.Enqueue(received);
                var response = Encoding.UTF8.GetBytes(Response(received));
                await stream.WriteAsync(response, _stop.Token);
                await stream.FlushAsync(_stop.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            try
            {
                await Task.WhenAll(_servers).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                // Only teardown cancellation is expected; protocol and fixture failures escape.
            }
            finally
            {
                _stop.Dispose();
            }
        }
    }
}
