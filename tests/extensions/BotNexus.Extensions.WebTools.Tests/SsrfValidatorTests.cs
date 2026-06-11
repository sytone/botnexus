using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Extensions.WebTools.Tests;

public sealed class SsrfValidatorTests
{
    [Theory]
    [InlineData("https://example.com")]
    [InlineData("https://api.github.com/repos")]
    [InlineData("http://8.8.8.8/health")]
    [InlineData("https://203.0.113.1/api")]
    public void Validate_PublicUrls_ReturnsAllowed(string url)
    {
        var result = SsrfValidator.Validate(new Uri(url));
        result.IsSafe.ShouldBeTrue();
        result.Reason.ShouldBeNull();
    }

    [Theory]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://127.0.0.1:8080/api")]
    [InlineData("http://127.255.0.1")]
    public void Validate_LoopbackIPv4_ReturnsBlocked(string url)
    {
        var result = SsrfValidator.Validate(new Uri(url));
        result.IsSafe.ShouldBeFalse();
        result.Reason!.ShouldContain("SSRF prevention");
    }

    [Fact]
    public void Validate_LoopbackIPv6_ReturnsBlocked()
    {
        var result = SsrfValidator.Validate(new Uri("http://[::1]/api"));
        result.IsSafe.ShouldBeFalse();
        result.Reason!.ShouldContain("SSRF prevention");
    }

    [Fact]
    public void Validate_Localhost_ReturnsBlocked()
    {
        var result = SsrfValidator.Validate(new Uri("http://localhost/api"));
        result.IsSafe.ShouldBeFalse();
        result.Reason!.ShouldContain("SSRF prevention");
    }

    [Fact]
    public void Validate_LocalhostCaseInsensitive_ReturnsBlocked()
    {
        var result = SsrfValidator.Validate(new Uri("http://LOCALHOST:3000/"));
        result.IsSafe.ShouldBeFalse();
    }

    [Fact]
    public void Validate_GoogleMetadata_ReturnsBlocked()
    {
        var result = SsrfValidator.Validate(new Uri("http://metadata.google.internal/computeMetadata"));
        result.IsSafe.ShouldBeFalse();
        result.Reason!.ShouldContain("SSRF prevention");
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data")] // AWS IMDS
    [InlineData("http://169.254.0.1")]                       // link-local
    public void Validate_LinkLocalImds_ReturnsBlocked(string url)
    {
        var result = SsrfValidator.Validate(new Uri(url));
        result.IsSafe.ShouldBeFalse();
    }

    [Theory]
    [InlineData("http://10.0.0.1")]
    [InlineData("http://10.255.255.255")]
    [InlineData("http://172.16.0.1")]
    [InlineData("http://172.31.255.255")]
    [InlineData("http://192.168.0.1")]
    [InlineData("http://192.168.255.255")]
    public void Validate_Rfc1918_ReturnsBlocked(string url)
    {
        var result = SsrfValidator.Validate(new Uri(url));
        result.IsSafe.ShouldBeFalse();
    }

    [Theory]
    [InlineData("http://100.64.0.1")]   // CGN start
    [InlineData("http://100.127.255.255")] // CGN end
    public void Validate_CarrierGradeNat_ReturnsBlocked(string url)
    {
        var result = SsrfValidator.Validate(new Uri(url));
        result.IsSafe.ShouldBeFalse();
    }

    [Fact]
    public void Validate_ZeroNetwork_ReturnsBlocked()
    {
        var result = SsrfValidator.Validate(new Uri("http://0.0.0.0/"));
        result.IsSafe.ShouldBeFalse();
    }

    [Theory]
    [InlineData("http://172.15.255.255")] // just below 172.16.0.0
    [InlineData("http://172.32.0.0")]     // just above 172.31.255.255
    public void Validate_NearRfc1918Boundary_ReturnsAllowed(string url)
    {
        var result = SsrfValidator.Validate(new Uri(url));
        result.IsSafe.ShouldBeTrue();
    }

    [Fact]
    public void Validate_AdditionalBlockedHosts_ReturnsBlocked()
    {
        var blocked = new[] { "evil.internal", "attacker.local" };
        var result = SsrfValidator.Validate(new Uri("https://evil.internal/api"), blocked);
        result.IsSafe.ShouldBeFalse();
        result.Reason!.ShouldContain("blocked by configuration");
    }

    [Fact]
    public void Validate_AdditionalBlockedHosts_CaseInsensitive()
    {
        var blocked = new[] { "EVIL.INTERNAL" };
        var result = SsrfValidator.Validate(new Uri("https://evil.internal/api"), blocked);
        result.IsSafe.ShouldBeFalse();
    }

    [Fact]
    public void Validate_NonBlockedHost_ReturnsAllowed()
    {
        var blocked = new[] { "evil.internal" };
        var result = SsrfValidator.Validate(new Uri("https://safe.example.com/api"), blocked);
        result.IsSafe.ShouldBeTrue();
    }

    [Fact]
    public void Validate_NonHttpScheme_ReturnsBlocked()
    {
        var result = SsrfValidator.Validate(new Uri("ftp://files.example.com/data"));
        result.IsSafe.ShouldBeFalse();
        result.Reason!.ShouldContain("Only HTTP and HTTPS are permitted");
    }

    [Fact]
    public void AssertSafe_BlockedUrl_ThrowsArgumentException()
    {
        Should.Throw<ArgumentException>(() =>
            SsrfValidator.AssertSafe(new Uri("http://169.254.169.254/latest")));
    }

    [Fact]
    public void AssertSafe_SafeUrl_DoesNotThrow()
    {
        Should.NotThrow(() =>
            SsrfValidator.AssertSafe(new Uri("https://api.example.com/data")));
    }

    [Fact]
    public void Validate_NullUri_ThrowsArgumentNull()
    {
        Should.Throw<ArgumentNullException>(() =>
            SsrfValidator.Validate(null!));
    }

    [Fact]
    public void Validate_PublicIPv6_ReturnsAllowed()
    {
        // 2001:db8::1 is documentation range but not loopback
        var result = SsrfValidator.Validate(new Uri("http://[2001:db8::1]/api"));
        result.IsSafe.ShouldBeTrue();
    }

    [Fact]
    public void Validate_HostnameNotResolved_ReturnsAllowed()
    {
        // Non-IP hostnames are allowed (DNS not resolved at validation time)
        var result = SsrfValidator.Validate(new Uri("https://internal.corp.example.com/api"));
        result.IsSafe.ShouldBeTrue();
    }
}
