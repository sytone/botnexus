using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Extensions.WebTools.Tests;

/// <summary>
/// #3975: DNS policy must guard the numerical endpoint actually connected to, not just the URL.
/// Reflection intentionally lets this tests-first file compile before the production transport
/// exists. Missing production wiring is a named assertion failure, never a skip or a fake handler.
/// All HTTP bytes travel through SocketsHttpHandler and an owned loopback TCP fixture. The
/// connector records the requested endpoint, then maps it to that fixture; no DNS or socket call
/// in this file can contact the example destination, a real private service, or the Internet.
/// </summary>
public sealed class WebFetchDnsPolicyTests
{
    private const string PublicV4 = "203.0.113.7";
    private const string PublicV6 = "2001:db8::7";
    private const string Origin = "http://origin.policy.test:8087/first";
    private const string Body = "owned-dns-policy-fixture";
    private const string TransportName = "BotNexus.Extensions.WebTools.PublicNetworkHttpTransport";

    // Cover every blocked range in the shared table, including both ULA halves, site-local,
    // IPv4-compatible, mapped and 6to4 forms. Documentation IPs are deliberate allowed controls
    // under the existing table, NOT claims that they are Internet-routable.
    private static readonly string[] BlockedAddresses =
    [
        "127.0.0.1", "0.0.0.0", "0.2.3.4", "10.0.0.1", "172.16.0.1",
        "172.31.255.255", "192.168.0.1", "169.254.169.254", "100.64.0.1",
        "100.127.255.255", "::1", "::", "fe80::1", "febf::1", "fc00::1",
        "fdff::1", "fec0::1", "feff::1", "ff02::1", "::ffff:127.0.0.1",
        "::ffff:169.254.169.254", "::ffff:10.0.0.1", "::ffff:172.16.0.1",
        "::ffff:192.168.0.1", "::ffff:100.64.0.1", "::ffff:0.0.0.0",
        "::169.254.169.254", "::127.0.0.1", "::10.0.0.1",
        "2002:a9fe:a9fe::1", "2002:7f00:1::1", "2002:0a00:0001::1"
    ];

    public static IEnumerable<object[]> PrivateAnswers()
        => BlockedAddresses.Select(address => new object[] { address });

    public static IEnumerable<object[]> MixedAnswers()
    {
        foreach (var address in BlockedAddresses)
        {
            yield return [address, false];
            yield return [address, true];
        }
    }

    [Fact]
    public void DefaultConstructor_UsesProductionDestinationGuard()
    {
        using var tool = new WebFetchTool(new WebFetchConfig());
        AssertProductionPipeline(tool);
    }

    [Fact]
    public async Task ContributeAsync_DefaultFetch_UsesProductionDestinationGuard()
    {
        using var json = JsonDocument.Parse("{\"fetch\":{\"allowPrivateNetworks\":false}}");
        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("dns-policy-tests"),
            DisplayName = "DNS policy tests",
            ModelId = "test-model",
            ApiProvider = "github-copilot",
            ExtensionConfig = new Dictionary<string, JsonElement>
            {
                ["botnexus-web"] = json.RootElement.Clone()
            }
        };
        var context = new AgentToolContributionContext(
            descriptor,
            new AgentExecutionContext { SessionId = SessionId.Create() },
            Path.Combine(Path.GetTempPath(), "dns-policy-tests"),
            new AllowAllPathValidator(),
            null,
            (_, _) => Task.FromResult<string?>(null));

