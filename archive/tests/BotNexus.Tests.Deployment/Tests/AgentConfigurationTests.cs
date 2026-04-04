using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Tests.Deployment.Infrastructure;

namespace BotNexus.Tests.Deployment.Tests;

/// <summary>SC-DPL-003: Configure agents in config.json → verify agents visible via API.</summary>
[Trait("Category", "Deployment")]
public sealed class AgentConfigurationTests
{
    [Fact]
    public async Task ConfiguredAgents_AppearInApiWithCorrectSettings()
    {
        var port = GatewayProcessFixture.FindFreePort();
        await using var gw = new GatewayProcessFixture(port);

        // Configure two named agents via config.json
        gw.WriteAppSettings(GatewayProcessFixture.DefaultAppSettings(port));
        gw.WriteConfigJson("""
            {
              "BotNexus": {
                "Agents": {
                  "Named": {
                    "alpha": { "Name": "alpha", "SystemPrompt": "You are Alpha, a research agent." },
                    "beta": { "Name": "beta", "SystemPrompt": "You are Beta, a coding agent." }
                  }
                }
              }
            }
            """);

        await gw.StartAsync();
        await gw.WaitForHealthyAsync();

        using var client = gw.CreateHttpClient();

        // Verify /api/agents returns both agents (plus "default")
        var agentsResponse = await client.GetAsync("/api/agents");
        agentsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var agents = await agentsResponse.Content.ReadFromJsonAsync<JsonElement>();

        agents.ValueKind.Should().Be(JsonValueKind.Array);
        var agentNames = agents.EnumerateArray()
            .Select(a => a.GetProperty("name").GetString())
            .ToList();

        agentNames.Should().Contain("alpha");
        agentNames.Should().Contain("beta");
        agentNames.Count.Should().Be(2, "should have 2 configured agents (no phantom 'default' when named agents exist)");
    }
}
