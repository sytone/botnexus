using System.Net;
using System.Text.Json;
using BotNexus.Gateway.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace BotNexus.Gateway.Tests.Api;

public sealed class GatewayBuildInfoTests
{
    [Fact]
    public void StartedAt_IsNotDefault()
    {
        GatewayBuildInfo.StartedAt.ShouldNotBe(default(DateTimeOffset));
        GatewayBuildInfo.StartedAt.ShouldBeGreaterThan(DateTimeOffset.MinValue);
    }

    [Fact]
    public void CommitShort_IsSevenCharsOrFewer()
    {
        GatewayBuildInfo.CommitShort.Length.ShouldBeLessThanOrEqualTo(7);
    }

    [Fact]
    public void CommitSha_IsNotNullOrEmpty()
    {
        GatewayBuildInfo.CommitSha.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Version_IsNotNullOrEmpty()
    {
        GatewayBuildInfo.Version.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task InfoEndpoint_Returns200WithExpectedFields()
    {
        await using var factory = CreateTestFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/gateway/info");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;

        root.TryGetProperty("startedAt", out _).ShouldBeTrue();
        root.TryGetProperty("uptimeSeconds", out _).ShouldBeTrue();
        root.TryGetProperty("commitSha", out var commitSha).ShouldBeTrue();
        root.TryGetProperty("commitShort", out var commitShort).ShouldBeTrue();
        root.TryGetProperty("version", out _).ShouldBeTrue();

        commitSha.GetString().ShouldNotBeNullOrWhiteSpace();
        commitShort.GetString()!.Length.ShouldBeLessThanOrEqualTo(7);
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