        var contribution = await new WebToolsContributor().ContributeAsync(context);
        using var tool = contribution.Tools.OfType<WebFetchTool>().ShouldHaveSingleItem();
        AssertProductionPipeline(tool);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContributeAsync_OwnedTransport_EnforcesDnsPolicy(bool permitted)
    {
        await using var fixture = new LoopbackFixture();
        var calls = 0;
        var contributor = new WebToolsContributor(config => new PublicNetworkHttpTransport(config,
            (_, _) => { calls++; return Answer(permitted ? PublicV4 : "127.0.0.1"); },
            fixture.ConnectAsync,
            new DeterministicProxy(new Uri("http://unused.proxy.test"), bypass: true)));
        using var json = JsonDocument.Parse("{\"fetch\":{\"allowPrivateNetworks\":false}}");
        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("dns-contributor-test"), DisplayName = "DNS contributor test",
            ModelId = "test-model", ApiProvider = "github-copilot",
            ExtensionConfig = new Dictionary<string, JsonElement> { ["botnexus-web"] = json.RootElement.Clone() }
        };
        var context = new AgentToolContributionContext(descriptor,
            new AgentExecutionContext { SessionId = SessionId.Create() },
            Path.GetTempPath(), new AllowAllPathValidator(), null, (_, _) => Task.FromResult<string?>(null));
        var contribution = await contributor.ContributeAsync(context);
        using var tool = contribution.Tools.OfType<WebFetchTool>().ShouldHaveSingleItem();
        AssertProductionPipeline(tool);
        var prepared = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["url"] = Origin });
        var result = await tool.ExecuteAsync("contributor-dns", prepared);
        calls.ShouldBe(1);
        if (permitted)
        {
            result.Content[0].Value.ShouldContain(Body);
            fixture.Endpoints.ShouldHaveSingleItem().Address.ShouldBe(IPAddress.Parse(PublicV4));
            fixture.Requests.ShouldHaveSingleItem();
        }
        else
        {
            AssertBlocked(result.Content[0].Value);
            fixture.Endpoints.ShouldBeEmpty();
            fixture.Requests.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task ExecuteAsync_PublicFirstConnectFails_FallsBackOnlyToValidatedAnswer()
    {
        await using var fixture = new LoopbackFixture();
        var attempts = new List<IPAddress>();
        var resolutions = 0;
        var config = new WebFetchConfig();
        using var tool = new WebFetchTool(config, new PublicNetworkHttpTransport(config,
            (_, _) => { resolutions++; return Answer(PublicV6, PublicV4); },
            (endpoint, token) =>
            {
                attempts.Add(endpoint.Address);
                return attempts.Count == 1
                    ? ValueTask.FromException<Stream>(new SocketException((int)SocketError.NetworkUnreachable))
                    : fixture.ConnectAsync(endpoint, token);
            }, new DeterministicProxy(new Uri("http://unused.proxy.test"), bypass: true)));
        var prepared = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["url"] = Origin });
        var result = await tool.ExecuteAsync("fallback-dns", prepared);
        result.Content[0].Value.ShouldContain(Body);
        resolutions.ShouldBe(1);
        attempts.ShouldBe(new[] { IPAddress.Parse(PublicV6), IPAddress.Parse(PublicV4) });
        fixture.Endpoints.ShouldHaveSingleItem().Address.ShouldBe(IPAddress.Parse(PublicV4));
    }

    [Theory]
    [MemberData(nameof(PrivateAnswers))]
    public async Task ExecuteAsync_DnsPrivateAnswer_BlocksBeforeConnector(string address)
    {
        await using var harness = new Harness((_, _) => Answer(address));
        var text = await harness.FetchAsync(Origin);

        AssertBlocked(text);
        harness.Resolutions.ShouldBe(["origin.policy.test"]);
        harness.Fixture.Endpoints.ShouldBeEmpty();
        harness.Fixture.Requests.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(MixedAnswers))]
    public async Task ExecuteAsync_MixedDnsAnswersEitherOrder_BlocksEntireSetBeforeConnector(
        string blockedAddress, bool publicFirst)
    {
        var answers = publicFirst ? new[] { PublicV4, blockedAddress } : new[] { blockedAddress, PublicV4 };
        await using var harness = new Harness((_, _) => Answer(answers));
        var text = await harness.FetchAsync(Origin);

        AssertBlocked(text);
        harness.Resolutions.ShouldBe(["origin.policy.test"]);
        harness.Fixture.Endpoints.ShouldBeEmpty();
        harness.Fixture.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_EmptyOrFailedDns_FailsClosedWithoutConnector(bool fails)
    {
        await using var harness = new Harness((_, _) => fails
            ? Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound))
            : Answer());
        var text = await harness.FetchAsync(Origin);

        AssertBlocked(text);
        harness.Resolutions.ShouldBe(["origin.policy.test"]);
        harness.Fixture.Endpoints.ShouldBeEmpty();
        harness.Fixture.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(PublicV4)]
    [InlineData(PublicV6)]
    [InlineData("::ffff:203.0.113.7")]
    [InlineData("2002:cb00:7107::1")]
    public async Task ExecuteAsync_PublicDnsAnswer_ConnectsToValidatedNumericalEndpoint(string address)
    {
        await using var harness = new Harness((_, _) => Answer(address));
        var text = await harness.FetchAsync(Origin);

        text.ShouldContain(Body);
        text.ShouldContain("\"status\":200");
        harness.Resolutions.ShouldBe(["origin.policy.test"]);
        var endpoint = harness.Fixture.Endpoints.ShouldHaveSingleItem();
        // Normalising mapped IPv4 before connect is permitted; selecting a different IP is not.
        Normalized(endpoint.Address).ShouldBe(Normalized(IPAddress.Parse(address)));
        endpoint.Port.ShouldBe(8087);
        var request = harness.Fixture.Requests.ShouldHaveSingleItem();
        request.ShouldStartWith("GET /first HTTP/1.1\r\n");
        request.ShouldContain("Host: origin.policy.test:8087\r\n");
        request.ShouldContain("User-Agent: dns-policy-fixture-agent\r\n");
    }

    [Fact]
    public async Task ExecuteAsync_PublicMultiAddressAnswer_SelectsOnlyFromValidatedSet()
    {
        await using var harness = new Harness((_, _) => Answer(PublicV6, PublicV4));
        var text = await harness.FetchAsync(Origin);

        text.ShouldContain(Body);
        harness.Resolutions.ShouldBe(["origin.policy.test"]);
        var endpoint = harness.Fixture.Endpoints.ShouldHaveSingleItem();
        new[] { IPAddress.Parse(PublicV4), IPAddress.Parse(PublicV6) }.ShouldContain(endpoint.Address);
        endpoint.Port.ShouldBe(8087);
        harness.Fixture.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ExecuteAsync_DnsChangesAfterClosedConnection_RevalidatesAndNeverConnectsToPrivateAnswer()
    {
        var calls = 0;
        await using var harness = new Harness((_, _) =>
            Interlocked.Increment(ref calls) == 1 ? Answer(PublicV4) : Answer("10.0.0.1"));

        (await harness.FetchAsync(Origin)).ShouldContain(Body);
        // Every fixture response explicitly closes its connection: the second fetch cannot use
        // a safe pooled socket to hide an unguarded new connection or stale DNS decision.
        var second = await harness.FetchAsync(Origin);

        AssertBlocked(second);
        calls.ShouldBe(2, "exactly one guarded resolution per new connection; no check/use DNS lookup");
        harness.Resolutions.ShouldBe(["origin.policy.test", "origin.policy.test"]);
        harness.Fixture.Endpoints.ShouldHaveSingleItem().Address.ShouldBe(IPAddress.Parse(PublicV4));
        harness.Fixture.Requests.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_RedirectToPrivateOrMixedDns_BlocksRedirectBeforeConnector(
        bool mixed, bool publicFirst)
    {
        var target = mixed
            ? (publicFirst ? new[] { PublicV4, "10.0.0.1" } : new[] { "10.0.0.1", PublicV4 })
            : new[] { "10.0.0.1" };
        await using var harness = new Harness((host, _) =>
            host == "origin.policy.test" ? Answer(PublicV4) : Answer(target));
        harness.Fixture.Response = _ => Redirect("http://redirect.policy.test:8091/secret");

        var text = await harness.FetchAsync(Origin);

        AssertBlocked(text);
        harness.Resolutions.ShouldBe(["origin.policy.test", "redirect.policy.test"]);
        // Only the already-approved origin was contacted: zero connector calls for the hop.
        var endpoint = harness.Fixture.Endpoints.ShouldHaveSingleItem();
        endpoint.Address.ShouldBe(IPAddress.Parse(PublicV4));
        endpoint.Port.ShouldBe(8087);
        harness.Fixture.Requests.ShouldHaveSingleItem().ShouldStartWith("GET /first ");
    }

    [Fact]
    public async Task ExecuteAsync_RedirectToPrivateLiteral_BlocksWithoutResolvingOrConnectingHop()
    {
        await using var harness = new Harness((_, _) => Answer(PublicV4));
        harness.Fixture.Response = _ => Redirect("http://169.254.169.254/latest/meta-data");

        AssertBlocked(await harness.FetchAsync(Origin));
        harness.Resolutions.ShouldBe(["origin.policy.test"]);
        harness.Fixture.Endpoints.ShouldHaveSingleItem().Address.ShouldBe(IPAddress.Parse(PublicV4));
        harness.Fixture.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ExecuteAsync_PublicRedirect_RevalidatesAndServesFinalResponse()
    {
        await using var harness = new Harness((host, _) =>
            host == "origin.policy.test" ? Answer(PublicV4) : Answer(PublicV6));
        harness.Fixture.Response = request => request.StartsWith("GET /first ", StringComparison.Ordinal)
            ? Redirect("http://redirect.policy.test:8091/final")
            : Ok(Body + "-final");

        var text = await harness.FetchAsync(Origin);

        text.ShouldContain(Body + "-final");
        text.ShouldContain("http://redirect.policy.test:8091/final");
        harness.Resolutions.ShouldBe(["origin.policy.test", "redirect.policy.test"]);
        harness.Fixture.Endpoints.Select(endpoint => endpoint.Address).ShouldBe(
            new[] { IPAddress.Parse(PublicV4), IPAddress.Parse(PublicV6) });
        harness.Fixture.Endpoints.Select(endpoint => endpoint.Port).ShouldBe(new[] { 8087, 8091 });
        harness.Fixture.Requests.Count.ShouldBe(2);
        harness.Fixture.Requests.Last().ShouldContain("Host: redirect.policy.test:8091\r\n");
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("fd00::1")]
    public async Task ExecuteAsync_AllowPrivateNetworksTrue_PermitsPrivateDns(string address)
    {
        await using var harness = new Harness((_, _) => Answer(address), allowPrivate: true);

        (await harness.FetchAsync(Origin)).ShouldContain(Body);
        harness.Resolutions.ShouldBe(["origin.policy.test"]);
        harness.Fixture.Endpoints.ShouldHaveSingleItem().Address.ShouldBe(IPAddress.Parse(address));
        harness.Fixture.Requests.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_ExplicitBlocklist_StillBlocksBeforeDnsAndConnector(bool allowPrivate)
    {
        await using var harness = new Harness((_, _) => Answer("10.0.0.1"), allowPrivate,
            blockedHosts: ["ORIGIN.POLICY.TEST"]);

        // Deliberately bypass PrepareArgumentsAsync: the final outbound boundary must enforce
        // explicit policy even for already-prepared, persisted or otherwise supplied arguments.
        AssertBlocked(await harness.ExecutePreparedAsync(Origin));
        harness.Resolutions.ShouldBeEmpty();
        harness.Fixture.Endpoints.ShouldBeEmpty();
        harness.Fixture.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_AllowPrivateNetworksTrue_RedirectStillHonorsBlocklist()
    {
        await using var harness = new Harness((_, _) => Answer("10.0.0.1"), allowPrivate: true,
            blockedHosts: ["REDIRECT.POLICY.TEST"]);
        harness.Fixture.Response = _ => Redirect("http://redirect.policy.test/secret");

        AssertBlocked(await harness.FetchAsync(Origin));
        harness.Resolutions.ShouldBe(["origin.policy.test"]);
        harness.Fixture.Endpoints.ShouldHaveSingleItem().Address.ShouldBe(IPAddress.Parse("10.0.0.1"));
        harness.Fixture.Requests.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("http://proxy.policy.test:3128", "http://origin.policy.test/first")]
    [InlineData("http://proxy.policy.test:3128", "https://origin.policy.test/first")]
    [InlineData("https://proxy.policy.test:8443", "http://origin.policy.test/first")]
    [InlineData("https://proxy.policy.test:8443", "https://origin.policy.test/first")]
    [InlineData("socks4://proxy.policy.test:1080", "http://origin.policy.test/first")]
    [InlineData("socks4a://proxy.policy.test:1080", "http://origin.policy.test/first")]
    [InlineData("socks5://proxy.policy.test:1080", "http://origin.policy.test/first")]
    [InlineData("socks5://proxy.policy.test:1080", "https://origin.policy.test/first")]
    public async Task Transport_ActiveProxyWithPrivateNetworksDisabled_FailsClosedWithoutDirectFallback(
        string proxyUri, string destination)
    {
        var proxy = new DeterministicProxy(new Uri(proxyUri), bypass: false);
        await using var harness = new Harness((_, _) => Answer(PublicV4), proxy: proxy);
        using var client = new HttpClient(harness.Transport, disposeHandler: false);

        var exception = await Should.ThrowAsync<HttpRequestException>(async () =>
        {
            using var response = await client.GetAsync(destination);
        });

        exception.Message.ShouldContain("SSRF");
        proxy.Decisions.ShouldContain(new Uri(destination));
        harness.Resolutions.ShouldBeEmpty();
        harness.Fixture.Endpoints.ShouldBeEmpty();
        harness.Fixture.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ActiveProxy_ReportsSsrfRatherThanFallingBack()
    {
        await using var harness = new Harness((_, _) => Answer(PublicV4),
            proxy: new DeterministicProxy(new Uri("http://proxy.policy.test:3128"), bypass: false));

        AssertBlocked(await harness.FetchAsync(Origin));
        harness.Resolutions.ShouldBeEmpty();
        harness.Fixture.Endpoints.ShouldBeEmpty();
        harness.Fixture.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_BypassedProxy_RetainsDirectDnsGuard(bool mixed)
    {
        // IWebProxy.IsBypassed is the same per-destination decision used by NO_PROXY, without
        // mutating process-global environment variables or relying on cached default proxies.
        var proxy = new DeterministicProxy(new Uri("http://proxy.policy.test:3128"), bypass: true);
        await using var harness = new Harness((_, _) => mixed
            ? Answer(PublicV4, "10.0.0.1") : Answer("10.0.0.1"), proxy: proxy);

        AssertBlocked(await harness.FetchAsync(Origin));
        proxy.Decisions.ShouldContain(new Uri(Origin));
        harness.Resolutions.ShouldBe(["origin.policy.test"]);
        harness.Fixture.Endpoints.ShouldBeEmpty();
        harness.Fixture.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_BypassedProxyWithPublicDns_UsesValidatedDirectConnection()
    {
        var proxy = new DeterministicProxy(new Uri("http://proxy.policy.test:3128"), bypass: true);
        await using var harness = new Harness((_, _) => Answer(PublicV4), proxy: proxy);

        (await harness.FetchAsync(Origin)).ShouldContain(Body);
        proxy.Decisions.ShouldContain(new Uri(Origin));
        harness.Resolutions.ShouldBe(["origin.policy.test"]);
        harness.Fixture.Endpoints.ShouldHaveSingleItem().Address.ShouldBe(IPAddress.Parse(PublicV4));
        harness.Fixture.Requests.ShouldHaveSingleItem().ShouldStartWith("GET /first HTTP/1.1\r\n");
    }

    [Fact]
    public async Task ExecuteAsync_AllowPrivateNetworksTrue_PermitsExplicitProxyWithoutDirectFallback()
    {
        var proxy = new DeterministicProxy(new Uri("http://proxy.policy.test:3128"), bypass: false);
        await using var harness = new Harness((host, _) =>
        {
            host.ShouldBe("proxy.policy.test", "the proxy, not the origin, owns destination resolution");
            return Answer("192.0.2.250");
        }, allowPrivate: true, proxy: proxy);

        (await harness.FetchAsync(Origin)).ShouldContain(Body);
        proxy.Decisions.ShouldContain(new Uri(Origin));
        harness.Resolutions.ShouldBe(["proxy.policy.test"]);
        var endpoint = harness.Fixture.Endpoints.ShouldHaveSingleItem();
        endpoint.Address.ShouldBe(IPAddress.Parse("192.0.2.250"));
        endpoint.Port.ShouldBe(3128);
        harness.Fixture.Requests.ShouldHaveSingleItem()
            .ShouldStartWith("GET http://origin.policy.test:8087/first HTTP/1.1\r\n");
    }

    [Fact]
    public async Task ExecuteAsync_CancellationDuringDns_CancelsResolverWithoutConnector()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new Harness(async (_, token) =>
        {
            token.CanBeCanceled.ShouldBeTrue();
            using var registration = token.Register(() => cancelled.TrySetResult(true));
            entered.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, token);
            return [];
        });
        using var cancellation = new CancellationTokenSource();
        var pending = harness.FetchAsync(Origin, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        cancellation.Cancel();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var text = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        text.ShouldContain("cancel", Case.Insensitive);
        text.ShouldNotContain(Body);
        harness.Resolutions.ShouldBe(["origin.policy.test"]);
        harness.Fixture.Endpoints.ShouldBeEmpty();
        harness.Fixture.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Transport_PreCancelledRequest_DoesNotResolveOrConnect()
    {
        await using var harness = new Harness((_, _) => Answer(PublicV4));
        using var client = new HttpClient(harness.Transport, disposeHandler: false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            using var response = await client.GetAsync(Origin, cancellation.Token);
        });

        harness.Resolutions.ShouldBeEmpty();
        harness.Fixture.Endpoints.ShouldBeEmpty();
        harness.Fixture.Requests.ShouldBeEmpty();
    }

    private static Task<IPAddress[]> Answer(params string[] addresses)
        => Task.FromResult(addresses.Select(IPAddress.Parse).ToArray());

    private static IPAddress Normalized(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static void AssertBlocked(string text)
    {
        text.ShouldContain("SSRF");
        text.ShouldNotContain(Body);
        text.ShouldNotContain("\"status\":200");
    }

    private static Type RequireTransportType()
    {
        var type = typeof(WebFetchTool).Assembly.GetType(TransportName);
        type.ShouldNotBeNull("#3975 requires the production PublicNetworkHttpTransport destination guard");
        return type ?? throw new InvalidOperationException("Missing #3975 production destination guard.");
    }

    private static void AssertProductionPipeline(WebFetchTool tool)
    {
        var clientField = typeof(WebFetchTool).GetField("_httpClient", BindingFlags.NonPublic | BindingFlags.Instance);
        clientField.ShouldNotBeNull();
        var client = clientField?.GetValue(tool).ShouldBeOfType<HttpClient>()
            ?? throw new InvalidOperationException("WebFetchTool must own its production HttpClient.");
        var handlerField = typeof(HttpMessageInvoker).GetField("_handler", BindingFlags.NonPublic | BindingFlags.Instance);
        handlerField.ShouldNotBeNull("inspect the real HttpClient handler, not an unrelated guard field");
        var handler = handlerField?.GetValue(client).ShouldBeAssignableTo<HttpMessageHandler>()
            ?? throw new InvalidOperationException("HttpClient handler unavailable.");
        handler.GetType().FullName.ShouldBe(TransportName,
            "both default construction and contributor wiring must use the destination guard");
        var transport = handler.ShouldBeAssignableTo<DelegatingHandler>();
        transport.InnerHandler.ShouldBeOfType<SocketsHttpHandler>().AllowAutoRedirect.ShouldBeFalse();
    }

    private static string Ok(string body)
        => $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";

    private static string Redirect(string location)
        => $"HTTP/1.1 302 Found\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

    private sealed class Harness : IAsyncDisposable
    {
        public ConcurrentQueue<string> Resolutions { get; } = new();
        public LoopbackFixture Fixture { get; } = new();
        public DelegatingHandler Transport { get; }
        private WebFetchTool Tool { get; }

        public Harness(
            Func<string, CancellationToken, Task<IPAddress[]>> resolver,
            bool allowPrivate = false,
            IReadOnlyList<string>? blockedHosts = null,
            IWebProxy? proxy = null)
        {
            // Bind before opening any listener, so RED is a precise missing-guard assertion.
            var type = RequireTransportType();
            var config = new WebFetchConfig
            {
                AllowPrivateNetworks = allowPrivate,
                AdditionalBlockedHosts = blockedHosts ?? [],
                TimeoutSeconds = 10,
                UserAgent = "dns-policy-fixture-agent"
            };
            Func<string, CancellationToken, Task<IPAddress[]>> resolve = (host, token) =>
            {
                Resolutions.Enqueue(host);
                return resolver(host, token);
            };
            Func<IPEndPoint, CancellationToken, ValueTask<Stream>> connect = Fixture.ConnectAsync;
            var constructor = type.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(WebFetchConfig), resolve.GetType(), connect.GetType(), typeof(IWebProxy)],
                modifiers: null);
            constructor.ShouldNotBeNull("#3975 transport constructor must expose deterministic DNS/connect/proxy seams");
            Transport = constructor?.Invoke([config, resolve, connect,
                    proxy ?? new DeterministicProxy(new Uri("http://unused.proxy.test:3128"), bypass: true)])
                .ShouldBeAssignableTo<DelegatingHandler>()
                ?? throw new InvalidOperationException("Missing production transport constructor.");
            var sockets = Transport.InnerHandler.ShouldBeOfType<SocketsHttpHandler>();
            sockets.ConnectCallback.ShouldNotBeNull("the numerical connector must belong to the real socket pipeline");
            sockets.AllowAutoRedirect.ShouldBeFalse("WebFetchTool must validate each redirect hop");
            var toolConstructor = typeof(WebFetchTool).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(WebFetchConfig), type, typeof(ISecretRedactor)],
                modifiers: null);
            toolConstructor.ShouldNotBeNull("#3975 WebFetchTool must own an HttpClient over the production transport");
            Tool = toolConstructor?.Invoke([config, Transport, null]).ShouldBeOfType<WebFetchTool>()
                ?? throw new InvalidOperationException("Missing transport-aware WebFetchTool constructor.");
            AssertProductionPipeline(Tool);
        }

        public async Task<string> FetchAsync(string url, CancellationToken token = default)
        {
            var prepared = await Tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["url"] = url }, token);
            var result = await Tool.ExecuteAsync("dns-policy-call", prepared, token);
            return result.Content[0].Value;
        }

        public async Task<string> ExecutePreparedAsync(string url)
        {
            var arguments = new Dictionary<string, object?>
            {
                ["url"] = url, ["raw"] = true, ["max_length"] = 5000, ["start_index"] = 0
            };
            var result = await Tool.ExecuteAsync("dns-policy-prepared-call", arguments);
            return result.Content[0].Value;
        }

        public async ValueTask DisposeAsync()
        {
            Tool.Dispose();
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
        public Func<string, string> Response { get; set; } = _ => Ok(Body);

        public async ValueTask<Stream> ConnectAsync(IPEndPoint requested, CancellationToken token)
        {
            Endpoints.Enqueue(requested);
            token.ThrowIfCancellationRequested();
            // This is the ONLY socket destination used by the harness, irrespective of the
            // requested address. A broken guard can fail assertions but cannot contact IMDS.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(); // Fail immediately if the owned harness cannot start; no fallback.
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
                while (true)
                {
                    var line = await reader.ReadLineAsync(_stop.Token);
                    if (line is null)
                        throw new IOException("Fixture connection closed before complete HTTP headers.");
                    request.Append(line).Append("\r\n");
                    if (line.Length == 0)
                        break;
                    if (request.Length > 32_768)
                        throw new IOException("Fixture request headers exceeded diagnostic limit.");
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
                // Only teardown cancellation is expected; HTTP/protocol/fixture failures escape.
            }
            finally
            {
                _stop.Dispose();
            }
        }
    }

    private sealed class AllowAllPathValidator : IPathValidator
    {
        public bool CanRead(string absolutePath) => true;
        public bool CanWrite(string absolutePath) => true;
        public string? ValidateAndResolve(string rawPath, FileAccessMode mode) => rawPath;
    }
}
