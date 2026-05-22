using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BotNexus.Gateway.Tests.Api;

/// <summary>
/// Unit tests for GET/PUT /api/config/agents/{agentId}/extensions/{extensionId}.
/// </summary>
public sealed class AgentExtensionConfigControllerTests
{
    private static AgentDefinitionConfig AgentWithExtensions(Dictionary<string, JsonElement>? extensions = null)
        => new()
        {
            DisplayName = "Test Agent",
            Model = "gpt-4o",
            Provider = "copilot",
            Extensions = extensions
        };

    private static (ConfigController controller, PlatformConfig config) BuildController(PlatformConfig platformConfig)
    {
        var monitorMock = new Mock<IOptionsMonitor<PlatformConfig>>();
        monitorMock.Setup(m => m.CurrentValue).Returns(platformConfig);

        var configMock = new Mock<IConfiguration>();
        configMock.Setup(c => c["BotNexus:ConfigPath"]).Returns((string?)null);

        // Use a real PlatformConfigWriter backed by temp file
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, "{}");
        var writer = new PlatformConfigWriter(tempFile, new System.IO.Abstractions.FileSystem());

        var controller = new ConfigController();
        return (controller, platformConfig);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  GET - "defaults" agent -> 404
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_DefaultsAgent_Returns404()
    {
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["defaults"] = new AgentDefinitionConfig { DisplayName = "D" }
            }
        };
        var monitor = CreateMonitor(config);
        var controller = new ConfigController();

        var result = await controller.GetAgentExtensionConfig(
            "defaults", "botnexus-skills", monitor, CreateConfiguration(), CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundObjectResult>();
    }

    // ──────────────────────────────────────────────────────────────────────
    //  GET - agent not in config -> 404 (falls through to fallback, also empty -> 404)
    //  Note: we test in-memory path only; fallback-to-disk path is tested via integration
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_AgentNotInMemoryConfig_ReturnsNotFound_WhenFallbackAlsoEmpty()
    {
        // Config has no agents - fallback path will read an empty temp file
        var tempPath = Path.GetTempFileName();
        File.WriteAllText(tempPath, "{}");
        try
        {
            var config = new PlatformConfig { Agents = null };
            var monitor = CreateMonitor(config);
            var configSource = CreateConfiguration(tempPath);
            var controller = new ConfigController();

            var result = await controller.GetAgentExtensionConfig(
                "ghost", "botnexus-skills", monitor, configSource, CancellationToken.None);

            result.Result.ShouldBeOfType<NotFoundObjectResult>();
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  GET - agent exists, no extensions -> 204
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_AgentExistsNoExtensions_Returns204()
    {
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["agent-a"] = AgentWithExtensions(null)
            }
        };
        var controller = new ConfigController();

        var result = await controller.GetAgentExtensionConfig(
            "agent-a", "botnexus-skills", CreateMonitor(config), CreateConfiguration(), CancellationToken.None);

        result.Result.ShouldBeOfType<NoContentResult>();
    }

    // ──────────────────────────────────────────────────────────────────────
    //  GET - extension key missing from extensions dict -> 204
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_AgentHasExtensionsButMissingKey_Returns204()
    {
        var extJson = JsonDocument.Parse("{\"other\": 1}").RootElement.Clone();
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["agent-b"] = AgentWithExtensions(new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                {
                    ["other-ext"] = extJson
                })
            }
        };
        var controller = new ConfigController();

        var result = await controller.GetAgentExtensionConfig(
            "agent-b", "botnexus-skills", CreateMonitor(config), CreateConfiguration(), CancellationToken.None);

        result.Result.ShouldBeOfType<NoContentResult>();
    }

    // ──────────────────────────────────────────────────────────────────────
    //  GET - extension config exists -> 200 with JSON body
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_AgentHasExtensionConfig_Returns200()
    {
        var extElement = JsonDocument.Parse("{\"skillsPath\":\"/custom/skills\"}").RootElement.Clone();
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["agent-c"] = AgentWithExtensions(new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                {
                    ["botnexus-skills"] = extElement
                })
            }
        };
        var controller = new ConfigController();

        var result = await controller.GetAgentExtensionConfig(
            "agent-c", "botnexus-skills", CreateMonitor(config), CreateConfiguration(), CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var node = ok.Value.ShouldBeAssignableTo<JsonNode>();
        node.ShouldNotBeNull();
        node["skillsPath"]?.GetValue<string>().ShouldBe("/custom/skills");
    }

    // ──────────────────────────────────────────────────────────────────────
    //  PUT - "defaults" agent -> 404
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Put_DefaultsAgent_Returns404()
    {
        var config = new PlatformConfig { Agents = null };
        var (writer, _tempPath) = CreateWriter("{}");
        try
        {
            var controller = new ConfigController();

            var result = await controller.PutAgentExtensionConfig(
                "defaults", "botnexus-skills", JsonNode.Parse("{\"x\":1}")!,
                CreateMonitor(config), CreateConfiguration(_tempPath), writer, CancellationToken.None);

            result.ShouldBeOfType<NotFoundObjectResult>();
        }
        finally { File.Delete(_tempPath); }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  PUT - agent not found -> 404
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Put_AgentNotFound_Returns404()
    {
        var tempPath = Path.GetTempFileName();
        File.WriteAllText(tempPath, "{}");
        var (writer, _tempPath2) = CreateWriter("{}");
        try
        {
            var config = new PlatformConfig { Agents = null };
            var controller = new ConfigController();

            var result = await controller.PutAgentExtensionConfig(
                "ghost-agent", "botnexus-skills", JsonNode.Parse("{\"x\":1}")!,
                CreateMonitor(config), CreateConfiguration(tempPath), writer, CancellationToken.None);

            result.ShouldBeOfType<NotFoundObjectResult>();
        }
        finally
        {
            File.Delete(tempPath);
            File.Delete(_tempPath2);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  PUT - agent exists -> 200, extension config written to file
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Put_AgentExists_WritesExtensionConfigAndReturns200()
    {
        var initialJson = """
        {
          "agents": {
            "agent-d": { "displayName": "D", "model": "gpt-4o", "provider": "copilot" }
          }
        }
        """;
        var (writer, tempPath) = CreateWriter(initialJson);
        try
        {
            var config = new PlatformConfig
            {
                Agents = new Dictionary<string, AgentDefinitionConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["agent-d"] = AgentWithExtensions(null)
                }
            };
            var controller = new ConfigController();
            var payload = JsonNode.Parse("{\"skillsPath\":\"/written\",\"enabled\":true}")!;

            var result = await controller.PutAgentExtensionConfig(
                "agent-d", "botnexus-skills", payload,
                CreateMonitor(config), CreateConfiguration(tempPath), writer, CancellationToken.None);

            result.ShouldBeOfType<OkObjectResult>();

            var written = File.ReadAllText(tempPath);
            var doc = JsonDocument.Parse(written);
            doc.RootElement
                .GetProperty("agents")
                .GetProperty("agent-d")
                .GetProperty("extensions")
                .GetProperty("botnexus-skills")
                .GetProperty("skillsPath")
                .GetString()
                .ShouldBe("/written");
        }
        finally { File.Delete(tempPath); }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────────────────

    private static IOptionsMonitor<PlatformConfig> CreateMonitor(PlatformConfig config)
    {
        var mock = new Mock<IOptionsMonitor<PlatformConfig>>();
        mock.Setup(m => m.CurrentValue).Returns(config);
        return mock.Object;
    }

    private static IConfiguration CreateConfiguration(string? configPath = null)
    {
        var mock = new Mock<IConfiguration>();
        mock.Setup(c => c["BotNexus:ConfigPath"]).Returns(configPath);
        return mock.Object;
    }

    private static (PlatformConfigWriter writer, string tempPath) CreateWriter(string json)
    {
        var tempPath = Path.GetTempFileName();
        File.WriteAllText(tempPath, json);
        return (new PlatformConfigWriter(tempPath, new System.IO.Abstractions.FileSystem()), tempPath);
    }
}
