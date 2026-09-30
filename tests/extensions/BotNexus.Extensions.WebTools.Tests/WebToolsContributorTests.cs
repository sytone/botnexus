using System.Reflection;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Domain.Primitives;
using BotNexus.Extensions.WebTools.Search;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration;

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
    public async Task ContributeAsync_DefaultsOnly_ContributesConfiguredFetchAndSearch()
    {
        var defaults = JsonSerializer.SerializeToElement(new
        {
            fetch = new { maxLengthChars = 1234, timeoutSeconds = 17 },
            search = new { provider = "brave", apiKey = "shared-key", maxResults = 8 }
        });
        var descriptor = BuildDescriptor(defaults: defaults);

        var contribution = await new WebToolsContributor().ContributeAsync(BuildContext(descriptor));

        var fetch = contribution.Tools.OfType<WebFetchTool>().ShouldHaveSingleItem();
        GetFetchConfig(fetch).MaxLengthChars.ShouldBe(1234);
        GetFetchConfig(fetch).TimeoutSeconds.ShouldBe(17);
        var search = contribution.Tools.OfType<WebSearchTool>().ShouldHaveSingleItem();
        GetSearchConfig(search).Provider.ShouldBe("brave");
        GetSearchConfig(search).ApiKey.ShouldBe("shared-key");
        GetSearchConfig(search).MaxResults.ShouldBe(8);
    }

    [Fact]
    public async Task ContributeAsync_WorldAndGatewayBags_DoNotFlowIntoAgentReads()
    {
        var configured = JsonSerializer.SerializeToElement(new
        {
            fetch = new { maxLengthChars = 1234 },
            search = new { provider = "brave", apiKey = "scope-key" }
        });
        var platform = new PlatformConfig
        {
            World = new WorldSettingsConfig
            {
                Extensions = new Dictionary<string, JsonElement> { ["botnexus-web"] = configured }
            },
            Gateway = new GatewaySettingsConfig
            {
                Extensions = new Dictionary<string, JsonElement> { ["botnexus-web"] = configured }
            }
        };
        var descriptor = BuildDescriptor();

        platform.BindWorldExtension<WebToolsConfig>("botnexus-web").ShouldNotBeNull();
        platform.BindGatewayExtension<WebToolsConfig>("botnexus-web").ShouldNotBeNull();
        platform.BindAgentDefaultExtension<WebToolsConfig>("botnexus-web").ShouldBeNull();
        var contribution = await new WebToolsContributor().ContributeAsync(BuildContext(descriptor));

        contribution.Tools.ShouldBeEmpty();
    }

    [Fact]
    public async Task ContributeAsync_NoAgentScopes_DoesNotContributeTools()
    {
        var descriptor = BuildDescriptor();

        var contribution = await new WebToolsContributor().ContributeAsync(BuildContext(descriptor));

        contribution.Tools.ShouldBeEmpty();
    }

    [Fact]
    public async Task ContributeAsync_PartialNamedOverride_RetainsSiblingAgentDefaults()
    {
        var defaults = JsonSerializer.SerializeToElement(new { search = new { provider = "brave", apiKey = "shared-key", maxResults = 4 } });
        var named = JsonSerializer.SerializeToElement(new { search = new { maxResults = 9 } });
        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("test-agent"),
            DisplayName = "Test Agent",
            ModelId = "gpt-4.1",
            ApiProvider = "github-copilot",
            DefaultExtensionConfig = new Dictionary<string, JsonElement> { ["botnexus-web"] = defaults },
            ExtensionConfig = new Dictionary<string, JsonElement> { ["botnexus-web"] = named }
        };
        var context = new AgentToolContributionContext(
            descriptor,
            new AgentExecutionContext { SessionId = SessionId.Create() },
            Path.GetTempPath(),
            new AllowAllPathValidator(),
            null,
            (_, _) => Task.FromResult<string?>(null));

        var contribution = await new WebToolsContributor().ContributeAsync(context);

        var search = contribution.Tools.OfType<WebSearchTool>().ShouldHaveSingleItem();
        var config = GetSearchConfig(search);
        config.Provider.ShouldBe("brave");
        config.ApiKey.ShouldBe("shared-key");
        config.MaxResults.ShouldBe(9);
    }

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

    // -----------------------------------------------------------------------------------------
    // #3360 AC1 - the contributor supplies the DI-resolved ISecretRedactor to BOTH tools.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task ContributeAsync_SuppliesTheResolvedSecretRedactorToBothTools()
    {
        var redactor = new PassThroughRedactor();
        var context = BuildContext(searchProvider: "brave", apiKey: "token");

        var contribution = await new WebToolsContributor(secretRedactor: redactor).ContributeAsync(context);

        // Asserting on the tools rather than on the contributor is the point: an implementation
        // that accepted the dependency and dropped it would satisfy a constructor-signature test
        // and leak exactly as before.
        GetRedactor(contribution.Tools.OfType<WebFetchTool>().ShouldHaveSingleItem())
            .ShouldBeSameAs(redactor);
        GetRedactor(contribution.Tools.OfType<WebSearchTool>().ShouldHaveSingleItem())
            .ShouldBeSameAs(redactor);
    }

    [Fact]
    public async Task ContributeAsync_WithNoRedactorRegistered_StillContributesBothTools()
    {
        // The dependency is OPTIONAL by design: the extension load context prunes any contributor
        // whose constructor the host container cannot satisfy, so a required parameter would
        // silently delete both web tools on a host with no redactor registered.
        var context = BuildContext(searchProvider: "brave", apiKey: "token");

        var contribution = await new WebToolsContributor().ContributeAsync(context);

        contribution.Tools.OfType<WebFetchTool>().ShouldHaveSingleItem();
        contribution.Tools.OfType<WebSearchTool>().ShouldHaveSingleItem();
        GetRedactor(contribution.Tools.OfType<WebFetchTool>().First()).ShouldBeNull();
    }

    private static AgentDescriptor BuildDescriptor(JsonElement? defaults = null, JsonElement? named = null)
        => new()
        {
            AgentId = AgentId.From("test-agent"),
            DisplayName = "Test Agent",
            ModelId = "gpt-4.1",
            ApiProvider = "github-copilot",
            DefaultExtensionConfig = defaults is { } defaultValue
                ? new Dictionary<string, JsonElement> { ["botnexus-web"] = defaultValue }
                : new Dictionary<string, JsonElement>(),
            ExtensionConfig = named is { } namedValue
                ? new Dictionary<string, JsonElement> { ["botnexus-web"] = namedValue }
                : new Dictionary<string, JsonElement>()
        };

    private static AgentToolContributionContext BuildContext(AgentDescriptor descriptor)
        => new(
            descriptor,
            new AgentExecutionContext { SessionId = SessionId.Create() },
            Path.GetTempPath(),
            new AllowAllPathValidator(),
            null,
            (_, _) => Task.FromResult<string?>(null));

    private static WebFetchConfig GetFetchConfig(WebFetchTool tool)
    {
        var field = typeof(WebFetchTool).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WebFetchTool must retain its resolved configuration.");
        return field.GetValue(tool) as WebFetchConfig
            ?? throw new InvalidOperationException("WebFetchTool configuration has an unexpected type.");
    }

    private static WebSearchConfig GetSearchConfig(WebSearchTool tool)
    {
        var field = typeof(WebSearchTool).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WebSearchTool must retain its resolved configuration.");
        return field.GetValue(tool) as WebSearchConfig
            ?? throw new InvalidOperationException("WebSearchTool configuration has an unexpected type.");
    }

    private static ISecretRedactor? GetRedactor(object tool)
    {
        var field = tool.GetType().GetField("_secretRedactor", BindingFlags.Instance | BindingFlags.NonPublic);
        field.ShouldNotBeNull($"{tool.GetType().Name} must hold the injected ISecretRedactor (#3360).");
        return (ISecretRedactor?)field!.GetValue(tool);
    }

    private sealed class PassThroughRedactor : ISecretRedactor
    {
        public string Redact(string input) => input;

        public string RedactForExternalDelivery(string input) => input;
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
