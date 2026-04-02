using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace BotNexus.Tests.Integration.Tests;

public class GatewayApiKeyAuthTests : IDisposable
{
    private readonly string? _previousHome;
    private readonly string _tempHome;

    public GatewayApiKeyAuthTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), $"botnexus-apikey-test-{Guid.NewGuid():N}");
        _previousHome = Environment.GetEnvironmentVariable("BOTNEXUS_HOME");
        Environment.SetEnvironmentVariable("BOTNEXUS_HOME", _tempHome);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("BOTNEXUS_HOME", _previousHome);
        try { if (Directory.Exists(_tempHome)) Directory.Delete(_tempHome, recursive: true); } catch { }
    }

    [Fact]
    public async Task ApiEndpoints_AllowRequest_WithValidHeaderApiKey()
    {
        using var factory = new GatewayApiKeyAuthFactory("test-api-key");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-api-key");

        var response = await client.GetAsync("/api/channels");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ApiEndpoints_ReturnUnauthorized_WhenApiKeyMissing()
    {
        using var factory = new GatewayApiKeyAuthFactory("test-api-key");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/channels");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadFromJsonAsync<UnauthorizedBody>();
        body.Should().NotBeNull();
        body!.Error.Should().Be("Unauthorized");
        body.Message.Should().Be("Invalid or missing API key.");
    }

    [Fact]
    public async Task ApiEndpoints_AllowUnauthenticatedRequest_WhenApiKeyNotConfigured()
    {
        using var factory = new GatewayApiKeyAuthFactory(apiKey: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/channels");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HealthEndpoint_BypassesAuthentication()
    {
        using var factory = new GatewayApiKeyAuthFactory("test-api-key");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.RootElement.TryGetProperty("checks", out _).Should().BeTrue();
    }

    [Fact]
    public async Task ReadyEndpoint_BypassesAuthentication()
    {
        using var factory = new GatewayApiKeyAuthFactory("test-api-key");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/ready");

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WebSocketEndpoint_AcceptsApiKeyFromQuery()
    {
        using var factory = new GatewayApiKeyAuthFactory("test-api-key");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/ws?apiKey=test-api-key");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed record UnauthorizedBody(string Error, string Message);

    private sealed class GatewayApiKeyAuthFactory(string? apiKey) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                var configValues = new Dictionary<string, string?>
                {
                    ["BotNexus:Gateway:ApiKey"] = apiKey
                };
                configBuilder.AddInMemoryCollection(configValues);
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
            });
        }
    }
}
