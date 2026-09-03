namespace BotNexus.Cron.Tests;

/// <summary>
/// #3809 acceptance criterion 6, cron-webhook half. <see cref="CronWebhookUrl"/> delegates address
/// classification to the shared <c>SsrfValidator</c> rather than keeping its own table (#2745), so
/// the IPv6-mapped fix must reach it with no change to this file. These assertions fail on
/// origin/main ef638c506, where a mapped IMDS webhook target was accepted and persisted.
/// </summary>
public sealed class CronWebhookUrlMappedIPv6Tests
{
    [Theory]
    [InlineData("http://[::ffff:169.254.169.254]/latest/meta-data")]
    [InlineData("http://[::ffff:127.0.0.1]:5005/hook")]
    [InlineData("http://[0:0:0:0:0:ffff:7f00:1]/hook")]
    [InlineData("http://[::ffff:10.0.0.1]/hook")]
    [InlineData("http://[::ffff:192.168.0.1]/hook")]
    [InlineData("http://[fe80::1]/hook")]
    [InlineData("http://[fd00::1]/hook")]
    [InlineData("http://[::]/hook")]
    public void TryNormalize_MappedOrReservedIPv6_IsRejectedAsBlockedAddress(string url)
    {
        CronWebhookUrl.TryNormalize(url, out var normalized, out var reason).ShouldBeFalse();

        normalized.ShouldBeNull();
        reason.ShouldBe(CronWebhookUrl.BlockedAddressRejectionMessage);
    }

    [Theory]
    [InlineData("https://[2606:4700:4700::1111]/hook")]
    [InlineData("https://[::ffff:8.8.8.8]/hook")]
    public void TryNormalize_PublicIPv6_IsStillAccepted(string url)
    {
        CronWebhookUrl.TryNormalize(url, out var normalized, out var reason).ShouldBeTrue();

        normalized.ShouldBe(url);
        reason.ShouldBeNull();
    }
}
