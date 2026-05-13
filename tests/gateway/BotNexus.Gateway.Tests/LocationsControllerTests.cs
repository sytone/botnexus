using System.IO.Abstractions;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc;

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
        afterCreate.Gateway!.Locations!["repo"].Path.ShouldBe(originalPath);

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
        (badRequest.Value?.ToString() ?? string.Empty).ShouldContain("gateway.locations.gateway-api.endpoint must be a valid http or https absolute URL.");
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
        (badRequest.Value?.ToString() ?? string.Empty).ShouldContain("Location name in payload must match route name.");
    }

    [Fact]
    public async Task DatabaseLocationResponses_RedactConnectionString_ButPersistValue()
    {
        var configPath = WriteConfig("""{"gateway":{"locations":{}}}""");
        var controller = CreateController(configPath);
        const string secret = "Host=db.internal;User Id=botnexus;Password=S3cr3t!;";

        var create = await controller.Create(new UpsertLocationRequest
        {
            Name = "db-primary",
            Type = "database",
            Value = secret,
            Description = "Main database"
        }, CancellationToken.None);

        var created = create.Result.ShouldBeOfType<CreatedAtActionResult>();
        var createdResponse = created.Value.ShouldBeOfType<LocationResponse>();
        createdResponse.PathOrEndpoint.ShouldBe("(redacted)");
        createdResponse.HasConfiguredSecret.ShouldBeTrue();
        createdResponse.PathOrEndpoint.ShouldNotBe(secret);

        var get = await controller.Get("db-primary", CancellationToken.None);
        var getResponse = get.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<LocationResponse>();
        getResponse.PathOrEndpoint.ShouldBe("(redacted)");
        getResponse.HasConfiguredSecret.ShouldBeTrue();

        var list = await controller.List(CancellationToken.None);
        var listResponses = list.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeAssignableTo<IReadOnlyList<LocationResponse>>()!;
        var listedDb = listResponses.Single(location => location.Name == "db-primary");
        listedDb.PathOrEndpoint.ShouldBe("(redacted)");
        listedDb.HasConfiguredSecret.ShouldBeTrue();

        var persisted = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
        persisted.Gateway!.Locations!["db-primary"].ConnectionString.ShouldBe(secret);
    }

    [Fact]
    public async Task Update_DatabaseLocation_WithBlankValue_PreservesExistingConnectionString()
    {
        const string originalSecret = "Server=tcp:prod.example;Database=BotNexus;User Id=bot;Password=S3cr3t!;";
        var configPath = WriteConfig($$"""
            {
              "gateway": {
                "locations": {
                  "db-primary": {
                    "type": "database",
                    "connectionString": "{{originalSecret}}",
                    "description": "Initial"
                  }
                }
              }
            }
            """);
        var controller = CreateController(configPath);

        var update = await controller.Update("db-primary", new UpsertLocationRequest
        {
            Name = "db-primary",
            Type = "database",
            Value = "",
            Description = "Updated description"
        }, CancellationToken.None);

        var ok = update.Result.ShouldBeOfType<OkObjectResult>();
        var response = ok.Value.ShouldBeOfType<LocationResponse>();
        response.PathOrEndpoint.ShouldBe("(redacted)");
        response.HasConfiguredSecret.ShouldBeTrue();

        var persisted = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
        persisted.Gateway!.Locations!["db-primary"].ConnectionString.ShouldBe(originalSecret);
        persisted.Gateway.Locations["db-primary"].Description.ShouldBe("Updated description");
    }


    [Fact]
    public async Task DatabaseLocations_RedactConnectionStringInApiResponses_ButPersistInConfig()
    {
        const string secret = "Server=db.internal;Database=BotNexus;User Id=botnexus;Password=SuperSecret123!;";
        const string updatedSecret = "Server=db.internal;Database=BotNexus;User Id=botnexus;Password=EvenMoreSecret456!;";

        var configPath = WriteConfig("""{"gateway":{"locations":{}}}""");
        var controller = CreateController(configPath);

        var create = await controller.Create(new UpsertLocationRequest
        {
            Name = "db-main",
            Type = "database",
            Value = secret,
            Description = "Primary DB"
        }, CancellationToken.None);

        var createdResult = create.Result.ShouldBeOfType<CreatedAtActionResult>();
        var createdLocation = createdResult.Value.ShouldBeOfType<LocationResponse>();
        createdLocation.PathOrEndpoint.ShouldBe("(redacted)");
        createdLocation.HasConfiguredSecret.ShouldBeTrue();
        (createdLocation.PathOrEndpoint ?? string.Empty).ShouldNotContain("SuperSecret123!");

        var afterCreate = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
        afterCreate.Gateway!.Locations!["db-main"].ConnectionString.ShouldBe(secret);

        var list = await controller.List(CancellationToken.None);
        var listResult = list.Result.ShouldBeOfType<OkObjectResult>();
        var listLocations = listResult.Value.ShouldBeAssignableTo<IReadOnlyList<LocationResponse>>()!;
        var listedDb = listLocations.Single(location => location.Name == "db-main");
        listedDb.PathOrEndpoint.ShouldBe("(redacted)");
        listedDb.HasConfiguredSecret.ShouldBeTrue();
        (listedDb.PathOrEndpoint ?? string.Empty).ShouldNotContain("SuperSecret123!");

        var get = await controller.Get("db-main", CancellationToken.None);
        var getResult = get.Result.ShouldBeOfType<OkObjectResult>();
        var getLocation = getResult.Value.ShouldBeOfType<LocationResponse>();
        getLocation.PathOrEndpoint.ShouldBe("(redacted)");
        getLocation.HasConfiguredSecret.ShouldBeTrue();
        (getLocation.PathOrEndpoint ?? string.Empty).ShouldNotContain("SuperSecret123!");

        var update = await controller.Update("db-main", new UpsertLocationRequest
        {
            Name = "db-main",
            Type = "database",
            Value = updatedSecret,
            Description = "Primary DB v2"
        }, CancellationToken.None);

        var updateResult = update.Result.ShouldBeOfType<OkObjectResult>();
        var updatedLocation = updateResult.Value.ShouldBeOfType<LocationResponse>();
        updatedLocation.PathOrEndpoint.ShouldBe("(redacted)");
        updatedLocation.HasConfiguredSecret.ShouldBeTrue();
        (updatedLocation.PathOrEndpoint ?? string.Empty).ShouldNotContain("EvenMoreSecret456!");

        var afterUpdate = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
        afterUpdate.Gateway!.Locations!["db-main"].ConnectionString.ShouldBe(updatedSecret);
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
        var writer = new PlatformConfigWriter(configPath, new FileSystem());
        var agentRegistry = new EmptyAgentRegistry();
        var httpClientFactory = new StubHttpClientFactory();

        return new LocationsController(
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
