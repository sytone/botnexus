using BotNexus.Gateway.Security;

namespace BotNexus.Gateway.Tests.Security;

/// <summary>
/// Verifies the forward-design external-delivery redaction primitive added for #1752:
/// <see cref="SecretRedactor.RedactForExternalDelivery(string)"/> must strip device /
/// verification codes, device action URLs, and key=value secrets before a cron summary
/// ever leaves the box. These are happy + sad path tests and must not be weakened.
/// </summary>
public sealed class SecretRedactorExternalDeliveryTests
{
    private readonly SecretRedactor _sut = new();

    [Fact]
    public void RedactForExternalDelivery_NullOrEmpty_ReturnsInput()
    {
        _sut.RedactForExternalDelivery(string.Empty).ShouldBe(string.Empty);
        _sut.RedactForExternalDelivery(null!).ShouldBeNull();
    }

    [Theory]
    [InlineData("WDJB-MJHT")]
    [InlineData("ABCD-1234")]
    [InlineData("9F4K-7T2Q")]
    public void RedactForExternalDelivery_HyphenatedDeviceCode_IsRedacted(string code)
    {
        var input = $"Your verification code is {code} - enter it soon.";
        var result = _sut.RedactForExternalDelivery(input);
        result.ShouldNotContain(code);
        result.ShouldContain("[redacted-code]");
    }

    [Theory]
    [InlineData("enter code 483920")]
    [InlineData("enter code A1B2C3D4")]
    public void RedactForExternalDelivery_EnterCodePhrase_IsRedacted(string phrase)
    {
        var result = _sut.RedactForExternalDelivery(phrase);
        result.ShouldContain("[redacted-code]");
        result.ShouldNotContain(phrase.Replace("enter code ", string.Empty));
    }

    [Fact]
    public void RedactForExternalDelivery_DeviceActionUrl_IsRedacted()
    {
        const string url = "https://github.com/login/device";
        var input = $"To authorize, visit {url} and enter code WDJB-MJHT.";
        var result = _sut.RedactForExternalDelivery(input);

        result.ShouldNotContain(url);
        result.ShouldContain("[redacted-url]");
        result.ShouldNotContain("WDJB-MJHT");
        result.ShouldContain("[redacted-code]");
    }

    [Theory]
    [InlineData("token=abc123def456ghi789")]
    [InlineData("api_key=SOMELONGSECRETVALUE123")]
    [InlineData("api-key: SOMELONGSECRETVALUE123")]
    [InlineData("password=hunter2hunter2hunter2")]
    [InlineData("secret=topsecretvalue987654321")]
    public void RedactForExternalDelivery_KeyValueSecret_IsMaskedWithStars(string kv)
    {
        var result = _sut.RedactForExternalDelivery(kv);
        result.ShouldContain("***");
        // The secret value itself must be gone; the key name is preserved.
        var value = kv.Split(new[] { '=', ':' }, 2)[1].Trim();
        result.ShouldNotContain(value);
    }

    [Fact]
    public void RedactForExternalDelivery_AlsoAppliesBaseSecretPatterns()
    {
        const string ghToken = "ghp_aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789";
        var input = $"cloned repo using {ghToken}";
        var result = _sut.RedactForExternalDelivery(input);
        result.ShouldNotContain(ghToken);
        result.ShouldContain("[REDACTED]");
    }

    [Fact]
    public void RedactForExternalDelivery_SafeText_IsUnchanged()
    {
        const string safe = "Job completed: processed 42 records in 1.3s. No errors.";
        _sut.RedactForExternalDelivery(safe).ShouldBe(safe);
    }
}
