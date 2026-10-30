using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Gateway.Api.Controllers;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>Quick saves must use model identities and the lossless operator edit seam (#4952).</summary>
public sealed class AgentConfigPanelModelSaveTests : IDisposable
{
    private readonly BunitContext _context = new();
    private readonly ModelHandler _handler = new();
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    public AgentConfigPanelModelSaveTests()
    {
        var store = new ClientStateStore();
        store.SeedAgents([new AgentSummary("sample", "Sample")]);
        var rest = Substitute.For<IGatewayRestClient>();
        rest.ApiBaseUrl.Returns("http://localhost/api/");
        _context.Services.AddSingleton<IClientStateStore>(store);
        _context.Services.AddSingleton(rest);
        _context.Services.AddSingleton(new HttpClient(_handler));
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _context.Dispose();

    private async Task<IRenderedComponent<AgentConfigPanel>> OpenAsync()
    {
        var panel = _context.Render<AgentConfigPanel>(parameters => parameters.Add(p => p.AgentId, "sample"));
        await panel.InvokeAsync(panel.Instance.Open);
        return panel;
    }

    private static async Task SelectAsync(IRenderedComponent<AgentConfigPanel> panel) =>
        await panel.Find("[data-field='model'] select").ChangeAsync(new ChangeEventArgs { Value = "next" });

    private static AngleSharp.Dom.IElement SaveButton(IRenderedComponent<AgentConfigPanel> panel) =>
        panel.FindAll("button").Single(button => button.TextContent.Trim() == "Save Model");

    [Fact]
    public async Task Open_RealModelInfoPayload_RendersIdentityAndDisplayName()
    {
        var panel = await OpenAsync();

        var option = panel.Find("[data-field='model'] option[value='next']");
        option.TextContent.ShouldContain("Next display name");
        panel.Find("[data-field='model'] select").GetAttribute("aria-label").ShouldBe("Model");
        panel.FindAll("button").ShouldNotContain(button => button.TextContent.Trim() == "Save Model");
    }

    [Fact]
    public async Task Save_PatchesOnlyModel_ThenReadsPersistedAndEffectiveState()
    {
        _handler.EffectiveModel = "next";
        var panel = await OpenAsync();
        await SelectAsync(panel);
        await SaveButton(panel).ClickAsync(new());

        _handler.Writes.ShouldHaveSingleItem().Method.ShouldBe(HttpMethod.Patch);
        _handler.Writes[0].Path.ShouldBe("/api/agents/sample/configuration");
        using var body = JsonDocument.Parse(_handler.Writes[0].Body);
        body.RootElement.EnumerateObject().Select(property => property.Name).ShouldBe(["model"]);
        body.RootElement.GetProperty("model").GetString().ShouldBe("next");
        _handler.ReadsAfterSave.ShouldContain("/api/agents/sample/configuration");
        _handler.ReadsAfterSave.ShouldContain("/api/agents/sample");
        panel.Find("[role='status']").TextContent.ShouldContain("Saved");
        panel.Find("[role='status']").TextContent.ShouldNotContain("pending");
        panel.FindAll("button").ShouldNotContain(button => button.TextContent.Trim() == "Save Model");
    }

    [Fact]
    public async Task Save_RuntimeStillOld_ReportsPendingWithoutPretendingEffectiveStateChanged()
    {
        var panel = await OpenAsync();
        await SelectAsync(panel);
        await SaveButton(panel).ClickAsync(new());

        panel.Find("[role='status']").TextContent.ShouldContain("pending");
        panel.Find("[role='status']").TextContent.ShouldContain("current");
        panel.FindAll("button").ShouldNotContain(button => button.TextContent.Trim() == "Save Model");
    }

    [Theory]
    [InlineData("{\"errors\":[\"Model is not allowed.\"]}", "Model is not allowed.")]
    [InlineData("{\"error\":\"Agent not found.\"}", "Agent not found.")]
    [InlineData("{\"detail\":\"Configuration store unavailable.\"}", "Configuration store unavailable.")]
    [InlineData("not-json", "BadRequest")]
    public async Task Save_Rejected_ShowsActionableErrorAndKeepsSelection(string response, string expected)
    {
        _handler.SaveStatus = HttpStatusCode.BadRequest;
        _handler.SaveResponse = response;
        var panel = await OpenAsync();
        await SelectAsync(panel);
        await SaveButton(panel).ClickAsync(new());

        panel.Find("[role='alert']").TextContent.ShouldContain(expected);
        panel.Find("[data-field='model'] select").GetAttribute("value").ShouldBe("next");
        SaveButton(panel).HasAttribute("disabled").ShouldBeFalse();
        panel.FindAll("[role='status']").ShouldBeEmpty();
    }

    [Fact]
    public async Task Save_ReadbackMismatch_DoesNotClaimSuccess()
    {
        _handler.PersistedModel = "current";
        var panel = await OpenAsync();
        await SelectAsync(panel);
        await SaveButton(panel).ClickAsync(new());

        panel.Find("[role='alert']").TextContent.ShouldContain("readback");
        panel.FindAll("[role='status']").ShouldBeEmpty();
        SaveButton(panel).HasAttribute("disabled").ShouldBeFalse();
    }

    [Fact]
    public async Task Open_EmptyAllowedModels_ShowsCurrentModelWithoutSave()
    {
        _handler.Models = [];
        var panel = await OpenAsync();

        panel.Find("[data-field='model'] .config-value").TextContent.ShouldBe("current");
        panel.FindAll("button").ShouldNotContain(button => button.TextContent.Trim() == "Save Model");
    }

    [Fact]
    public async Task Open_ModelFromDifferentProvider_IsNotSelectable()
    {
        _handler.Models = [new ModelInfo("Wrong provider", "next", "next", "other", [], [])];
        var panel = await OpenAsync();

        panel.FindAll("[data-field='model'] option[value='next']").ShouldBeEmpty();
    }

    [Fact]
    public async Task Save_AuthoredAlias_UsesServerProviderInstanceAndPreservesAuthoredProvider()
    {
        var registry = new BotNexus.Agent.Providers.Core.Registry.ModelRegistry();
        registry.Register("github-copilot", new BotNexus.Agent.Providers.Core.Models.LlmModel(
            "next", "Next", "completions", "github-copilot", "https://example.invalid",
            false, ["text"], new(0m, 0m, 0m, 0m), 128_000, 16_000));
        var options = Substitute.For<Microsoft.Extensions.Options.IOptionsMonitor<BotNexus.Gateway.Configuration.PlatformConfig>>();
        options.CurrentValue.Returns(new BotNexus.Gateway.Configuration.PlatformConfig());
        var filter = new BotNexus.Gateway.Configuration.ConfigModelFilter(registry, options);
        var agents = Substitute.For<BotNexus.Gateway.Abstractions.Agents.IAgentRegistry>();
        agents.Get(BotNexus.Domain.Primitives.AgentId.From("sample")).Returns(new BotNexus.Gateway.Abstractions.Models.AgentDescriptor
        {
            AgentId = BotNexus.Domain.Primitives.AgentId.From("sample"),
            DisplayName = "Sample", ApiProvider = "copilot", ModelId = "current",
        });
        var result = new ModelsController(filter, agents).GetAgentModels("sample");
        var response = result.Result.ShouldBeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>();
        _handler.ModelsJson = JsonSerializer.Serialize(response.Value, Wire);
        using var document = JsonDocument.Parse(_handler.ModelsJson);
        document.RootElement[0].GetProperty("provider").GetString().ShouldBe("github-copilot");
        document.RootElement[0].GetProperty("providerInstance").GetString().ShouldBe("copilot");
        _handler.Provider = "copilot";
        _handler.EffectiveModel = "next";
        var panel = await OpenAsync();
        await SelectAsync(panel);
        await SaveButton(panel).ClickAsync(new());

        using var body = JsonDocument.Parse(_handler.Writes.ShouldHaveSingleItem().Body);
        body.RootElement.EnumerateObject().Select(property => property.Name).ShouldBe(["model"]);
        panel.Find("[data-field='provider'] .config-value").TextContent.ShouldBe("copilot");
        panel.Find("[role='status']").TextContent.ShouldNotContain("pending");
    }

    [Fact]
    public async Task Open_PendingRuntimeReopen_RetainsPersistedSelectionWithoutAnotherWrite()
    {
        var panel = await OpenAsync();
        await SelectAsync(panel);
        await SaveButton(panel).ClickAsync(new());
        await panel.InvokeAsync(panel.Instance.Open);

        panel.Find("[data-field='model'] select").GetAttribute("value").ShouldBe("next");
        panel.Find("[role='status']").TextContent.ShouldContain("pending");
        panel.FindAll("button").ShouldNotContain(button => button.TextContent.Trim() == "Save Model");
        _handler.Writes.Count.ShouldBe(1);
        _handler.EffectiveModel = "next";
        await panel.InvokeAsync(panel.Instance.Open);
        panel.Find("[role='status']").TextContent.ShouldNotContain("pending");
        _handler.Writes.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Open_UnavailableCurrentModel_RemainsVisibleWithoutPretendingFirstOptionSelected()
    {
        _handler.Models.RemoveAt(0);
        var panel = await OpenAsync();
        panel.Find("[data-field='model'] option[value='current']").TextContent.ShouldContain("current");
        panel.Find("[data-field='model'] select").GetAttribute("value").ShouldBe("current");
        panel.FindAll("button").ShouldNotContain(button => button.TextContent.Trim() == "Save Model");
    }

    private sealed class ModelHandler : HttpMessageHandler
    {
        public List<ModelInfo> Models { get; set; } = [
            new("Current", "current", "current", "test", [], []),
            new("Next display name", "next", "next", "test", [], [])];
        public string? ModelsJson { get; set; }
        public string Provider { get; set; } = "test";
        public HttpStatusCode SaveStatus { get; set; } = HttpStatusCode.OK;
        public string SaveResponse { get; set; } = "{\"saved\":true}";
        public string PersistedModel { get; set; } = "next";
        public string EffectiveModel { get; set; } = "current";
        public List<(HttpMethod Method, string Path, string Body)> Writes { get; } = [];
        public List<string> ReadsAfterSave { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (request.Method != HttpMethod.Get)
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                Writes.Add((request.Method, path, body));
                return Response(SaveStatus, SaveResponse);
            }
            if (Writes.Count > 0)
                ReadsAfterSave.Add(path);
            return path switch
            {
                "/api/agents/sample/models" => Response(HttpStatusCode.OK, ModelsJson ?? JsonSerializer.Serialize(Models, Wire)),
                "/api/agents/sample/configuration" => Response(HttpStatusCode.OK, JsonSerializer.Serialize(new { model = Writes.Count == 0 ? "current" : PersistedModel, provider = Provider }, Wire)),
                "/api/agents/sample" => Response(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    agentId = "sample", modelId = Writes.Count == 0 ? "current" : EffectiveModel,
                    apiProvider = Provider, toolIds = new[] { "read" },
                }, Wire)),
                _ => Response(HttpStatusCode.NotFound, "{}"),
            };
        }

        private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
