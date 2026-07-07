using System.Reflection;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Domain.Primitives;
using BotNexus.Extensions.WebTools.Search;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Extensions.WebTools.Tests;

/// <summary>
/// Verifies that <see cref="WebToolsContributor"/> consumes the resolved Copilot MCP endpoint
/// seam (<see cref="AgentToolContributionContext.CopilotMcpEndpoint"/>) rather than re-deriving
/// it from a raw provider-endpoint override (#1797).
/// </summary>
[Trait("Category", "Unit")]
public class WebToolsContributorTests
{
    [Fact]
    public async Task ContributeAsync_CopilotProvider_FlowsResolvedEnterpriseEndpointToWebSearchTool()
    {
        const string enterpriseEndpoint = "https://api.enterprise.githubcopilot.com/mcp";
        var context = BuildContext(searchProvider: "copilot", copilotMcpEndpoint: enterpriseEndpoint);

        var contribution = await new WebToolsContributor().ContributeAsync(context);

        var searchTool = contribution.Tools.OfType<WebSearchTool>().ShouldHaveSingleItem();
        GetCopilotEndpoint(searchTool).ShouldBe(enterpriseEndpoint);
    }

    [Fact]
    public async Task ContributeAsync_CopilotProvider_FlowsIndividualFallbackEndpointToWebSearchTool()
    {
        const string individualEndpoint = "https://api.githubcopilot.com/mcp";
        var context = BuildContext(searchProvider: "copilot", copilotMcpEndpoint: individualEndpoint);

        var contribution = await new WebToolsContributor().ContributeAsync(context);

        var searchTool = contribution.Tools.OfType<WebSearchTool>().ShouldHaveSingleItem();
        GetCopilotEndpoint(searchTool).ShouldBe(individualEndpoint);
    }

    [Fact]
    public async Task ContributeAsync_NonCopilotProvider_DoesNotStampCopilotEndpoint()
    {
        var context = BuildContext(
            searchProvider: "brave",
            apiKey: "token",
            copilotMcpEndpoint: "https://api.enterprise.githubcopilot.com/mcp");

        var contribution = await new WebToolsContributor().ContributeAsync(context);

        var searchTool = contribution.Tools.OfType<WebSearchTool>().ShouldHaveSingleItem();
        GetCopilotEndpoint(searchTool).ShouldBeNull();
    }

    private static string? GetCopilotEndpoint(WebSearchTool tool)
    {
        var field = typeof(WebSearchTool).GetField("_copilotApiEndpoint", BindingFlags.Instance | BindingFlags.NonPublic);
        return (string?)field!.GetValue(tool);
    }

    private static AgentToolContributionContext BuildContext(
        string searchProvider,
        string? apiKey = null,
        string? copilotMcpEndpoint = null)
    {
        var searchJson = apiKey is null
            ? "{\"search\":{\"provider\":\"" + searchProvider + "\"}}"
            : "{\"search\":{\"provider\":\"" + searchProvider + "\",\"apiKey\":\"" + apiKey + "\"}}";

        var extensionConfig = new Dictionary<string, JsonElement>
        {
            ["botnexus-web"] = JsonDocument.Parse(searchJson).RootElement
        };

        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("test-agent"),
            DisplayName = "Test Agent",
            ModelId = "claude-opus-4.5",
            ApiProvider = "github-copilot",
            ExtensionConfig = extensionConfig
        };

        return new AgentToolContributionContext(
            descriptor,
            new AgentExecutionContext { SessionId = SessionId.Create() },
            Path.Combine(Path.GetTempPath(), "webtools-contributor-tests"),
            new AllowAllPathValidator(),
            copilotMcpEndpoint,
            (_, _) => Task.FromResult<string?>("copilot-token"));
    }

    private sealed class AllowAllPathValidator : IPathValidator
    {
        public bool CanRead(string absolutePath) => true;
        public bool CanWrite(string absolutePath) => true;
        public string? ValidateAndResolve(string rawPath, FileAccessMode mode) => rawPath;
    }
}
