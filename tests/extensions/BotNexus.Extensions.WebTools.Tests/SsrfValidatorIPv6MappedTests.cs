using System.Net;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Extensions.WebTools.Tests;

/// <summary>
/// #3809: the IPv6 half of the shared SSRF address table. Before this fix the validator returned
/// <c>Allowed</c> for every IPv6 address except a literal <c>::1</c>, so an IPv4-mapped spelling of
/// a blocked IPv4 address (<c>[::ffff:169.254.169.254]</c>) reached cloud metadata, loopback and
/// every RFC-1918 host through <c>web_fetch</c>, the browser tools and the cron webhook target.
/// <para>
/// These tests fail on origin/main ef638c506 and pass with the normalisation fix.
/// </para>
/// </summary>
public sealed class SsrfValidatorIPv6MappedTests
{
    /// <summary>
    /// Every IPv4 address the validator blocks, in its IPv4-mapped IPv6 spelling. Acceptance
    /// criteria 1 and 2: mapped loopback (both compressed and fully-expanded), mapped IMDS, and
    /// the mapped forms of each blocked private range.
    /// </summary>
    [Theory]
    [InlineData("http://[::ffff:127.0.0.1]/")]              // mapped loopback
    [InlineData("http://[0:0:0:0:0:ffff:7f00:1]/")]         // same address, fully expanded
    [InlineData("http://[::ffff:169.254.169.254]/latest/meta-data")] // mapped IMDS
    [InlineData("http://[::ffff:10.0.0.1]/")]               // mapped RFC-1918 /8
    [InlineData("http://[::ffff:172.16.0.1]/")]             // mapped RFC-1918 /12
    [InlineData("http://[::ffff:192.168.0.1]/")]            // mapped RFC-1918 /16
    [InlineData("http://[::ffff:100.64.0.1]/")]             // mapped CGN
    [InlineData("http://[::ffff:0.0.0.0]/")]                // mapped any
    public void Validate_IPv4MappedIPv6_BlockedLikeItsIPv4Form(string url)
    {
        var result = SsrfValidator.Validate(new Uri(url));

        result.IsSafe.ShouldBeFalse();
        result.Reason!.ShouldContain("SSRF prevention");
    }

    /// <summary>
    /// Acceptance criterion 3: native IPv6 private/reserved ranges that have no IPv4 equivalent
    /// and were therefore never in the table at all.
    /// </summary>
    [Theory]
    [InlineData("http://[fe80::1]/")]      // link-local fe80::/10
    [InlineData("http://[fd00::1]/")]      // unique-local fc00::/7
    [InlineData("http://[fc00::1]/")]      // unique-local, lower bound
    [InlineData("http://[::]/")]           // unspecified
    [InlineData("http://[ff02::1]/")]      // multicast all-nodes
    public void Validate_ReservedNativeIPv6_ReturnsBlocked(string url)
    {
        var result = SsrfValidator.Validate(new Uri(url));

        result.IsSafe.ShouldBeFalse();
        result.Reason!.ShouldContain("SSRF prevention");
    }

    /// <summary>
    /// The transitional forms <see cref="IPAddress.IsIPv4MappedToIPv6"/> does NOT recognise:
    /// IPv4-compatible (<c>::a.b.c.d</c>) and 6to4 (<c>2002:aabb:ccdd::/16</c>). Both still reach
    /// the embedded IPv4 destination, so a mapped-only fix would leave the bypass open.
    /// </summary>
    [Theory]
    [InlineData("http://[::169.254.169.254]/")]   // IPv4-compatible IMDS
    [InlineData("http://[::127.0.0.1]/")]         // IPv4-compatible loopback
    [InlineData("http://[::10.0.0.1]/")]          // IPv4-compatible RFC-1918
    [InlineData("http://[2002:a9fe:a9fe::1]/")]   // 6to4 wrapping 169.254.169.254
    [InlineData("http://[2002:7f00:1::1]/")]      // 6to4 wrapping 127.0.0.1
    [InlineData("http://[2002:0a00:0001::1]/")]   // 6to4 wrapping 10.0.0.1
    public void Validate_TransitionalIPv6Forms_BlockedByEmbeddedIPv4(string url)
    {
        var result = SsrfValidator.Validate(new Uri(url));

        result.IsSafe.ShouldBeFalse();
        result.Reason!.ShouldContain("SSRF prevention");
    }

    /// <summary>
    /// Acceptance criterion 4: the fix must not become a blanket IPv6 ban. Routable public IPv6
    /// addresses, and 6to4 wrapping a public IPv4, are still allowed.
    /// </summary>
    [Theory]
    [InlineData("http://[2606:4700:4700::1111]/")] // Cloudflare public resolver
    [InlineData("http://[2001:4860:4860::8888]/")] // Google public resolver
    [InlineData("http://[2001:db8::1]/")]          // documentation range, not reserved-private
    [InlineData("http://[::ffff:8.8.8.8]/")]       // mapped PUBLIC IPv4 stays allowed
    [InlineData("http://[2002:0808:0808::1]/")]    // 6to4 wrapping public 8.8.8.8
    public void Validate_RoutablePublicIPv6_ReturnsAllowed(string url)
    {
        var result = SsrfValidator.Validate(new Uri(url));

        result.IsSafe.ShouldBeTrue();
        result.Reason.ShouldBeNull();
    }

    /// <summary>
    /// Acceptance criterion 5: the anti-drift invariant. For every IPv4 address the validator
    /// blocks, its IPv4-mapped IPv6 form must also be blocked -- so the two halves of the table
    /// cannot diverge again the way they did in #2745.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.1")]
    [InlineData("192.168.255.255")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    public void Validate_BlockedIPv4_ImpliesMappedFormAlsoBlocked(string ipv4)
    {
        // Guard the premise: this address really is blocked in its IPv4 form.
        var v4 = SsrfValidator.Validate(new Uri($"http://{ipv4}/"));
        v4.IsSafe.ShouldBeFalse();

        var mapped = IPAddress.Parse(ipv4).MapToIPv6();
        var v6 = SsrfValidator.Validate(new Uri($"http://[{mapped}]/"));

        v6.IsSafe.ShouldBeFalse($"IPv4 {ipv4} is blocked but its mapped form [{mapped}] was allowed");
    }

    /// <summary>
    /// The converse invariant: an allowed public IPv4 must stay allowed in mapped form, proving
    /// the normalisation classifies by the embedded address rather than blocking indiscriminately.
    /// </summary>
    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("203.0.113.1")]
    [InlineData("172.15.255.255")] // just below the RFC-1918 /12 boundary
    [InlineData("172.32.0.0")]     // just above it
    public void Validate_AllowedIPv4_ImpliesMappedFormAlsoAllowed(string ipv4)
    {
        var v4 = SsrfValidator.Validate(new Uri($"http://{ipv4}/"));
        v4.IsSafe.ShouldBeTrue();

        var mapped = IPAddress.Parse(ipv4).MapToIPv6();
        var v6 = SsrfValidator.Validate(new Uri($"http://[{mapped}]/"));

        v6.IsSafe.ShouldBeTrue($"IPv4 {ipv4} is allowed but its mapped form [{mapped}] was blocked");
    }

    /// <summary>
    /// <c>AssertSafe</c> is the exception-shaped entry point used by tool argument validation;
    /// it must reject the mapped IMDS address too.
    /// </summary>
    [Fact]
    public void AssertSafe_MappedImds_Throws()
    {
        Should.Throw<ArgumentException>(() =>
            SsrfValidator.AssertSafe(new Uri("http://[::ffff:169.254.169.254]/latest/meta-data")));
    }
}
