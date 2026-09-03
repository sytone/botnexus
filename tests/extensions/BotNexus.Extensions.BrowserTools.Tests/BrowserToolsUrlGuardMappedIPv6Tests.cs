using BotNexus.Extensions.BrowserTools;

namespace BotNexus.Extensions.BrowserTools.Tests;

/// <summary>
/// #3809 acceptance criterion 6, browser-tools half. <see cref="BrowserToolsUrlGuard"/> delegates
/// address classification to the shared <c>SsrfValidator</c>, so the IPv6-mapped fix must reach
/// navigation without any change to the guard itself. This matters more here than elsewhere: the
/// guard also classifies URLs discovered from the page and from CDP, not just model-supplied ones.
/// </summary>
[Trait("Category", "Security")]
public sealed class BrowserToolsUrlGuardMappedIPv6Tests
{
    [Theory]
    [InlineData("http://[::ffff:169.254.169.254]/latest/meta-data")]
    [InlineData("http://[::ffff:127.0.0.1]:9222/json/list")]
    [InlineData("http://[0:0:0:0:0:ffff:7f00:1]:9222/json/list")]
    [InlineData("http://[::ffff:10.0.0.1]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[fd00::1]/")]
    public void Validate_MappedOrReservedIPv6_IsDenied(string url)
    {
        var result = BrowserToolsUrlGuard.Validate(url);

        result.IsAllowed.ShouldBeFalse();
        result.Reason!.ShouldContain("SSRF prevention");
    }

    [Fact]
    public void Validate_PublicIPv6_IsStillAllowed()
    {
        var result = BrowserToolsUrlGuard.Validate("https://[2606:4700:4700::1111]/");

        result.IsAllowed.ShouldBeTrue();
    }
}
