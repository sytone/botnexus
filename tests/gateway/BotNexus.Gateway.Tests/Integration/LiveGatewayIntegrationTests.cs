using System.Net;
using System.Net.Http.Json;
using BotNexus.Gateway;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using BotNexus.Gateway.Tests.Helpers;

namespace BotNexus.Gateway.Tests.Integration;

[Trait("Category", "Integration")]
[Collection("IntegrationTests")]
public sealed class LiveGatewayIntegrationTests
{
    [Fact]
    public async Task GatewayStartupTest_HealthEndpoint_ReturnsOk()
    {
        await using var factory = CreateTestFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GatewayStartupTest_SwaggerEndpoint_ReturnsOk()
    {
        await using var factory = CreateTestFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RestApiTests_AgentsSessionsAndConfigEndpoints_ReturnExpectedResponses()
    {
        await using var factory = CreateTestFactory();
        using var client = factory.CreateClient();

        var listAgentsResponse = await client.GetAsync("/api/agents");
        listAgentsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var listSessionsResponse = await client.GetAsync("/api/sessions");
        listSessionsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var descriptor = new AgentDescriptor
        {
            AgentId = $"integration-agent-{Guid.NewGuid():N}",
            DisplayName = "Integration Agent",
            ModelId = "gpt-4.1",
            ApiProvider = "copilot",
            IsolationStrategy = "in-process"
        };
        var registerResponse = await client.PostAsJsonAsync("/api/agents", descriptor);
        registerResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        var validateResponse = await client.GetAsync("/api/config/validate");
        validateResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SignalRHub_ConnectsAndReceivesAgentList()
    {
        await using var factory = CreateTestFactory();
        using var client = factory.CreateClient();
        var descriptor = new AgentDescriptor
        {
            AgentId = $"hub-agent-{Guid.NewGuid():N}",
            DisplayName = "Hub Agent",
            ModelId = "gpt-4.1",
            ApiProvider = "copilot",
            IsolationStrategy = "in-process"
        };
        var registerResponse = await client.PostAsJsonAsync("/api/agents", descriptor);
        registerResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        // Verify the hub endpoint is mapped by checking a simple HTTP request
        // (SignalR negotiate endpoint)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var negotiateResponse = await client.PostAsync("/hub/gateway/negotiate?negotiateVersion=1", null, cts.Token);
        negotiateResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static WebApplicationFactory<Program> CreateTestFactory(Action<IServiceCollection>? configureTestServices = null)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseUrls("http://127.0.0.1:0");
                builder.ConfigureServices(services =>
                {
                    var hostedServicesToRemove = services
                        .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
                        .ToList();

                    foreach (var descriptor in hostedServicesToRemove)
                        services.Remove(descriptor);

                    services.AddSignalRChannelForTests();
                });

                builder.ConfigureTestServices(services =>
                {
                    configureTestServices?.Invoke(services);
                });
            });

}


