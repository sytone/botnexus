using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class AgentDetailPanelTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly MockHttpMessageHandler _httpHandler = new();

    public AgentDetailPanelTests()
    {
        var httpClient = new HttpClient(_httpHandler) { BaseAddress = new Uri("http://localhost/") };
        _ctx.Services.AddSingleton(httpClient);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private static string AgentJson(string agentId = "test-agent", string displayName = "Test Agent") =>
        JsonSerializer.Serialize(new
        {
            agentId,
            displayName,
            description = "A test agent",
            enabled = true,
            apiProvider = "openai",
            modelId = "gpt-4",
            systemPrompt = "You are helpful"
        });

    [Fact]
    public void AgentsPage_NavigatesToDetailPanel_WhenAgentIdRouteProvided()
    {
        _httpHandler.SetupResponse("/api/agents/test-agent", AgentJson());
        _httpHandler.SetupResponse("/api/agents", "[{\"agentId\":\"test-agent\",\"displayName\":\"Test Agent\",\"apiProvider\":\"openai\",\"modelId\":\"gpt-4\"}]");
        _httpHandler.SetupResponse("/api/providers", "[]");
        _httpHandler.SetupResponse("/api/models", "[]");

        var hub = new GatewayHubConnection();
        _ctx.Services.AddSingleton(hub);

        var cut = _ctx.Render<Agents>(p => p.Add(c => c.AgentId, "test-agent"));

        cut.WaitForState(() => cut.HasComponent<AgentDetailPanel>(), TimeSpan.FromSeconds(3));
        Assert.True(cut.HasComponent<AgentDetailPanel>());
    }

    [Fact]
    public void AgentDetailPanel_LoadsAgentData_OnInit()
    {
        _httpHandler.SetupResponse("/api/agents/test-agent", AgentJson());
        _httpHandler.SetupResponse("/api/agents", "[]");
        _httpHandler.SetupResponse("/api/providers", "[]");
        _httpHandler.SetupResponse("/api/models", "[]");

        var cut = _ctx.Render<AgentDetailPanel>(p =>
            p.Add(c => c.AgentId, "test-agent"));

        cut.WaitForState(() => cut.Markup.Contains("Test Agent"), TimeSpan.FromSeconds(3));
        Assert.Contains("Test Agent", cut.Markup);
    }

    [Fact]
    public void AgentDetailPanel_SaveButton_Disabled_WhenNotDirty()
    {
        _httpHandler.SetupResponse("/api/agents/test-agent", AgentJson());
        _httpHandler.SetupResponse("/api/agents", "[]");
        _httpHandler.SetupResponse("/api/providers", "[]");
        _httpHandler.SetupResponse("/api/models", "[]");

        var cut = _ctx.Render<AgentDetailPanel>(p =>
            p.Add(c => c.AgentId, "test-agent"));

        cut.WaitForState(() => !cut.Markup.Contains("Loading agent"), TimeSpan.FromSeconds(3));

        var saveBtn = cut.FindAll("button")
            .FirstOrDefault(b => b.TextContent.Contains("Save Changes"));
        Assert.NotNull(saveBtn);
        Assert.True(saveBtn.HasAttribute("disabled"));
    }

    [Fact]
    public void AgentDetailPanel_ShowsDeleteConfirm_OnDeleteClick()
    {
        _httpHandler.SetupResponse("/api/agents/test-agent", AgentJson());
        _httpHandler.SetupResponse("/api/agents", "[]");
        _httpHandler.SetupResponse("/api/providers", "[]");
        _httpHandler.SetupResponse("/api/models", "[]");

        var cut = _ctx.Render<AgentDetailPanel>(p =>
            p.Add(c => c.AgentId, "test-agent"));

        cut.WaitForState(() => cut.Markup.Contains("Delete Agent"), TimeSpan.FromSeconds(3));

        cut.Find(".toolbar-btn.danger").Click();

        Assert.Contains("Are you sure", cut.Markup);
        Assert.Contains("Yes, Delete", cut.Markup);
    }

    // --- PBI4 (#1705): thinking + context pickers driven by model capabilities ---

    private static string ReasoningAgentJson() =>
        JsonSerializer.Serialize(new
        {
            agentId = "test-agent",
            displayName = "Test Agent",
            enabled = true,
            apiProvider = "openai",
            modelId = "reasoning-model",
            thinking = "high",
            contextWindow = 1000000
        });

    private static string ModelsWithCapabilitiesJson() =>
        JsonSerializer.Serialize(new[]
        {
            new
            {
                name = "Reasoning Model",
                modelId = "reasoning-model",
                id = "reasoning-model",
                provider = "openai",
                supportedThinkingLevels = new[] { "minimal", "low", "medium", "high", "xhigh", "max" },
                supportedContextSizes = new[] { 200000, 1000000 }
            }
        });

    [Fact]
    public void AgentDetailPanel_RendersThinkingPicker_WithModelSupportedLevels()
    {
        _httpHandler.SetupResponse("/api/agents/test-agent", ReasoningAgentJson());
        _httpHandler.SetupResponse("/api/agents", "[]");
        _httpHandler.SetupResponse("/api/providers", "[]");
        _httpHandler.SetupResponse("/api/models", ModelsWithCapabilitiesJson());

        var cut = _ctx.Render<AgentDetailPanel>(p => p.Add(c => c.AgentId, "test-agent"));

        cut.WaitForState(() => cut.Markup.Contains("Thinking Level"), TimeSpan.FromSeconds(3));

        Assert.Contains("Thinking Level", cut.Markup);
        Assert.Contains("Context Window", cut.Markup);
        // The capability-driven option set must be offered (xhigh only exists on capable models).
        Assert.Contains("xhigh", cut.Markup);
        Assert.Contains("1,000,000 tokens", cut.Markup);
    }

    /// <summary>Simple mock HTTP handler reused from AgentsPageTests pattern.</summary>
    internal sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpResponseMessage> _responses = new(StringComparer.OrdinalIgnoreCase);

        public void SetupResponse(string pathSuffix, string jsonContent)
        {
            _responses[pathSuffix] = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json")
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            foreach (var (key, response) in _responses)
            {
                if (path.Contains(key, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(response);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
