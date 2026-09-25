using System.IO.Compression;
using System.Net;
using BotNexus.Extensions.Channels.SignalR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Gateway.Tests.Channels;

/// <summary>
/// Exercises the real SignalR portal middleware so client-route fallback cannot drift from
/// the static index response's representation, caching, or conditional-request behavior.
/// </summary>
public sealed class SignalRBlazorDeepLinkMiddlewareTests : IAsyncLifetime
{
    private const string DesktopHtml = "<html><base href=\"/\">desktop-shell</html>";
    private const string MobileHtml = "<html><base href=\"/mobile/\">mobile-shell</html>";

    private string _root = string.Empty;
    private WebApplication? _app;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _root = Directory.CreateTempSubdirectory("bn-deep-link-").FullName;
        var desktop = Path.Combine(_root, "desktop");
        var mobile = Path.Combine(_root, "mobile");
        await CreateSurfaceAsync(desktop, DesktopHtml);
        await CreateSurfaceAsync(mobile, MobileHtml);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        _app = builder.Build();

        SignalREndpointContributor.MapBlazorApp(_app, desktop, pathPrefix: null);
        SignalREndpointContributor.MapBlazorApp(_app, mobile, pathPrefix: "/mobile");
        _app.Run(context =>
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });

        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        if (_app is not null)
            await _app.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Theory]
    [InlineData("/", "/agents", DesktopHtml, "br", "br")]
    [InlineData("/", "/agents", DesktopHtml, "gzip", "gzip")]
    [InlineData("/", "/agents", DesktopHtml, null, null)]
    [InlineData("/mobile/", "/mobile/settings", MobileHtml, "br", "br")]
    [InlineData("/mobile/", "/mobile/settings", MobileHtml, "gzip", "gzip")]
    [InlineData("/mobile/", "/mobile/settings", MobileHtml, null, null)]
    public async Task ClientRoute_UsesRootIndexRepresentationAndValidator(
        string rootPath,
        string deepLinkPath,
        string expectedHtml,
        string? acceptEncoding,
        string? expectedEncoding)
    {
        using var root = await SendAsync(rootPath, acceptEncoding);
        using var deepLink = await SendAsync(deepLinkPath, acceptEncoding);

        root.StatusCode.ShouldBe(HttpStatusCode.OK);
        deepLink.StatusCode.ShouldBe(HttpStatusCode.OK);
        deepLink.Headers.CacheControl?.ToString().ShouldBe("no-cache");
        deepLink.Headers.Vary.ShouldContain("Accept-Encoding");
        deepLink.Content.Headers.ContentEncoding.SingleOrDefault().ShouldBe(expectedEncoding);
        (await DecodeAsync(deepLink)).ShouldBe(expectedHtml);
        (await DecodeAsync(root)).ShouldBe(expectedHtml);

        var etag = deepLink.Headers.ETag?.ToString();
        etag.ShouldNotBeNullOrWhiteSpace();
        etag.ShouldBe(root.Headers.ETag?.ToString());

        using var conditional = await SendAsync(deepLinkPath, acceptEncoding, etag);
        conditional.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        (await conditional.Content.ReadAsByteArrayAsync()).ShouldBeEmpty();
        conditional.Content.Headers.ContentEncoding.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/api/agents")]
    [InlineData("/hub/gateway")]
    [InlineData("/health")]
    [InlineData("/mobile/missing.js")]
    [InlineData("/missing.css")]
    public async Task NonClientRoute_IsNotRewrittenToIndex(string path)
    {
        using var response = await SendAsync(path, "br");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Headers.ETag.ShouldBeNull();
        response.Headers.CacheControl.ShouldBeNull();
    }

    [Fact]
    public async Task FingerprintedAsset_RemainsImmutableThroughRealMiddleware()
    {
        using var response = await SendAsync("/_framework/app.dxsi8fk310.js", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl?.ToString().ShouldBe("public, max-age=31536000, immutable");
        response.Headers.ETag.ShouldBeNull();
        (await response.Content.ReadAsStringAsync()).ShouldBe("console.log('immutable');");
    }

    private async Task<HttpResponseMessage> SendAsync(
        string path,
        string? acceptEncoding,
        string? ifNoneMatch = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (acceptEncoding is not null)
            request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        if (ifNoneMatch is not null)
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        return await _client.SendAsync(request);
    }

    private static async Task CreateSurfaceAsync(string path, string html)
    {
        Directory.CreateDirectory(path);
        var bytes = System.Text.Encoding.UTF8.GetBytes(html);
        await File.WriteAllBytesAsync(Path.Combine(path, "index.html"), bytes);
        await WriteCompressedAsync(Path.Combine(path, "index.html.br"), bytes, brotli: true);
        await WriteCompressedAsync(Path.Combine(path, "index.html.gz"), bytes, brotli: false);

        var framework = Directory.CreateDirectory(Path.Combine(path, "_framework"));
        await File.WriteAllTextAsync(
            Path.Combine(framework.FullName, "app.dxsi8fk310.js"),
            "console.log('immutable');");
    }

    private static async Task WriteCompressedAsync(string path, byte[] bytes, bool brotli)
    {
        await using var output = File.Create(path);
        await using Stream compressor = brotli
            ? new BrotliStream(output, CompressionLevel.SmallestSize)
            : new GZipStream(output, CompressionLevel.SmallestSize);
        await compressor.WriteAsync(bytes);
    }

    private static async Task<string> DecodeAsync(HttpResponseMessage response)
    {
        await using var source = await response.Content.ReadAsStreamAsync();
        await using Stream decoded = response.Content.Headers.ContentEncoding.SingleOrDefault() switch
        {
            "br" => new BrotliStream(source, CompressionMode.Decompress),
            "gzip" => new GZipStream(source, CompressionMode.Decompress),
            _ => source
        };
        using var reader = new StreamReader(decoded);
        return await reader.ReadToEndAsync();
    }
}
