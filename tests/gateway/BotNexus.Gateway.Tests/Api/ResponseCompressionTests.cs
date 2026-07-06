using System.Net;
using System.Net.Http.Headers;
using BotNexus.Gateway.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace BotNexus.Gateway.Tests.Api;

/// <summary>
/// Verifies dynamic JSON API responses are compressed with Brotli/Gzip when the
/// client advertises support via Accept-Encoding, and that the middleware emits
/// <c>Vary: Accept-Encoding</c> and leaves requests without Accept-Encoding
/// uncompressed (identity). Covers issue #1781.
/// </summary>
public sealed class ResponseCompressionTests
{
    [Fact]
    public async Task JsonEndpoint_WithAcceptEncodingBrotli_ReturnsBrotliEncoded()
    {
        await using var factory = CreateTestFactory();
        // Disable automatic decompression so we can observe the raw Content-Encoding header.
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/version");
        request.Headers.AcceptEncoding.Clear();
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));

        using var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        response.Content.Headers.ContentEncoding.ShouldContain("br");
        response.Headers.Vary.ShouldContain("Accept-Encoding");
    }

    [Fact]
    public async Task JsonEndpoint_WithoutAcceptEncoding_ReturnsIdentity()
    {
        await using var factory = CreateTestFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/version");
        request.Headers.AcceptEncoding.Clear();

        using var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        response.Content.Headers.ContentEncoding.ShouldBeEmpty();
    }

    private static WebApplicationFactory<Program> CreateTestFactory()
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseUrls("http://127.0.0.1:0");
                builder.ConfigureServices(services =>
                {
                    var hostedServices = services
                        .Where(d => d.ServiceType == typeof(IHostedService))
                        .ToList();
                    foreach (var descriptor in hostedServices)
                        services.Remove(descriptor);
                });
            });
}
