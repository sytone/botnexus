using BotNexus.Gateway.Configuration;
using Shouldly;

namespace BotNexus.Gateway.Api.Tests;

/// <summary>
/// Pins the URL the gateway startup banner announces (issue #2929 AC4).
/// </summary>
/// <remarks>
/// The defect these tests exist to redden is a *silent* one: the banner's final fallback said
/// <c>http://localhost:5000</c> while every other default resolved to 5005, and because it only fires
/// when nothing is configured, ordinary use never reveals it. The banner is the first thing a fresh
/// install reads, so an announced port the host is not on is indistinguishable from a dead gateway.
/// Each case below names the source that should win, so a future precedence change reddens by name
/// rather than by a diff in a string.
/// </remarks>
public sealed class GatewayStartupUrlResolverTests
{
    /// <summary>
    /// Happy path, and the strongest clause: what the banner reports is what the host actually bound.
    /// </summary>
    [Fact]
    public void Resolve_BoundAddressPresent_ReportsTheAddressTheHostActuallyBound()
    {
        var resolved = GatewayStartupUrlResolver.Resolve(
            ["http://localhost:8123"],
            configuredListenUrl: "http://localhost:9999",
            aspNetCoreUrls: "http://localhost:7777");

        resolved.ShouldBe(
            "http://localhost:8123",
            "The banner must report the bound address over any requested one. A configured or ambient "
            + "URL says what was asked for; only app.Urls says what Kestrel did.");
    }

    /// <summary>
    /// The first non-blank bound address wins; a blank entry must not be announced.
    /// </summary>
    [Fact]
    public void Resolve_BoundAddressesLeadWithBlank_SkipsTheBlankEntry()
    {
        var resolved = GatewayStartupUrlResolver.Resolve(
            ["", "   ", "http://localhost:8123"],
            configuredListenUrl: null,
            aspNetCoreUrls: null);

        resolved.ShouldBe("http://localhost:8123");
    }

    [Fact]
    public void Resolve_NoBoundAddress_FallsBackToTheConfiguredListenUrl()
    {
        var resolved = GatewayStartupUrlResolver.Resolve(
            [],
            configuredListenUrl: "http://localhost:9999",
            aspNetCoreUrls: "http://localhost:7777");

        resolved.ShouldBe("http://localhost:9999");
    }

    [Fact]
    public void Resolve_NoBoundOrConfiguredUrl_FallsBackToTheFirstAspNetCoreUrl()
    {
        var resolved = GatewayStartupUrlResolver.Resolve(
            null,
            configuredListenUrl: null,
            aspNetCoreUrls: "http://localhost:7777;https://localhost:7778");

        resolved.ShouldBe("http://localhost:7777");
    }

    /// <summary>
    /// The sad path this issue was filed for: nothing configured anywhere. The banner must announce the
    /// shared default, never a second hardcoded literal.
    /// </summary>
    [Fact]
    public void Resolve_NothingConfigured_AnnouncesTheSharedDefaultNotAStaleLiteral()
    {
        var resolved = GatewayStartupUrlResolver.Resolve(null, null, null);

        resolved.ShouldBe(
            GatewayDefaults.LoopbackListenUrl,
            "With no URL from any source the banner must resolve from GatewayDefaults. This assertion "
            + "is written against the constant, not against '5005', so it cannot drift from the value "
            + "the rest of the platform binds.");
        resolved.Contains(":5000", StringComparison.Ordinal).ShouldBeFalse(
            "Issue #2929: the banner's fallback announced port 5000 while the gateway binds 5005.");
    }

    /// <summary>
    /// Blank strings are not values. An empty configured URL must not produce an empty banner.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankConfiguredUrl_FallsThroughRatherThanAnnouncingNothing(string configured)
    {
        var resolved = GatewayStartupUrlResolver.Resolve([], configured, aspNetCoreUrls: null);

        resolved.ShouldBe(GatewayDefaults.LoopbackListenUrl);
    }

    /// <summary>
    /// An ASPNETCORE_URLS value of only separators/whitespace is not a URL either.
    /// </summary>
    [Fact]
    public void Resolve_AspNetCoreUrlsIsOnlySeparators_FallsBackToTheSharedDefault()
    {
        var resolved = GatewayStartupUrlResolver.Resolve([], null, ";  ;");

        resolved.ShouldBe(GatewayDefaults.LoopbackListenUrl);
    }

    /// <summary>
    /// The default is 5005 and is spelled once. Asserting the port separately from the URL means a
    /// partial edit (changing the constant's port but not its host, or vice versa) still reddens.
    /// </summary>
    [Fact]
    public void GatewayDefaults_LoopbackUrlCarriesTheDeclaredPort()
    {
        GatewayDefaults.ListenPort.ShouldBe(5005);
        GatewayDefaults.LoopbackListenUrl.ShouldBe($"http://localhost:{GatewayDefaults.ListenPort}");
        GatewayDefaults.WildcardListenUrl.ShouldBe($"http://0.0.0.0:{GatewayDefaults.ListenPort}");
    }
}
