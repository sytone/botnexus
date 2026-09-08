using BotNexus.Extensions.WebTools.Tests.Helpers;

namespace BotNexus.Extensions.WebTools.Tests;

/// <summary>
/// #3809 acceptance criterion 6, web_fetch half: the shared SsrfValidator fix must actually reach
/// the delegating call site. A unit test on the validator alone would pass even if this tool had
/// its own bypassing path, so the mapped-IMDS URL is asserted through the tool's real argument
/// preparation entry point.
/// </summary>
[Trait("Category", "Security")]
public sealed class WebFetchToolMappedIPv6Tests
{
    [Theory]
    [InlineData("http://[::ffff:169.254.169.254]/latest/meta-data")]
    [InlineData("http://[::ffff:127.0.0.1]:5005/health")]
    [InlineData("http://[0:0:0:0:0:ffff:7f00:1]:5005/health")]
    [InlineData("http://[::ffff:10.0.0.1]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[fd00::1]/")]
    public async Task PrepareArgumentsAsync_MappedOrReservedIPv6_IsBlocked(string url)
    {
        using var tool = CreateTool();

        var act = () => tool.PrepareArgumentsAsync(
            new Dictionary<string, object?> { ["url"] = url });

        var ex = await act.ShouldThrowAsync<ArgumentException>();
        ex.Message.ShouldContain("SSRF prevention");
    }

    [Fact]
    public async Task PrepareArgumentsAsync_PublicIPv6_IsStillAllowed()
    {
        using var tool = CreateTool();

        await Should.NotThrowAsync(() => tool.PrepareArgumentsAsync(
            new Dictionary<string, object?> { ["url"] = "https://[2606:4700:4700::1111]/" }));
    }

    private static WebFetchTool CreateTool()
    {
        var httpClient = new HttpClient(new MockHttpMessageHandler());
        var config = new WebFetchConfig { MaxLengthChars = 20_000, TimeoutSeconds = 5 };
        return new WebFetchTool(config, httpClient);
    }
}
