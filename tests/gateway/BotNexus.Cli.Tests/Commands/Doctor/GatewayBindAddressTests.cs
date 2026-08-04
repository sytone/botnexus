using System.Text.Json.Nodes;
using BotNexus.Cli.Commands;
using BotNexus.Cli.Commands.Doctor;
using Shouldly;

namespace BotNexus.Cli.Tests.Commands.Doctor;

/// <summary>
/// Issue #2798: one definition of "is this a wildcard bind", shared by the <c>init</c> default and
/// the <c>doctor config</c> advisory. These tests pin the predicate itself, so a new wildcard
/// spelling added for one call site is proven to be recognised by both.
/// </summary>
public sealed class GatewayBindAddressTests
{
    [Theory]
    [InlineData("http://0.0.0.0:5005")]
    [InlineData("http://0.0.0.0")]
    [InlineData("https://0.0.0.0:443")]
    [InlineData("http://*:5005")]
    [InlineData("http://+:5005")]
    [InlineData("http://[::]:5005")]
    [InlineData("http://[::0]:5005")]
    [InlineData("http://0.0.0.0:5005/")]
    [InlineData("0.0.0.0:5005")]
    public void IsWildcard_TrueForEveryAllInterfacesSpelling(string listenUrl)
        => GatewayBindAddress.IsWildcard(listenUrl).ShouldBeTrue(listenUrl);

    [Theory]
    [InlineData("http://localhost:5005")]
    [InlineData("http://127.0.0.1:5005")]
    [InlineData("http://[::1]:5005")]
    [InlineData("http://192.168.1.10:5005")]
    [InlineData("https://gateway.example.com")]
    public void IsWildcard_FalseForSpecificAddresses(string listenUrl)
        => GatewayBindAddress.IsWildcard(listenUrl).ShouldBeFalse(listenUrl);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsWildcard_FalseForAbsentValue_UnreadableIsNotAnExposure(string? listenUrl)
        => GatewayBindAddress.IsWildcard(listenUrl).ShouldBeFalse();

    /// <summary>
    /// The two canonical listen URLs must not collide with each other's classification - the
    /// generated default is never a wildcard, and the documented opt-in always is.
    /// </summary>
    [Fact]
    public void CanonicalListenUrls_AreClassifiedConsistently()
    {
        GatewayBindAddress.IsWildcard(GatewayBindAddress.LoopbackListenUrl).ShouldBeFalse();
        GatewayBindAddress.IsWildcard(GatewayBindAddress.WildcardListenUrl).ShouldBeTrue();
    }

    [Fact]
    public void ReadListenUrl_ReturnsPersistedValue()
    {
        var root = JsonNode.Parse("{\"gateway\":{\"listenUrl\":\"http://0.0.0.0:5005\"}}")!.AsObject();
        GatewayBindAddress.ReadListenUrl(root).ShouldBe("http://0.0.0.0:5005");
    }

    [Fact]
    public void ReadListenUrl_ReturnsNull_WhenGatewayOrValueAbsent()
    {
        GatewayBindAddress.ReadListenUrl(new JsonObject()).ShouldBeNull();
        GatewayBindAddress.ReadListenUrl(JsonNode.Parse("{\"gateway\":{}}")!.AsObject()).ShouldBeNull();
    }
}

/// <summary>
/// Issue #2798 AC4: <c>doctor config</c> reports a finding when <c>listenUrl</c> binds a wildcard
/// address, and names the exposed surface.
/// </summary>
public sealed class WildcardListenUrlAdvisoryTests
{
    private static JsonObject Config(string listenUrl)
        => JsonNode.Parse($"{{\"gateway\":{{\"listenUrl\":\"{listenUrl}\"}}}}")!.AsObject();

    [Fact]
    public void Advisory_Applicable_WhenListenUrlIsWildcard()
        => new WildcardListenUrlAdvisory().IsApplicable(Config("http://0.0.0.0:5005")).ShouldBeTrue();

    [Fact]
    public void Advisory_NotApplicable_ForLoopbackDefault()
        => new WildcardListenUrlAdvisory()
            .IsApplicable(Config(GatewayBindAddress.LoopbackListenUrl))
            .ShouldBeFalse();

    [Fact]
    public void Advisory_NotApplicable_WhenListenUrlAbsent()
        => new WildcardListenUrlAdvisory().IsApplicable(new JsonObject()).ShouldBeFalse();

    /// <summary>
    /// AC4 requires the finding to name the exposed surface. A message that only says "wildcard"
    /// tells an operator nothing about what an attacker on the LAN can reach - and the admin
    /// endpoints are the reason this matters (#506).
    /// </summary>
    [Fact]
    public void Advisory_Description_NamesTheExposedSurface()
    {
        var description = new WildcardListenUrlAdvisory().Describe(Config("http://0.0.0.0:5005"));

        description.ShouldContain("0.0.0.0:5005");
        description.ShouldContain("admin", Case.Insensitive);
        description.ShouldContain("portal", Case.Insensitive);
        description.ShouldContain("REST API", Case.Insensitive);
    }

    [Fact]
    public void Advisory_Remediation_OffersLoopbackAndExplainsTheRemoteCase()
    {
        var remediation = new WildcardListenUrlAdvisory().Remediation;

        remediation.ShouldContain(GatewayBindAddress.LoopbackListenUrl);
        remediation.ShouldContain("remote", Case.Insensitive);
    }

    /// <summary>
    /// #2798 AC3 structural guard: the wildcard finding must never become an auto-applied
    /// <see cref="IConfigCheck"/>. If it did, <c>doctor config --yes</c> would rewrite an operator's
    /// deliberate remote-access configuration. Asserting the type relationship, not just current
    /// behaviour, is what stops a future "let's make it fixable" refactor.
    /// </summary>
    [Fact]
    public void Advisory_IsNotAnAutoApplicableConfigCheck()
    {
        typeof(IConfigCheck).IsAssignableFrom(typeof(WildcardListenUrlAdvisory)).ShouldBeFalse(
            "a wildcard bind can be deliberate; doctor config must report it, never rewrite it (#2798 AC3).");

        DoctorConfigCommand.Checks.ShouldNotContain(
            c => c.GetType() == typeof(WildcardListenUrlAdvisory),
            "the wildcard advisory must stay out of the auto-apply check list (#2798 AC3).");
    }
}
