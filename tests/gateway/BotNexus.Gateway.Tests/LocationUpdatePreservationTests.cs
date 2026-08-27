using System.IO.Abstractions;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Pins that updating one field of a location leaves its other stored fields alone (#3616).
/// </summary>
/// <remarks>
/// <para>
/// <c>UpsertLocationRequest</c> models four fields; <c>LocationConfig</c> declares six. The update
/// path used to rebuild the entry from the DTO, so <c>properties</c> was deleted by an edit that
/// never mentioned it.
/// </para>
/// <para>
/// The loss was invisible from every angle a normal test looks at: the write succeeded, the
/// response was 200, and the emitted change set genuinely contained only the edited keys - because
/// the entry had already been narrowed before the differ saw it. Asserting on the response or on
/// the change set therefore proves nothing. These tests assert on the PERSISTED DOCUMENT, which is
/// the only place the loss is visible.
/// </para>
/// </remarks>
public sealed class LocationUpdatePreservationTests : IDisposable
{
    private readonly string _rootPath;

    public LocationUpdatePreservationTests()
    {
        _rootPath = Path.Combine(
            Path.GetTempPath(), "botnexus-location-preservation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);
    }

    /// <summary>
    /// The reproduction: an edit that touches only the description must not delete
    /// <c>properties</c>.
    /// </summary>
    [Fact]
    public async Task Update_ChangingOnlyDescription_PreservesProperties()
    {
        var storedPath = Path.Combine(_rootPath, "repo");
        var configPath = WriteConfig($$"""
            {
              "gateway": {
                "locations": {
                  "repo": {
                    "type": "filesystem",
                    "path": {{Serialize(storedPath)}},
                    "description": "before",
                    "properties": { "node": "pve-01", "region": "home" }
                  }
                }
              }
            }
            """);

        var (controller, _) = CreateController(configPath);

        var update = await controller.Update("repo", new UpsertLocationRequest
        {
            Name = "repo",
            Type = "filesystem",
            Value = storedPath,
            Description = "after"
        }, CancellationToken.None);

        update.Result.ShouldBeOfType<OkObjectResult>();

        var persisted = await LoadLocationAsync(configPath, "repo");
        persisted.Description.ShouldBe("after");
        persisted.Properties.ShouldNotBeNull(
            "editing a description must not delete the location's properties bag (#3616)");
        persisted.Properties!["node"].ShouldBe("pve-01");
        persisted.Properties["region"].ShouldBe("home");
    }

    /// <summary>
    /// Changing the value a location points at is also an edit that does not mention properties.
    /// </summary>
    [Fact]
    public async Task Update_ChangingPath_PreservesProperties()
    {
        var originalPath = Path.Combine(_rootPath, "repo-a");
        var updatedPath = Path.Combine(_rootPath, "repo-b");
        var configPath = WriteConfig($$"""
            {
              "gateway": {
                "locations": {
                  "repo": {
                    "type": "filesystem",
                    "path": {{Serialize(originalPath)}},
                    "properties": { "node": "pve-01" }
                  }
                }
              }
            }
            """);

        var (controller, _) = CreateController(configPath);

        await controller.Update("repo", new UpsertLocationRequest
        {
            Name = "repo",
            Type = "filesystem",
            Value = updatedPath
        }, CancellationToken.None);

        var persisted = await LoadLocationAsync(configPath, "repo");
        persisted.Path.ShouldBe(updatedPath);
        persisted.Properties.ShouldNotBeNull();
        persisted.Properties!["node"].ShouldBe("pve-01");
    }

    /// <summary>
    /// The deliberate exception to preservation: a type change must clear the previous type's value
    /// field rather than leaving it stranded beside the new one.
    /// </summary>
    /// <remarks>
    /// This is the assertion that stops "preserve everything" being implemented as a blanket merge.
    /// A filesystem location switched to <c>api</c> that kept its <c>path</c> would report two
    /// contradictory targets, and <c>ResolveStoredValue</c> prefers <c>Path</c> - so the location
    /// would silently continue resolving to the old filesystem target after being repointed at a
    /// URL.
    /// </remarks>
    [Fact]
    public async Task Update_ChangingType_ClearsThePreviousTypeValue()
    {
        var storedPath = Path.Combine(_rootPath, "repo");
        var configPath = WriteConfig($$"""
            {
              "gateway": {
                "locations": {
                  "thing": {
                    "type": "filesystem",
                    "path": {{Serialize(storedPath)}},
                    "properties": { "node": "pve-01" }
                  }
                }
              }
            }
            """);

        var (controller, _) = CreateController(configPath);

        var update = await controller.Update("thing", new UpsertLocationRequest
        {
            Name = "thing",
            Type = "api",
            Value = "https://example.invalid/api"
        }, CancellationToken.None);

        update.Result.ShouldBeOfType<OkObjectResult>();

        var persisted = await LoadLocationAsync(configPath, "thing");
        persisted.Type.ShouldBe("api");
        persisted.Endpoint.ShouldBe("https://example.invalid/api");
        persisted.Path.ShouldBeNull("a type change must not strand the previous type's value");
        persisted.Properties.ShouldNotBeNull("a type change is still not a reason to drop properties");
    }

    /// <summary>
    /// Updating one location must not disturb another. Guards the cross-entry blast radius, which
    /// is a different property from the within-entry preservation above.
    /// </summary>
    [Fact]
    public async Task Update_DoesNotDisturbSiblingLocations()
    {
        var pathA = Path.Combine(_rootPath, "a");
        var pathB = Path.Combine(_rootPath, "b");
        var configPath = WriteConfig($$"""
            {
              "gateway": {
                "locations": {
                  "alpha": {
                    "type": "filesystem",
                    "path": {{Serialize(pathA)}},
                    "properties": { "keep": "alpha-value" }
                  },
                  "beta": {
                    "type": "filesystem",
                    "path": {{Serialize(pathB)}},
                    "description": "beta stays",
                    "properties": { "keep": "beta-value" }
                  }
                }
              }
            }
            """);

        var (controller, _) = CreateController(configPath);

        await controller.Update("alpha", new UpsertLocationRequest
        {
            Name = "alpha",
            Type = "filesystem",
            Value = pathA,
            Description = "alpha edited"
        }, CancellationToken.None);

        var beta = await LoadLocationAsync(configPath, "beta");
        beta.Path.ShouldBe(pathB);
        beta.Description.ShouldBe("beta stays");
        beta.Properties.ShouldNotBeNull();
        beta.Properties!["keep"].ShouldBe("beta-value");
    }

    /// <summary>
    /// Create has nothing to preserve, so it must still produce exactly the requested entry and not
    /// inherit state from anywhere.
    /// </summary>
    [Fact]
    public async Task Create_IsUnaffected_AndCarriesNoInheritedState()
    {
        var configPath = WriteConfig("""{"gateway":{"locations":{}}}""");
        var (controller, _) = CreateController(configPath);
        var newPath = Path.Combine(_rootPath, "fresh");

        var create = await controller.Create(new UpsertLocationRequest
        {
            Name = "fresh",
            Type = "filesystem",
            Value = newPath,
            Description = "new one"
        }, CancellationToken.None);

        create.Result.ShouldBeOfType<CreatedAtActionResult>();

        var persisted = await LoadLocationAsync(configPath, "fresh");
        persisted.Path.ShouldBe(newPath);
        persisted.Description.ShouldBe("new one");
        persisted.Properties.ShouldBeNull("a created location must not acquire a properties bag");
        persisted.Endpoint.ShouldBeNull();
        persisted.ConnectionString.ShouldBeNull();
    }

    private static async Task<LocationConfig> LoadLocationAsync(string configPath, string name)
    {
        var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
        config.Gateway.ShouldNotBeNull();
        config.Gateway.Locations.ShouldNotBeNull();
        config.Gateway.Locations!.ShouldContainKey(name);
        return config.Gateway.Locations[name];
    }

    private static string Serialize(string value)
        => System.Text.Json.JsonSerializer.Serialize(value);

    private string WriteConfig(string json)
    {
        var path = Path.Combine(_rootPath, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    private static (LocationsController Controller, TestOptionsMonitor<PlatformConfig> Options) CreateController(
        string configPath)
    {
        var writer = new PlatformConfigWriter(configPath, new FileSystem());
        var configOptions = new TestOptionsMonitor<PlatformConfig>(
            PlatformConfigLoader.Load(configPath, validateOnLoad: false));

        return (new LocationsController(
            writer,
            configOptions,
            new EmptyAgentRegistry(),
            Array.Empty<IIsolationStrategy>(),
            new StubHttpClientFactory()),
            configOptions);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp directory; a leaked temp dir must not fail the suite.
        }
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
