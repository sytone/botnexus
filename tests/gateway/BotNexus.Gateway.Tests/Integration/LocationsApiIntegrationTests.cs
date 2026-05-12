using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BotNexus.Gateway.Tests.Integration;

[Trait("Category", "Integration")]
[Collection("IntegrationTests")]
public sealed class LocationsApiIntegrationTests
{
    private const string ConfigPathKey = "BotNexus__ConfigPath";
    private static readonly SemaphoreSlim EnvLock = new(1, 1);

    [Fact]
    public async Task LocationsApi_AddUpdateDelete_PersistsToConfigFile()
    {
        using var fixture = new LocationsFixture();
        fixture.WriteDefaultConfig("""{"gateway":{"listenUrl":"http://localhost:5005"}}""");

        await fixture.WithEnvironmentAsync(async () =>
        {
            await using var factory = CreateTestFactory();
            using var client = factory.CreateClient();

            var createResponse = await client.PostAsJsonAsync("/api/locations", new
            {
                name = "workspace",
                type = "filesystem",
                value = "Q:\\repos\\workspace",
                description = "Working tree"
            });

            createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

            var listAfterCreate = await client.GetFromJsonAsync<JsonElement>("/api/locations");
            listAfterCreate.ValueKind.ShouldBe(JsonValueKind.Array);
            listAfterCreate.EnumerateArray().Any(loc =>
                loc.GetProperty("name").GetString() == "workspace").ShouldBeTrue();

            var configAfterCreate = fixture.ReadConfigJson();
            configAfterCreate["gateway"]?["locations"]?["workspace"]?["path"]?.GetValue<string>()
                .ShouldBe("Q:\\repos\\workspace");

            var updateResponse = await client.PutAsJsonAsync("/api/locations/workspace", new
            {
                name = "workspace",
                type = "filesystem",
                value = "Q:\\repos\\workspace-updated",
                description = "Updated"
            });

            updateResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

            var configAfterUpdate = fixture.ReadConfigJson();
            configAfterUpdate["gateway"]?["locations"]?["workspace"]?["path"]?.GetValue<string>()
                .ShouldBe("Q:\\repos\\workspace-updated");
            configAfterUpdate["gateway"]?["locations"]?["workspace"]?["description"]?.GetValue<string>()
                .ShouldBe("Updated");

            var deleteResponse = await client.DeleteAsync("/api/locations/workspace");
            deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

            var configAfterDelete = fixture.ReadConfigJson();
            configAfterDelete["gateway"]?["locations"]?["workspace"].ShouldBeNull();
        });
    }

    [Fact]
    public async Task LocationsApi_RuntimeEndpointsReflectChangesWithoutRestart()
    {
        using var fixture = new LocationsFixture();
        fixture.WriteDefaultConfig("""{"gateway":{"listenUrl":"http://localhost:5005"}}""");

        await fixture.WithEnvironmentAsync(async () =>
        {
            await using var factory = CreateTestFactory();
            using var client = factory.CreateClient();

            var beforeList = await client.GetFromJsonAsync<JsonElement>("/api/locations");
            beforeList.EnumerateArray()
                .Any(loc => string.Equals(loc.GetProperty("name").GetString(), "hot-reload", StringComparison.Ordinal))
                .ShouldBeFalse();

            var createResponse = await client.PostAsJsonAsync("/api/locations", new
            {
                name = "hot-reload",
                type = "filesystem",
                value = "Q:\\repos\\hot-reload"
            });
            createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

            var getByName = await client.GetFromJsonAsync<JsonElement>("/api/locations/hot-reload");
            getByName.GetProperty("name").GetString().ShouldBe("hot-reload");

            var afterList = await client.GetFromJsonAsync<JsonElement>("/api/locations");
            afterList.EnumerateArray()
                .Any(loc => string.Equals(loc.GetProperty("name").GetString(), "hot-reload", StringComparison.Ordinal))
                .ShouldBeTrue();
        });
    }

    [Fact]
    public async Task LocationsApi_InvalidAndDuplicateRequests_ReturnActionableErrors()
    {
        using var fixture = new LocationsFixture();
        fixture.WriteDefaultConfig("""{"gateway":{"listenUrl":"http://localhost:5005"}}""");

        await fixture.WithEnvironmentAsync(async () =>
        {
            await using var factory = CreateTestFactory();
            using var client = factory.CreateClient();

            var invalidResponse = await client.PostAsJsonAsync("/api/locations", new
            {
                name = "broken",
                type = "filesystem",
                value = ""
            });

            invalidResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var invalidBody = await invalidResponse.Content.ReadFromJsonAsync<JsonElement>();
            invalidBody.GetProperty("error").GetString().ShouldContain("required");

            var firstCreate = await client.PostAsJsonAsync("/api/locations", new
            {
                name = "duplicate",
                type = "filesystem",
                value = "Q:\\repos\\dupe"
            });
            firstCreate.StatusCode.ShouldBe(HttpStatusCode.Created);

            var duplicateCreate = await client.PostAsJsonAsync("/api/locations", new
            {
                name = "duplicate",
                type = "filesystem",
                value = "Q:\\repos\\dupe2"
            });

            duplicateCreate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            var duplicateBody = await duplicateCreate.Content.ReadFromJsonAsync<JsonElement>();
            duplicateBody.GetProperty("error").GetString().ShouldContain("already exists");
        });
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
                        .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
                        .ToList();

                    foreach (var descriptor in hostedServices)
                        services.Remove(descriptor);
                });
            });

    private sealed class LocationsFixture : IDisposable
    {
        private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "botnexus-locations-tests", Guid.NewGuid().ToString("N"));
        private readonly string _homeOverrideBefore = Environment.GetEnvironmentVariable(BotNexusHome.HomeOverrideEnvVar) ?? string.Empty;
        private readonly string _configPathOverrideBefore = Environment.GetEnvironmentVariable(ConfigPathKey) ?? string.Empty;

        public LocationsFixture()
        {
            Directory.CreateDirectory(_rootPath);
        }

        public void WriteDefaultConfig(string json)
            => File.WriteAllText(Path.Combine(_rootPath, "config.json"), json);

        public JsonObject ReadConfigJson()
        {
            var path = Path.Combine(_rootPath, "config.json");
            var text = File.ReadAllText(path);
            return JsonNode.Parse(text)?.AsObject() ?? new JsonObject();
        }

        public async Task WithEnvironmentAsync(Func<Task> action)
        {
            await EnvLock.WaitAsync();
            try
            {
                Environment.SetEnvironmentVariable(BotNexusHome.HomeOverrideEnvVar, _rootPath);
                Environment.SetEnvironmentVariable(ConfigPathKey, null);
                await action();
            }
            finally
            {
                Environment.SetEnvironmentVariable(BotNexusHome.HomeOverrideEnvVar, string.IsNullOrWhiteSpace(_homeOverrideBefore) ? null : _homeOverrideBefore);
                Environment.SetEnvironmentVariable(ConfigPathKey, string.IsNullOrWhiteSpace(_configPathOverrideBefore) ? null : _configPathOverrideBefore);
                EnvLock.Release();
            }
        }

        public void Dispose()
        {
            if (!Directory.Exists(_rootPath))
                return;

            try
            {
                Directory.Delete(_rootPath, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
