using System.Net;
using System.Net.Http.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Api.Models;
using BotNexus.Gateway.Agents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Tests.Integration;

[Trait("Category", "Integration")]
[Collection("IntegrationTests")]
public sealed class ReportsControllerIntegrationTests
{
    [Fact]
    public async Task ReportsEndpoints_ListAndReadMarkdownReport()
    {
        const string workspacePath = @"C:\workspace\agent-a";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Path.Combine(workspacePath, "reports", "daily.md")] = new("daily report"),
            [Path.Combine(workspacePath, "reports", "notes.txt")] = new("ignore")
        });

        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(new AgentDescriptor
        {
            AgentId = AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "gpt-4.1",
            ApiProvider = "openai"
        });

        var workspaceManager = new StubWorkspaceManager(workspacePath);

        await using var factory = CreateTestFactory(registry, workspaceManager, fileSystem);
        using var client = factory.CreateClient();

        var listResponse = await client.GetAsync("/api/agents/agent-a/reports");
        listResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var listPayload = await listResponse.Content.ReadFromJsonAsync<ReportsListResponse>();
        listPayload.ShouldNotBeNull();
        listPayload!.Reports.Select(report => report.Name).ShouldBe(["daily.md"]);

        var fileResponse = await client.GetAsync("/api/agents/agent-a/reports/daily.md");
        fileResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var filePayload = await fileResponse.Content.ReadFromJsonAsync<ReportContentResponse>();
        filePayload.ShouldNotBeNull();
        filePayload!.Name.ShouldBe("daily.md");
        filePayload.Content.ShouldBe("daily report");
    }

    [Fact]
    public async Task ReportsList_WhenDirectoryMissing_ReturnsEmptyList()
    {
        const string workspacePath = @"C:\workspace\agent-a";
        var fileSystem = new MockFileSystem();

        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(new AgentDescriptor
        {
            AgentId = AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "gpt-4.1",
            ApiProvider = "openai"
        });

        var workspaceManager = new StubWorkspaceManager(workspacePath);

        await using var factory = CreateTestFactory(registry, workspaceManager, fileSystem);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/agents/agent-a/reports");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<ReportsListResponse>();
        payload.ShouldNotBeNull();
        payload!.Reports.ShouldBeEmpty();
    }

    private static WebApplicationFactory<Program> CreateTestFactory(
        IAgentRegistry registry,
        IAgentWorkspaceManager workspaceManager,
        IFileSystem fileSystem)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseUrls("http://127.0.0.1:0");
                builder.ConfigureServices(services =>
                {
                    var hostedServices = services
                        .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
                        .ToList();
                    foreach (var descriptor in hostedServices)
                        services.Remove(descriptor);
                });

                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IAgentRegistry>();
                    services.RemoveAll<IAgentWorkspaceManager>();
                    services.RemoveAll<IFileSystem>();

                    services.AddSingleton(registry);
                    services.AddSingleton(workspaceManager);
                    services.AddSingleton(fileSystem);
                });
            });

    private sealed class StubWorkspaceManager(string workspacePath) : IAgentWorkspaceManager
    {
        public Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SaveMemoryAsync(string agentName, string content, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SaveMemoryAsync(string agentName, string? filePath, string content, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SaveMemoryAsync(string agentName, string? filePath, string content, string? memoryPathOverride, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public string GetWorkspacePath(string agentName) => workspacePath;
    }
}
