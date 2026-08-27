using BotNexus.Cli.Services;
using Shouldly;

namespace BotNexus.Cli.Tests;

/// <summary>
/// The CLI used to probe <c>http://localhost:{port}</c> unconditionally, while the gateway rebinds
/// to <c>gateway.listenUrl</c> when one is configured. On any host that sets a LAN listen URL the
/// readiness probe could therefore never succeed, and <c>gateway start</c> reported failure - after
/// waiting out its full timeout - for a gateway that had started and was serving requests.
/// </summary>
public sealed class GatewayProbeUrlResolverTests
{
    [Fact]
    public void Resolve_NoConfiguredListenUrl_UsesLoopbackOnTheRequestedPort()
        => GatewayProbeUrlResolver.Resolve(null, 5005).ShouldBe("http://localhost:5005");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankListenUrl_FallsBackToLoopback(string listenUrl)
        => GatewayProbeUrlResolver.Resolve(listenUrl, 5005).ShouldBe("http://localhost:5005");

    // The regression itself: a specific bind address must be probed where it binds.
    [Fact]
    public void Resolve_SpecificAddress_ProbesThatAddress()
        => GatewayProbeUrlResolver.Resolve("http://192.0.2.10:5005", 5005)
            .ShouldBe("http://192.0.2.10:5005");

    // The listen URL carries the effective port, which need not match --port.
    [Fact]
    public void Resolve_PortDiffersFromTheRequestedPort_TheListenUrlWins()
        => GatewayProbeUrlResolver.Resolve("http://192.0.2.10:7000", 5005)
            .ShouldBe("http://192.0.2.10:7000");

    [Fact]
    public void Resolve_HttpsListenUrl_KeepsTheScheme()
        => GatewayProbeUrlResolver.Resolve("https://gateway.internal:5005", 5005)
            .ShouldBe("https://gateway.internal:5005");

    // Kestrel wildcards are not connectable as written - "http://+:5005" is not even a legal Uri -
    // so they resolve to loopback, which a wildcard bind always includes.
    [Theory]
    [InlineData("http://+:5005")]
    [InlineData("http://*:5005")]
    [InlineData("http://0.0.0.0:5005")]
    [InlineData("http://[::]:5005")]
    public void Resolve_WildcardBind_ProbesLoopbackOnTheBoundPort(string listenUrl)
        => GatewayProbeUrlResolver.Resolve(listenUrl, 9999).ShouldBe("http://localhost:5005");

    [Fact]
    public void Resolve_Ipv6Literal_KeepsTheBracketsAndPort()
        => GatewayProbeUrlResolver.Resolve("http://[2001:db8::1]:5005", 5005)
            .ShouldBe("http://[2001:db8::1]:5005");

    [Fact]
    public void Resolve_SemicolonList_TakesTheFirstEntry()
        => GatewayProbeUrlResolver.Resolve("http://192.0.2.10:5005;http://localhost:5006", 5005)
            .ShouldBe("http://192.0.2.10:5005");

    [Fact]
    public void Resolve_TrailingSlashAndPath_AreDropped()
        => GatewayProbeUrlResolver.Resolve("http://192.0.2.10:5005/", 5005)
            .ShouldBe("http://192.0.2.10:5005");

    [Fact]
    public void Resolve_NoPort_OmitsIt()
        => GatewayProbeUrlResolver.Resolve("http://gateway.internal", 5005)
            .ShouldBe("http://gateway.internal");

    // A value that is not shaped like a URL must not produce a nonsense probe target.
    [Fact]
    public void Resolve_Malformed_FallsBackToLoopback()
        => GatewayProbeUrlResolver.Resolve("not-a-url", 5005).ShouldBe("http://localhost:5005");

    // #3598: the resolver is a general-purpose string-to-string transformation, so the #2925 fence
    // requires it to be reachable as `this string`. Calling it in extension form is what pins that
    // shape - a future revert to a plain static would fail to compile here rather than only
    // reddening the architecture suite on main.
    [Fact]
    public void Resolve_IsCallableAsAStringExtension()
        => "http://192.0.2.10:7000".Resolve(5005).ShouldBe("http://192.0.2.10:7000");
}
