using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Gateway.Api.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BotNexus.Gateway.Tests.Integration;

/// <summary>
/// Integration tests for GET /api/config/agents/{agentId}/effective (Bender's endpoint).
/// Tests run against a real WebApplicationFactory with a temp config file.
/// </summary>
[Trait("Category", "Integration")]
[Collection("IntegrationTests")]
public sealed class EffectiveAgentConfigEndpointTests : IDisposable
{
    private readonly string _tempDir;

    public EffectiveAgentConfigEndpointTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "botnexus-effective-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────────────────

    private string WriteConfig(string json)
    {
        var path = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    private static WebApplicationFactory<Program> CreateFactory(string configPath)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseUrls("http://127.0.0.1:0");
                builder.ConfigureAppConfiguration((_, cfg) =>
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["BotNexus:ConfigPath"] = configPath
                    }));
                builder.ConfigureServices(services =>
                {
                    // Remove background hosted services so tests don't need full infra
                    var toRemove = services
                        .Where(d => d.ServiceType == typeof(IHostedService))
                        .ToList();
                    foreach (var d in toRemove) services.Remove(d);
                });
            });

    private static async Task<EffectiveAgentConfigResponse> GetEffective(HttpClient client, string agentId)
    {
        var response = await client.GetAsync($"/api/config/agents/{agentId}/effective");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<EffectiveAgentConfigResponse>();
        return payload.ShouldNotBeNull();
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Scenario 1 — Known agent, no defaults → DefaultsApplied = false
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetEffectiveAgentConfig_KnownAgentNoDefaults_ReturnsFalseDefaultsApplied()
    {
        var path = WriteConfig("""
        {
          "agents": {
            "bot-alpha": {
              "displayName": "Alpha",
              "model": "gpt-4o",
              "toolIds": ["tool-a"]
            }
          }
        }
        """);

        await using var factory = CreateFactory(path);
        using var client = factory.CreateClient();

        var payload = await GetEffective(client, "bot-alpha");

        payload.AgentId.ShouldBe("bot-alpha");
        payload.DefaultsApplied.ShouldBeFalse();
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Scenario 2 — Known agent with defaults → DefaultsApplied = true, Sources populated
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetEffectiveAgentConfig_KnownAgentWithDefaults_ReturnsTrueDefaultsAppliedAndSourcesPopulated()
    {
        var path = WriteConfig("""
        {
          "agents": {
            "defaults": {
              "toolIds": ["default-tool"],
              "memory": { "enabled": true }
            },
            "bot-beta": {
              "displayName": "Beta",
              "model": "gpt-4o"
            }
          }
        }
        """);

        await using var factory = CreateFactory(path);
        using var client = factory.CreateClient();

        var payload = await GetEffective(client, "bot-beta");

        payload.DefaultsApplied.ShouldBeTrue();
        payload.Sources.ShouldNotBeEmpty();
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Scenario 3 — Agent inherits memory → sources["memory.enabled"] = "inherited"
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetEffectiveAgentConfig_AgentInheritsMemory_MemoryEnabledSourceIsInherited()
    {
        var path = WriteConfig("""
        {
          "agents": {
            "defaults": {
              "memory": { "enabled": true }
            },
            "bot-inherits-mem": {
              "displayName": "Inheritor",
              "model": "gpt-4o"
            }
          }
        }
        """);

        await using var factory = CreateFactory(path);
        using var client = factory.CreateClient();

        var payload = await GetEffective(client, "bot-inherits-mem");

        payload.Sources.ShouldContainKeyAndValue("memory.enabled", "inherited");
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Scenario 4 — Agent overrides memory → sources["memory.enabled"] = "agent"
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetEffectiveAgentConfig_AgentOverridesMemory_MemoryEnabledSourceIsAgent()
    {
        var path = WriteConfig("""
        {
          "agents": {
            "defaults": {
              "memory": { "enabled": true }
            },
            "bot-overrides-mem": {
              "displayName": "Overrider",
              "model": "gpt-4o",
              "memory": { "enabled": false }
            }
          }
        }
        """);

        await using var factory = CreateFactory(path);
        using var client = factory.CreateClient();

        var payload = await GetEffective(client, "bot-overrides-mem");

        payload.Sources.ShouldContainKeyAndValue("memory.enabled", "agent");
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Scenario 5 — Unknown agentId → 404
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetEffectiveAgentConfig_UnknownAgentId_Returns404()
    {
        var path = WriteConfig("""
        {
          "agents": {
            "bot-known": { "displayName": "Known", "model": "gpt-4o" }
          }
        }
        """);

        await using var factory = CreateFactory(path);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/config/agents/bot-unknown/effective");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Scenario 6 — "defaults" as agentId → 404 (reserved key)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetEffectiveAgentConfig_DefaultsAsAgentId_Returns404()
    {
        var path = WriteConfig("""
        {
          "agents": {
            "defaults": {
              "toolIds": ["default-tool"]
            },
            "bot-real": { "displayName": "Real", "model": "gpt-4o" }
          }
        }
        """);

        await using var factory = CreateFactory(path);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/config/agents/defaults/effective");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Scenario 7 — toolIds inherited → sources["toolIds"] = "inherited"
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetEffectiveAgentConfig_ToolIdsInherited_SourceIsInherited()
    {
        var path = WriteConfig("""
        {
          "agents": {
            "defaults": {
              "toolIds": ["default-tool-1", "default-tool-2"]
            },
            "bot-no-tools": {
              "displayName": "NoTools",
              "model": "gpt-4o"
            }
          }
        }
        """);

        await using var factory = CreateFactory(path);
        using var client = factory.CreateClient();

        var payload = await GetEffective(client, "bot-no-tools");

        payload.Sources.ShouldContainKeyAndValue("toolIds", "inherited");
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Scenario 8 — toolIds overridden → sources["toolIds"] = "agent"
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetEffectiveAgentConfig_ToolIdsOverridden_SourceIsAgent()
    {
        var path = WriteConfig("""
        {
          "agents": {
            "defaults": {
              "toolIds": ["default-tool"]
            },
            "bot-own-tools": {
              "displayName": "OwnTools",
              "model": "gpt-4o",
              "toolIds": ["custom-tool"]
            }
          }
        }
        """);

        await using var factory = CreateFactory(path);
        using var client = factory.CreateClient();

        var payload = await GetEffective(client, "bot-own-tools");

        payload.Sources.ShouldContainKeyAndValue("toolIds", "agent");
    }
}
