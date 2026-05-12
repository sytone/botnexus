using System.IO.Abstractions;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace BotNexus.Gateway.Tests;

public sealed class LocationsControllerTests : IDisposable
{
    private readonly string _rootPath;

    public LocationsControllerTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "botnexus-locations-controller-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);
    }

    [Fact]
    public async Task CreateUpdateDelete_PersistsLocationsInConfig()
    {
        var configPath = WriteConfig("""{"gateway":{"locations":{}}}""");
        var controller = CreateController(configPath);
        var originalPath = Path.Combine(_rootPath, "repo-a");
        var updatedPath = Path.Combine(_rootPath, "repo-b");

        var create = await controller.Create(new UpsertLocationRequest
        {
            Name = "repo",
            Type = "filesystem",
            Value = originalPath,
            Description = "Repository"
        }, CancellationToken.None);
        create.Result.ShouldBeOfType<CreatedAtActionResult>();

        var afterCreate = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
        afterCreate.Gateway!.Locations!.ShouldContainKey("repo");
        afterCreate.Gateway.Locations["repo"].Path.ShouldBe(originalPath);

        var update = await controller.Update("repo", new UpsertLocationRequest
        {
            Name = "repo",
            Type = "filesystem",
            Value = updatedPath,
            Description = "Repository v2"
        }, CancellationToken.None);
        update.Result.ShouldBeOfType<OkObjectResult>();

        var afterUpdate = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
        afterUpdate.Gateway!.Locations!["repo"].Path.ShouldBe(updatedPath);
        afterUpdate.Gateway.Locations["repo"].Description.ShouldBe("Repository v2");

        var delete = await controller.Delete("repo", CancellationToken.None);
        delete.ShouldBeOfType<NoContentResult>();

        var afterDelete = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
        afterDelete.Gateway?.Locations?.ContainsKey("repo").ShouldBeFalse();
    }

    [Fact]
    public async Task Create_WhenDuplicateLocationNameExists_IgnoresCaseAndReturnsConflict()
    {
        var configPath = WriteConfig("""
            {
              "gateway": {
                "locations": {
                  "Repo": {
                    "type": "filesystem",
                    "path": "C:\\repos\\botnexus"
                  }
                }
              }
            }
            """);
        var controller = CreateController(configPath);

        var result = await controller.Create(new UpsertLocationRequest
        {
            Name = "repo",
            Type = "filesystem",
            Value = Path.Combine(_rootPath, "another")
        }, CancellationToken.None);

        result.Result.ShouldBeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Create_WhenApiEndpointIsInvalid_ReturnsBadRequestWithValidationMessage()
    {
        var configPath = WriteConfig("""{"gateway":{"locations":{}}}""");
        var controller = CreateController(configPath);

        var result = await controller.Create(new UpsertLocationRequest
        {
            Name = "gateway-api",
            Type = "api",
            Value = "ftp://example.test"
        }, CancellationToken.None);

        var badRequest = result.Result.ShouldBeOfType<BadRequestObjectResult>();
        badRequest.Value.ShouldNotBeNull();
        badRequest.Value!.ToString().ShouldContain("gateway.locations.gateway-api.endpoint must be a valid http or https absolute URL.");
    }

    [Fact]
    public async Task Update_WhenPayloadNameDiffersFromRoute_ReturnsBadRequest()
    {
        var configPath = WriteConfig("""{"gateway":{"locations":{"repo":{"type":"filesystem","path":"C:\\repos\\botnexus"}}}}""");
        var controller = CreateController(configPath);

        var result = await controller.Update("repo", new UpsertLocationRequest
        {
            Name = "other",
            Type = "filesystem",
            Value = Path.Combine(_rootPath, "updated")
        }, CancellationToken.None);

        var badRequest = result.Result.ShouldBeOfType<BadRequestObjectResult>();
        badRequest.Value.ShouldNotBeNull();
        badRequest.Value!.ToString().ShouldContain("Location name in payload must match route name.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    private string WriteConfig(string json)
    {
        var path = Path.Combine(_rootPath, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    private static LocationsController CreateController(string configPath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotNexus:ConfigPath"] = configPath
            })
            .Build();

        var writer = new PlatformConfigWriter(configPath, new FileSystem());
        var agentRegistry = new EmptyAgentRegistry();
        var httpClientFactory = new StubHttpClientFactory();

        return new LocationsController(
            configuration,
            writer,
            agentRegistry,
            Array.Empty<IIsolationStrategy>(),
            httpClientFactory);
    }

    private sealed class EmptyAgentRegistry : IAgentRegistry
    {
        public void Register(AgentDescriptor descriptor) { }
        public void Unregister(AgentId agentId) { }
        public bool Update(AgentId agentId, AgentDescriptor descriptor) => false;
        public AgentDescriptor? Get(AgentId agentId) => null;
        public IReadOnlyList<AgentDescriptor> GetAll() => [];
        public bool Contains(AgentId agentId) => false;
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
