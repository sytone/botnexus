using BotNexus.Extensions.WebTools.Tests.Helpers;

namespace BotNexus.Extensions.WebTools.Tests;

/// <summary>
/// Issue #2418: the loopback rejection produced 116 identical retries in one week because the
/// error stated a policy with no alternative. These tests pin the exact text an agent sees.
/// The security posture is unchanged: every URL blocked before is still blocked.
/// </summary>
[Trait("Category", "Security")]
public class WebFetchToolLoopbackGuidanceTests
{
    /// <summary>
    /// The verbatim guidance appended to loopback rejections. Duplicated literally here
    /// (not referenced from production code) so the test pins the observable text.
    /// </summary>
    private const string Guidance =
        " web_fetch is a generic OUTBOUND fetch tool and cannot be used to inspect this gateway "
        + "or other services on this machine, so retrying this URL will always fail. To inspect "
        + "the local gateway (for example /health or /api/logs/recent), use a sanctioned local "
        + "mechanism instead: issue the request from the shell/exec tool against the local API.";

    [Fact]
    public async Task PrepareArgumentsAsync_GatewayLocalhostHealth_IsBlockedWithSelfCorrectingMessage()
    {
        using var tool = CreateTool();

        var act = () => tool.PrepareArgumentsAsync(
            new Dictionary<string, object?> { ["url"] = "http://localhost:5005/health" });

        var ex = await act.ShouldThrowAsync<ArgumentException>();
        ex.Message.ShouldBe(
            "URL host 'localhost' is blocked for security reasons (SSRF prevention)." + Guidance);
    }

    [Fact]
    public async Task PrepareArgumentsAsync_GatewayLocalhostLogs_IsBlockedWithSelfCorrectingMessage()
    {
        using var tool = CreateTool();

        var act = () => tool.PrepareArgumentsAsync(
            new Dictionary<string, object?> { ["url"] = "http://localhost:5005/api/logs/recent?limit=50" });

        var ex = await act.ShouldThrowAsync<ArgumentException>();
        ex.Message.ShouldBe(
            "URL host 'localhost' is blocked for security reasons (SSRF prevention)." + Guidance);
    }

    [Fact]
    public async Task PrepareArgumentsAsync_GatewayLoopbackIpLogs_IsBlockedWithSelfCorrectingMessage()
    {
        using var tool = CreateTool();

        var act = () => tool.PrepareArgumentsAsync(
            new Dictionary<string, object?> { ["url"] = "http://127.0.0.1:5005/api/logs/recent" });

        var ex = await act.ShouldThrowAsync<ArgumentException>();
        ex.Message.ShouldBe(
            "URL host '127.0.0.1' is blocked for security reasons (SSRF prevention)." + Guidance);
    }

    [Theory]
    [InlineData("http://localhost/", "localhost")]
    [InlineData("http://127.0.0.1:8080/admin", "127.0.0.1")]
    [InlineData("http://127.5.6.7/admin", "127.5.6.7")]
    [InlineData("http://[::1]/", "[::1]")]
    public async Task PrepareArgumentsAsync_AnyLoopbackForm_StillBlockedAndCarriesGuidance(
        string url,
        string expectedHost)
    {
        using var tool = CreateTool();

        var act = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["url"] = url });

        // Still blocked: an exception must be thrown, not merely a different message.
        var ex = await act.ShouldThrowAsync<ArgumentException>();
        ex.Message.ShouldBe(
            $"URL host '{expectedHost}' is blocked for security reasons (SSRF prevention).{Guidance}");
    }

    /// <summary>
    /// Control: non-loopback blocks must keep the message they have today, byte-identical.
    /// The gateway-inspection guidance would be wrong advice for these targets.
    /// </summary>
    [Theory]
    [InlineData("http://192.168.1.1/router", "192.168.1.1")]
    [InlineData("http://10.0.0.5/internal", "10.0.0.5")]
    [InlineData("http://172.16.0.1/internal", "172.16.0.1")]
    [InlineData("http://169.254.169.254/latest/meta-data/", "169.254.169.254")]
    [InlineData("http://100.64.0.1/cgn", "100.64.0.1")]
    [InlineData("http://metadata.google.internal/computeMetadata/v1/", "metadata.google.internal")]
    public async Task PrepareArgumentsAsync_NonLoopbackBlock_MessageIsByteIdenticalToToday(
        string url,
        string expectedHost)
    {
        using var tool = CreateTool();

        var act = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["url"] = url });

        var ex = await act.ShouldThrowAsync<ArgumentException>();
        ex.Message.ShouldBe(
            $"URL host '{expectedHost}' is blocked for security reasons (SSRF prevention).");
    }

    /// <summary>Control: public URLs are unaffected - no guidance, no rejection.</summary>
    [Theory]
    [InlineData("https://example.com")]
    [InlineData("https://api.github.com/repos")]
    public async Task PrepareArgumentsAsync_PublicUrl_StillAllowed(string url)
    {
        using var tool = CreateTool();

        var act = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["url"] = url });

        await act.ShouldNotThrowAsync();
    }

    /// <summary>
    /// Redirect hops to loopback are still blocked, the surfaced error carries the guidance,
    /// and the loopback request is never issued.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_RedirectToLoopback_IsBlockedAndSurfacesGuidance()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(
            System.Net.HttpStatusCode.Redirect,
            string.Empty,
            "text/plain",
            headers: new Dictionary<string, string> { ["Location"] = "http://localhost:5005/health" });
        using var tool = CreateTool(handler);
        var args = await tool.PrepareArgumentsAsync(
            new Dictionary<string, object?> { ["url"] = "https://example.com/redirect" });

        var result = await tool.ExecuteAsync("call-1", args);

        result.Content[0].Value.ShouldContain(
            "URL host 'localhost' is blocked for security reasons (SSRF prevention)." + Guidance);
        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].RequestUri!.Host.ShouldBe("example.com");
    }

    private static WebFetchTool CreateTool(MockHttpMessageHandler? handler = null)
    {
        var httpClient = new HttpClient(handler ?? new MockHttpMessageHandler());
        var config = new WebFetchConfig { MaxLengthChars = 20_000, TimeoutSeconds = 5 };
        return new WebFetchTool(config, httpClient);
    }
}
