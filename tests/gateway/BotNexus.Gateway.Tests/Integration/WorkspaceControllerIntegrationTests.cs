using System.Net;
using System.Net.Http.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
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
public sealed class WorkspaceControllerIntegrationTests
{
    [Fact]
    public async Task WorkspaceEndpoints_ListAndReadFile_ReturnExpectedPayloads()
    {
        const string workspacePath = @"C:\workspace\agent-a";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Path.Combine(workspacePath, "README.md")] = new("workspace readme"),
            [Path.Combine(workspacePath, "memory", "2026-05-15.md")] = new("daily memory")
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

        var treeResponse = await client.GetAsync("/api/agents/agent-a/workspace?depth=2");
        treeResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var tree = await treeResponse.Content.ReadFromJsonAsync<WorkspaceDirectoryResponse>();
        tree.ShouldNotBeNull();
        tree!.Entries.ShouldContain(entry => entry.Path == "README.md");
        tree.Entries.ShouldContain(entry => entry.Path == "memory" && entry.Children.Any(child => child.Path == "memory/2026-05-15.md"));

        var fileResponse = await client.GetAsync("/api/agents/agent-a/workspace/README.md");
        fileResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var file = await fileResponse.Content.ReadFromJsonAsync<WorkspaceFileResponse>();
        file.ShouldNotBeNull();
        file!.Path.ShouldBe("README.md");
        file.Content.ShouldBe("workspace readme");
    }

    [Fact]
    public async Task WorkspaceFile_WhenFileMissing_ReturnsNotFound()
    {
        const string workspacePath = @"C:\workspace\agent-a";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Path.Combine(workspacePath, "README.md")] = new("workspace readme")
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

        var response = await client.GetAsync("/api/agents/agent-a/workspace/missing.txt");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task WorkspacePath_WhenPathTargetsDirectory_ReturnsDirectoryPayload()
    {
        const string workspacePath = @"C:\workspace\agent-a";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Path.Combine(workspacePath, "memory", "2026-05-15.md")] = new("daily memory")
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

        var response = await client.GetAsync("/api/agents/agent-a/workspace/memory");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<WorkspaceDirectoryResponse>();
        payload.ShouldNotBeNull();
        payload!.Type.ShouldBe("directory");
        payload.Path.ShouldBe("memory");
        payload.Entries.ShouldContain(entry => entry.Path == "memory/2026-05-15.md");
    }

    [Fact]
    public async Task WorkspaceRestClient_UsesControllerPathContract_AndReadsTruncationFlag()
    {
        const string workspacePath = @"C:\workspace\agent-a";
        var largeText = new string('a', 512 * 1024 + 32);
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Path.Combine(workspacePath, "memory", "notes.md")] = new("daily note"),
            [Path.Combine(workspacePath, "memory", "large.txt")] = new(largeText)
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
        using var http = factory.CreateClient();
        var restClient = new GatewayRestClient(http);
        restClient.Configure(new Uri(http.BaseAddress!, "api").ToString());

        var directoryResponse = await restClient.GetWorkspaceAsync("agent-a", "memory");
        directoryResponse.ShouldNotBeNull();
        directoryResponse!.Type.ShouldBe("directory");
        directoryResponse.Path.ShouldBe("memory");
        directoryResponse.Entries.ShouldNotBeNull();
        directoryResponse.Entries!.ShouldContain(entry => entry.Name == "notes.md" && entry.Type == "file");

        var fileResponse = await restClient.GetWorkspaceAsync("agent-a", "memory/large.txt");
        fileResponse.ShouldNotBeNull();
        fileResponse!.Type.ShouldBe("text");
        fileResponse.Path.ShouldBe("memory/large.txt");
        fileResponse.IsTruncated.ShouldBeTrue();
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
