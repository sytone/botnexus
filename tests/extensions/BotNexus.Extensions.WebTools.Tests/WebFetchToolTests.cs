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

        result.Content[0].Value.ShouldContain("abcdefghij");
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

        result.Content[0].Value.ShouldContain(html);
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

        result.Content[0].Value.ShouldContain("Hello [Link](https://example.com)");
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

        result.Content[0].Value.ShouldContain("56789A");
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
    [InlineData("http://100.64.0.1/cgn")]
    [InlineData("http://172.16.0.1/internal")]
    [InlineData("http://metadata.google.internal/computeMetadata/v1/")]
    [Trait("Category", "Security")]
    public async Task PrepareArgumentsAsync_WithPrivateOrImdsTarget_Throws(string url)
    {
        using var tool = CreateTool(new MockHttpMessageHandler());

        var act = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["url"] = url });

        var ex = await act.ShouldThrowAsync<ArgumentException>();
        ex.Message.ShouldContain("blocked");
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("https://api.github.com")]
    [InlineData("http://httpbin.org/get")]
    [Trait("Category", "Security")]
    public async Task PrepareArgumentsAsync_WithPublicUrl_DoesNotThrow(string url)
    {
        using var tool = CreateTool(new MockHttpMessageHandler());

        var act = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["url"] = url });

        await act.ShouldNotThrowAsync();
    }

    [Theory]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://10.0.0.1/internal")]
    [Trait("Category", "Security")]
    public async Task PrepareArgumentsAsync_AllowPrivateNetworks_PermitsPrivateUrls(string url)
    {
        var httpClient = new HttpClient(new MockHttpMessageHandler());
        var config = new WebFetchConfig
        {
            MaxLengthChars = 20_000,
            TimeoutSeconds = 5,
            AllowPrivateNetworks = true
        };
        using var tool = new WebFetchTool(config, httpClient);

        var act = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["url"] = url });

        await act.ShouldNotThrowAsync();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PrepareArgumentsAsync_WithAdditionalBlockedHost_Throws()
    {
        var httpClient = new HttpClient(new MockHttpMessageHandler());
        var config = new WebFetchConfig
        {
            MaxLengthChars = 20_000,
            TimeoutSeconds = 5,
            AdditionalBlockedHosts = ["blocked-internal.corp.example"]
        };
        using var tool = new WebFetchTool(config, httpClient);

        var act = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["url"] = "https://blocked-internal.corp.example/secret" });

        var ex = await act.ShouldThrowAsync<ArgumentException>();
        ex.Message.ShouldContain("blocked");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ExecuteAsync_WithDnsRebindingStyleHost_AllowedByDefault()
    {
        // External hostnames that happen to look suspicious are allowed by default --
        // blocking requires DNS resolution which we cannot do synchronously. The protection
        // against IP-level IMDS addresses is what matters.
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
    public async Task ExecuteAsync_RedirectToPrivateAddress_IsBlocked()
    {
        // A safe public URL that 302-redirects to a loopback address must be blocked,
        // not followed -- this is the redirect-based SSRF bypass guard.
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(System.Net.HttpStatusCode.Redirect, string.Empty, "text/plain", headers: new Dictionary<string, string>
        {
            ["Location"] = "http://127.0.0.1/admin"
        });
        using var tool = CreateTool(handler);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["url"] = "https://example.com/redirect" });

        var result = await tool.ExecuteAsync("call-1", args);

        result.Content[0].Value.ShouldContain("blocked");
        // The request to the private host must never have been issued.
        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].RequestUri!.Host.ShouldBe("example.com");
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://localhost/secret")]
    [InlineData("http://10.0.0.5/internal")]
    [InlineData("http://[::1]/")]
    [Trait("Category", "Security")]
    public async Task ExecuteAsync_RedirectToVariousInternalTargets_IsBlocked(string redirectTarget)
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(System.Net.HttpStatusCode.MovedPermanently, string.Empty, "text/plain", headers: new Dictionary<string, string>
        {
            ["Location"] = redirectTarget
        });
        using var tool = CreateTool(handler);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["url"] = "https://example.com/start" });

        var result = await tool.ExecuteAsync("call-1", args);

        result.Content[0].Value.ShouldContain("blocked");
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ExecuteAsync_RedirectToPublicUrl_IsFollowed()
    {
        // A redirect to another safe public URL should be followed transparently.
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(System.Net.HttpStatusCode.Redirect, string.Empty, "text/plain", headers: new Dictionary<string, string>
        {
            ["Location"] = "https://example.org/final"
        });
        handler.EnqueueResponse(System.Net.HttpStatusCode.OK, "<html><body>redirected content</body></html>", "text/html");
        using var tool = CreateTool(handler);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["url"] = "https://example.com/redirect" });

        var result = await tool.ExecuteAsync("call-1", args);

        result.Content[0].Value.ShouldContain("redirected content");
        handler.Requests.Count.ShouldBe(2);
        handler.Requests[1].RequestUri!.ToString().ShouldBe("https://example.org/final");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ExecuteAsync_RedirectLoop_IsBoundedAndBlocked()
    {
        // Every response is a redirect back to a public URL -- the hop limit must stop the loop.
        var handler = new MockHttpMessageHandler();
        handler.SetResponder((req, _) =>
        {
            var resp = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Redirect);
            resp.Headers.TryAddWithoutValidation("Location", "https://example.com/loop");
            return Task.FromResult(resp);
        });
        using var tool = CreateTool(handler);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["url"] = "https://example.com/loop" });

        var result = await tool.ExecuteAsync("call-1", args);

        result.Content[0].Value.ShouldContain("Too many redirects");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ExecuteAsync_RedirectToPrivate_AllowPrivateNetworks_IsFollowed()
    {
        // When private networks are explicitly permitted, a redirect to a private address
        // is followed (parity with the initial-URL behaviour).
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(System.Net.HttpStatusCode.Redirect, string.Empty, "text/plain", headers: new Dictionary<string, string>
        {
            ["Location"] = "http://10.0.0.5/internal"
        });
        handler.EnqueueResponse(System.Net.HttpStatusCode.OK, "<html><body>internal content</body></html>", "text/html");
        var httpClient = new HttpClient(handler);
        var config = new WebFetchConfig
        {
            MaxLengthChars = 20_000,
            TimeoutSeconds = 5,
            AllowPrivateNetworks = true
        };
        using var tool = new WebFetchTool(config, httpClient);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["url"] = "https://example.com/redirect" });

        var result = await tool.ExecuteAsync("call-1", args);

        result.Content[0].Value.ShouldContain("internal content");
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ExecuteAsync_RedirectToBlockedHost_AllowPrivateNetworks_IsStillBlocked()
    {
        // AdditionalBlockedHosts is honoured even when AllowPrivateNetworks is true.
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(System.Net.HttpStatusCode.Redirect, string.Empty, "text/plain", headers: new Dictionary<string, string>
        {
            ["Location"] = "https://blocked.corp.example/secret"
        });
        var httpClient = new HttpClient(handler);
        var config = new WebFetchConfig
        {
            MaxLengthChars = 20_000,
            TimeoutSeconds = 5,
            AllowPrivateNetworks = true,
            AdditionalBlockedHosts = ["blocked.corp.example"]
        };
        using var tool = new WebFetchTool(config, httpClient);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["url"] = "https://example.com/redirect" });

        var result = await tool.ExecuteAsync("call-1", args);

        result.Content[0].Value.ShouldContain("blocked");
        handler.Requests.Count.ShouldBe(1);
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

        result.Content[0].Value.Length.ShouldBeLessThan(750);
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

        result.Content[0].Value.ShouldContain("[No content at this offset]");
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsMetadataWithFinalUrlAndContentType()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(System.Net.HttpStatusCode.OK, "<html><body>test</body></html>", "text/html");
        using var tool = CreateTool(handler);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["url"] = "https://example.com/page" });

        var result = await tool.ExecuteAsync("call-1", args);

        var output = result.Content[0].Value;
        output.ShouldContain("\"url\":");
        output.ShouldContain("\"status\":200");
        output.ShouldContain("\"content_type\":\"text/html");
        output.ShouldContain("\"total_length\":");
        output.ShouldContain("\"has_more\":false");
    }

    [Fact]
    public async Task ExecuteAsync_WithTruncation_HasMoreIsTrue()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(System.Net.HttpStatusCode.OK, "<html><body>abcdefghijklmnopqrstuvwxyz</body></html>", "text/html");
        using var tool = CreateTool(handler);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["url"] = "https://example.com",
            ["max_length"] = 5
        });

        var result = await tool.ExecuteAsync("call-1", args);

        result.Content[0].Value.ShouldContain("\"has_more\":true");
    }

    [Fact]
    public async Task ExecuteAsync_ErrorResponse_IncludesMetadata()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(System.Net.HttpStatusCode.NotFound, "not found", "text/plain");
        using var tool = CreateTool(handler);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["url"] = "https://example.com/missing" });

        var result = await tool.ExecuteAsync("call-1", args);

        var output = result.Content[0].Value;
        output.ShouldContain("\"status\":404");
        output.ShouldContain("HTTP 404");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ExecuteAsync_WithOversizedBody_DiscardsAndReturnsBoundedError()
    {
        // A fully agent/attacker-controlled URL that streams an oversized body must be capped
        // during the read, not truncated after buffering, to prevent an OOM DoS on the gateway.
        var handler = new MockHttpMessageHandler();
        var oversized = new string('a', 4096);
        handler.EnqueueResponse(System.Net.HttpStatusCode.OK, oversized, "text/html");
        var httpClient = new HttpClient(handler);
        var config = new WebFetchConfig { MaxLengthChars = 20_000, TimeoutSeconds = 5, MaxResponseBytes = 1024 };
        using var tool = new WebFetchTool(config, httpClient);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["url"] = "https://example.com/big" });

        var result = await tool.ExecuteAsync("call-1", args);

        result.Content[0].Value.ShouldContain("exceeded");
        result.Content[0].Value.ShouldContain("1024");
        result.Content[0].Value.ShouldNotContain("aaaa");
    }

    [Fact]
    public async Task ExecuteAsync_WithBodyUnderCap_ReturnsContent()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(System.Net.HttpStatusCode.OK, "<html><body>within cap</body></html>", "text/html");
        var httpClient = new HttpClient(handler);
        var config = new WebFetchConfig { MaxLengthChars = 20_000, TimeoutSeconds = 5, MaxResponseBytes = 1024 };
        using var tool = new WebFetchTool(config, httpClient);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["url"] = "https://example.com/small" });

        var result = await tool.ExecuteAsync("call-1", args);

        result.Content[0].Value.ShouldContain("within cap");
    }

    private static WebFetchTool CreateTool(MockHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        var config = new WebFetchConfig { MaxLengthChars = 20_000, TimeoutSeconds = 5 };
        return new WebFetchTool(config, httpClient);
    }
}
