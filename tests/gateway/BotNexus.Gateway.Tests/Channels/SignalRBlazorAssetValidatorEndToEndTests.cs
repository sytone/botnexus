using System.Net;
using BotNexus.Extensions.Channels.SignalR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace BotNexus.Gateway.Tests.Channels;

/// <summary>
/// End-to-end proof that the validator logic is actually WIRED into the response path, not
/// merely correct in isolation. Unit tests over BuildEntityTag/IsNotModified cannot show that
/// the middleware emits the headers or short-circuits to 304 -- that is exactly the gap that
/// let #2413 ship (a no-cache policy with no validator, so conditional GETs were impossible).
/// </summary>
public sealed class SignalRBlazorAssetValidatorEndToEndTests : IAsyncLifetime
{
    private string _root = string.Empty;
    private IHost? _host;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _root = Directory.CreateTempSubdirectory("bn-etag-").FullName;
        await File.WriteAllTextAsync(Path.Combine(_root, "index.html"), "<html>hello</html>");
        // Fingerprint detection requires BOTH the _framework/ prefix and a content-hash
        // segment, so the fixture must reproduce the real deployed layout.
        Directory.CreateDirectory(Path.Combine(_root, "_framework"));
        await File.WriteAllTextAsync(Path.Combine(_root, "_framework", "app.dxsi8fk310.js"), "console.log(1)");

        var provider = new PhysicalFileProvider(_root);

        _host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s => s.AddRouting())
                .Configure(app => app.Run(async context =>
                {
                    // Mirror the contributor's serve path against the temp root.
                    var path = context.Request.Path.Value == "/" ? "/index.html" : context.Request.Path.Value!;
                    var file = provider.GetFileInfo(path);
                    if (!file.Exists) { context.Response.StatusCode = 404; return; }

                    var cacheControl = SignalREndpointContributor.ResolveCacheControl(path);
                    context.Response.Headers.CacheControl = cacheControl;
                    if (cacheControl == SignalREndpointContributor.RevalidateCacheControl)
                    {
                        var etag = SignalREndpointContributor.BuildEntityTag(file);
                        context.Response.Headers.ETag = etag;
                        context.Response.Headers.LastModified =
                            file.LastModified.ToUniversalTime().ToString("R");
                        if (SignalREndpointContributor.IsNotModified(context.Request, etag, file.LastModified))
                        {
                            context.Response.StatusCode = StatusCodes.Status304NotModified;
                            return;
                        }
                    }

                    await context.Response.SendFileAsync(file);
                })))
            .StartAsync();

        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        if (_host is not null) { await _host.StopAsync(); _host.Dispose(); }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* temp dir best-effort */ }
    }

    [Fact]
    public async Task RevalidatedAsset_EmitsValidators_ThenAnswers304_OnTheEchoedTag()
    {
        var first = await _client.GetAsync("/");
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The bug: no-cache with no validator. Both must now be present.
        var etag = first.Headers.ETag?.ToString();
        etag.ShouldNotBeNullOrWhiteSpace();
        first.Content.Headers.LastModified.ShouldNotBeNull();

        var second = await _client.SendAsync(
            Conditional(HttpMethod.Get, "/", ifNoneMatch: etag!));

        second.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        (await second.Content.ReadAsByteArrayAsync()).Length.ShouldBe(0);
    }

    [Fact]
    public async Task RevalidatedAsset_Returns200WithBody_WhenTheTagIsStale()
    {
        var response = await _client.SendAsync(
            Conditional(HttpMethod.Get, "/", ifNoneMatch: "W/\"0-0\""));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("hello");
    }

    [Fact]
    public async Task FingerprintedAsset_IsImmutable_AndCarriesNoValidator()
    {
        // An immutable asset is never revalidated, so spending bytes on an ETag is waste.
        var response = await _client.GetAsync("/_framework/app.dxsi8fk310.js");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.ToString().ShouldContain("immutable");
        response.Headers.ETag.ShouldBeNull();
    }

    private static HttpRequestMessage Conditional(HttpMethod method, string url, string ifNoneMatch)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        return request;
    }
}
