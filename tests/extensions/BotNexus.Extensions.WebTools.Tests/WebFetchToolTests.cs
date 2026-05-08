using BotNexus.Extensions.WebTools.Tests.Helpers;

namespace BotNexus.Extensions.WebTools.Tests;

[Trait("Category", "Unit")]
public class WebFetchToolTests
{
    [Fact]
    public async Task ExecuteAsync_WithValidUrl_FetchesContent()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(System.Net.HttpStatusCode.OK, "<html><body><h1>Hello</h1></body></html>", "text/html");
        using var tool = CreateTool(handler);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["url"] = "https://example.com" });

        var result = await tool.ExecuteAsync("call-1", args);

        result.Content[0].Value.ShouldContain("Hello");
    }

    [Fact]
    public async Task ExecuteAsync_WithMaxLength_TruncatesResponse()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(System.Net.HttpStatusCode.OK, "<html><body>abcdefghijklmnopqrstuvwxyz</body></html>", "text/html");
        using var tool = CreateTool(handler);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["url"] = "https://example.com",
            ["max_length"] = 10
        });

        var result = await tool.ExecuteAsync("call-1", args);

        result.Content[0].Value.ShouldStartWith("abcdefghij");
        result.Content[0].Value.ShouldContain("Content truncated");
    }

    [Fact]
    public async Task ExecuteAsync_RawModeTrue_ReturnsHtml()
    {
        const string html = "<html><body><p>Raw content</p></body></html>";
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(System.Net.HttpStatusCode.OK, html, "text/html");
        using var tool = CreateTool(handler);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["url"] = "https://example.com",
            ["raw"] = true
        });

        var result = await tool.ExecuteAsync("call-1", args);

        result.Content[0].Value.ShouldBe(html);
    }

    [Fact]
    public async Task ExecuteAsync_RawModeFalse_ReturnsSimplifiedText()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(System.Net.HttpStatusCode.OK, "<html><body><p>Hello <a href=\"https://example.com\">Link</a></p></body></html>", "text/html");
        using var tool = CreateTool(handler);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["url"] = "https://example.com",
            ["raw"] = false
        });

        var result = await tool.ExecuteAsync("call-1", args);

        result.Content[0].Value.ShouldBe("Hello [Link](https://example.com)");
    }

    [Fact]
    public async Task ExecuteAsync_WithStartIndex_AppliesPagination()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(System.Net.HttpStatusCode.OK, "<html><body>0123456789ABCDEFGHIJ</body></html>", "text/html");
        using var tool = CreateTool(handler);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["url"] = "https://example.com",
            ["start_index"] = 5,
            ["max_length"] = 6
        });

        var result = await tool.ExecuteAsync("call-1", args);

        result.Content[0].Value.ShouldStartWith("56789A");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PrepareArgumentsAsync_WithNullOrEmptyUrl_Throws(string? url)
    {
        using var tool = CreateTool(new MockHttpMessageHandler());

        var act = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["url"] = url });

        (await act.ShouldThrowAsync<ArgumentException>()).Message.ShouldContain("url is required");
    }

    [Fact]
    public async Task ExecuteAsync_WithHttp404_ReturnsGracefulError()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(System.Net.HttpStatusCode.NotFound, "missing", "text/plain");
        using var tool = CreateTool(handler);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["url"] = "https://example.com/missing" });

        var result = await tool.ExecuteAsync("call-1", args);

        result.Content[0].Value.ShouldContain("HTTP 404");
    }

    [Fact]
    public async Task ExecuteAsync_WithHttp500_ReturnsGracefulError()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(System.Net.HttpStatusCode.InternalServerError, "boom", "text/plain");
        using var tool = CreateTool(handler);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["url"] = "https://example.com/error" });

        var result = await tool.ExecuteAsync("call-1", args);

        result.Content[0].Value.ShouldContain("HTTP 500");
    }

    [Fact]
    public async Task ExecuteAsync_WithTimeout_ReturnsError()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueException(new TaskCanceledException("timeout", new TimeoutException("simulated timeout")));
        using var tool = CreateTool(handler);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["url"] = "https://example.com/slow" });

        var result = await tool.ExecuteAsync("call-1", args);

        result.Content[0].Value.ShouldContain("Request timed out");
    }

    [Fact]
    public async Task PrepareArgumentsAsync_WithInvalidUrlFormat_Throws()
    {
        using var tool = CreateTool(new MockHttpMessageHandler());

        var act = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["url"] = "not-a-url" });

        (await act.ShouldThrowAsync<ArgumentException>()).Message.ShouldContain("valid HTTP or HTTPS URL");
    }

    [Theory]
    [InlineData("ftp://example.com/file")]
    [InlineData("file://localhost/c$/secret.txt")]
    public async Task PrepareArgumentsAsync_WithUnsupportedScheme_Throws(string url)
    {
        using var tool = CreateTool(new MockHttpMessageHandler());

        var act = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["url"] = url });

        (await act.ShouldThrowAsync<ArgumentException>()).Message.ShouldContain("valid HTTP or HTTPS URL");
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/admin")]
    [InlineData("http://10.0.0.1/internal")]
    [InlineData("http://192.168.1.1/router")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://localhost/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://[::1]/")]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task ExecuteAsync_WithPrivateOrLoopbackTargets_CurrentBehaviorDoesNotBlock(string url)
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(System.Net.HttpStatusCode.OK, "<html><body>internal resource</body></html>", "text/html");
        using var tool = CreateTool(handler);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["url"] = url });

        var result = await tool.ExecuteAsync("call-1", args);

        result.Content[0].Value.ShouldContain("internal resource");
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task ExecuteAsync_WithDnsRebindingStyleHost_CurrentBehaviorDoesNotBlock()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(System.Net.HttpStatusCode.OK, "<html><body>dns content</body></html>", "text/html");
        using var tool = CreateTool(handler);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["url"] = "http://rebind.attacker.test/path" });

        var result = await tool.ExecuteAsync("call-1", args);

        result.Content[0].Value.ShouldContain("dns content");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PrepareArgumentsAsync_WithCrlfEncodedUrl_DoesNotInjectHeaders()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(System.Net.HttpStatusCode.OK, "<html><body>ok</body></html>", "text/html");
        using var tool = CreateTool(handler);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["url"] = "https://example.com/path%0d%0aX-Evil:1"
        });

        _ = await tool.ExecuteAsync("call-1", args);

        handler.Requests.ShouldHaveSingleItem();
        var uri = handler.Requests[0].RequestUri!.ToString();
        uri.ShouldNotContain("\r");
        uri.ShouldNotContain("\n");
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task ExecuteAsync_WithRedirectToPrivateIp_CurrentBehaviorNotBlocked()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(System.Net.HttpStatusCode.Redirect, string.Empty, "text/plain", headers: new Dictionary<string, string>
        {
            ["Location"] = "http://127.0.0.1/admin"
        });
        using var tool = CreateTool(handler);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["url"] = "https://example.com/redirect" });

        var result = await tool.ExecuteAsync("call-1", args);

        result.Content[0].Value.ShouldContain("HTTP 302");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ExecuteAsync_WithVeryLargeResponse_TruncatesWithoutOom()
    {
        var payload = $"<html><body>{new string('x', 1_000_000)}</body></html>";
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(System.Net.HttpStatusCode.OK, payload, "text/html");
        using var tool = CreateTool(handler);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["url"] = "https://example.com/large",
            ["max_length"] = 500
        });

        var result = await tool.ExecuteAsync("call-1", args);

        result.Content[0].Value.Length.ShouldBeLessThan(650);
        result.Content[0].Value.ShouldContain("Content truncated");
    }

    [Fact]
    public async Task ExecuteAsync_WithOffsetBeyondContent_ReturnsNoContentMarker()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(System.Net.HttpStatusCode.OK, "<html><body>short</body></html>", "text/html");
        using var tool = CreateTool(handler);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["url"] = "https://example.com",
            ["start_index"] = 999
        });

        var result = await tool.ExecuteAsync("call-1", args);

        result.Content[0].Value.ShouldBe("[No content at this offset]");
    }

    private static WebFetchTool CreateTool(MockHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        var config = new WebFetchConfig { MaxLengthChars = 20_000, TimeoutSeconds = 5 };
        return new WebFetchTool(config, httpClient);
    }
}
