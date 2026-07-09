using System.Net;
using System.Net.Http.Json;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class AgentsPageTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly GatewayHubConnection _hub = new();
    private readonly MockHttpMessageHandler _httpHandler = new();

    public AgentsPageTests()
    {
        var httpClient = new HttpClient(_httpHandler) { BaseAddress = new Uri("http://localhost/") };
        _ctx.Services.AddSingleton(httpClient);
        _ctx.Services.AddSingleton(_hub);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Shows_loading_spinner_initially()
    {
        _httpHandler.SetupResponse("/api/agents", "[]");
        _httpHandler.SetupResponse("/api/providers", "[]");

        var cut = _ctx.Render<Agents>();

        cut.WaitForState(() => !cut.Markup.Contains("Loading agents"));
        Assert.DoesNotContain("Loading agents", cut.Markup);
    }

    [Fact]
    public void Shows_empty_state_when_no_agents()
    {
        _httpHandler.SetupResponse("/api/agents", "[]");
        _httpHandler.SetupResponse("/api/providers", "[]");

        var cut = _ctx.Render<Agents>();
        cut.WaitForState(() => cut.Markup.Contains("No agents configured"));

        Assert.Contains("No agents configured", cut.Markup);
    }

    [Fact]
    public void Displays_agents_in_table()
    {
        var agents = JsonSerializer.Serialize(new[]
        {
            new { agentId = "bot-1", displayName = "Bot One", description = "", apiProvider = "openai", modelId = "gpt-4", systemPrompt = "" }
        });
        _httpHandler.SetupResponse("/api/agents", agents);
        _httpHandler.SetupResponse("/api/providers", "[]");

        var cut = _ctx.Render<Agents>();
        cut.WaitForState(() => cut.Markup.Contains("bot-1"));

        Assert.Contains("bot-1", cut.Markup);
        Assert.Contains("Bot One", cut.Markup);
        Assert.Contains("openai", cut.Markup);
    }

    [Fact]
    public void Add_button_shows_full_editor_in_create_mode()
    {
        _httpHandler.SetupResponse("/api/agents", "[]");
        _httpHandler.SetupResponse("/api/providers", "[]");
        _httpHandler.SetupResponse("/api/models", "[]");

        var cut = _ctx.Render<Agents>();
        cut.WaitForState(() => cut.Markup.Contains("Add Agent"));

        cut.Find("button.primary").Click();

        cut.WaitForState(() => cut.HasComponent<AgentDetailPanel>(), TimeSpan.FromSeconds(3));
        Assert.True(cut.HasComponent<AgentDetailPanel>());
        // The full editor exposes the sectioned schema, not just the legacy 6 fields.
        Assert.Contains("New Agent", cut.Markup);
        Assert.Contains("Agent ID", cut.Markup);
        Assert.Contains("Heartbeat", cut.Markup);
        Assert.Contains("Tool Policy", cut.Markup);
        Assert.Contains("Create Agent", cut.Markup);
    }

    [Fact]
    public void Delete_button_shows_confirmation_dialog()
    {
        var agents = JsonSerializer.Serialize(new[]
        {
            new { agentId = "bot-1", displayName = "Bot One", description = "", apiProvider = "openai", modelId = "gpt-4", systemPrompt = "" }
        });
        _httpHandler.SetupResponse("/api/agents", agents);
        _httpHandler.SetupResponse("/api/providers", "[]");

        var cut = _ctx.Render<Agents>();
        cut.WaitForState(() => cut.Markup.Contains("bot-1"));

        cut.Find("button.agents-btn-delete").Click();

        Assert.Contains("Delete Agent", cut.Markup);
        Assert.Contains("Are you sure", cut.Markup);
        Assert.Contains("bot-1", cut.Markup);
    }

    [Fact]
    public void Cancel_delete_hides_confirmation_dialog()
    {
        var agents = JsonSerializer.Serialize(new[]
        {
            new { agentId = "bot-1", displayName = "Bot One", description = "", apiProvider = "openai", modelId = "gpt-4", systemPrompt = "" }
        });
        _httpHandler.SetupResponse("/api/agents", agents);
        _httpHandler.SetupResponse("/api/providers", "[]");

        var cut = _ctx.Render<Agents>();
        cut.WaitForState(() => cut.Markup.Contains("bot-1"));

        cut.Find("button.agents-btn-delete").Click();
        Assert.Contains("Are you sure", cut.Markup);

        var cancelBtn = cut.FindAll(".agents-confirm-actions button")
            .First(b => b.TextContent.Contains("Cancel"));
        cancelBtn.Click();

        Assert.DoesNotContain("Are you sure", cut.Markup);
    }

    [Fact]
    public void Shows_error_when_agents_api_fails()
    {
        _httpHandler.SetupErrorResponse("/api/agents", HttpStatusCode.InternalServerError);
        _httpHandler.SetupResponse("/api/providers", "[]");

        var cut = _ctx.Render<Agents>();
        cut.WaitForState(() => cut.Markup.Contains("Failed to load agents"));

        Assert.Contains("Failed to load agents", cut.Markup);
    }

    [Fact]
    public void Edit_button_opens_full_editor_in_edit_mode()
    {
        var agents = JsonSerializer.Serialize(new[]
        {
            new { agentId = "bot-1", displayName = "Bot One", description = "A test bot", apiProvider = "openai", modelId = "gpt-4", systemPrompt = "You are helpful" }
        });
        _httpHandler.SetupResponse("/api/agents/bot-1", "{\"agentId\":\"bot-1\",\"displayName\":\"Bot One\",\"apiProvider\":\"openai\",\"modelId\":\"gpt-4\"}");
        _httpHandler.SetupResponse("/api/agents", agents);
        _httpHandler.SetupResponse("/api/providers", "[]");
        _httpHandler.SetupResponse("/api/models", "[]");

        var cut = _ctx.Render<Agents>();
        cut.WaitForState(() => cut.Markup.Contains("bot-1"));

        cut.Find("button.agents-btn-edit").Click();

        cut.WaitForState(() => cut.HasComponent<AgentDetailPanel>(), TimeSpan.FromSeconds(3));
        Assert.True(cut.HasComponent<AgentDetailPanel>());
        Assert.Contains("Save Changes", cut.Markup);
    }

    [Fact]
    public void Clone_button_opens_editor_in_clone_mode_with_copy_prefix()
    {
        var agents = JsonSerializer.Serialize(new[]
        {
            new { agentId = "bot-1", displayName = "Bot One", description = "A test bot", apiProvider = "openai", modelId = "gpt-4", systemPrompt = "You are helpful" }
        });
        // Register the specific agent path first so the substring matcher does not resolve
        // /api/agents/bot-1 against the /api/agents list response.
        _httpHandler.SetupResponse("/api/agents/bot-1",
            "{\"agentId\":\"bot-1\",\"displayName\":\"Bot One\",\"apiProvider\":\"openai\",\"modelId\":\"gpt-4\"}");
        _httpHandler.SetupResponse("/api/agents", agents);
        _httpHandler.SetupResponse("/api/providers", "[]");
        _httpHandler.SetupResponse("/api/models", "[]");

        var cut = _ctx.Render<Agents>();
        cut.WaitForState(() => cut.Markup.Contains("bot-1"));

        cut.Find("button.agents-btn-clone").Click();

        cut.WaitForState(() => cut.Markup.Contains("Clone Agent"), TimeSpan.FromSeconds(3));
        Assert.Contains("Clone Agent", cut.Markup);
        Assert.Contains("Create Clone", cut.Markup);
        // Clone pre-fills the display name with a "Copy of" prefix and clears the ID.
        var displayInput = cut.FindAll("input.cfg-input")
            .FirstOrDefault(i => i.GetAttribute("value") == "Copy of Bot One");
        Assert.NotNull(displayInput);
        var idInput = cut.Find("#agent-id-input");
        Assert.Equal(string.Empty, idInput.GetAttribute("value") ?? string.Empty);
    }

    [Fact]
    public void Agents_changed_signalr_event_refreshes_agents_from_api()
    {
        var initialAgents = JsonSerializer.Serialize(new[]
        {
            new { agentId = "bot-1", displayName = "Bot One", description = "", apiProvider = "openai", modelId = "gpt-4", systemPrompt = "" }
        });
        _httpHandler.SetupResponse("/api/agents", initialAgents);
        _httpHandler.SetupResponse("/api/providers", "[]");

        var cut = _ctx.Render<Agents>();
        cut.WaitForState(() => cut.Markup.Contains("bot-1"));

        var refreshedAgents = JsonSerializer.Serialize(new[]
        {
            new { agentId = "bot-1", displayName = "Bot One", description = "", apiProvider = "openai", modelId = "gpt-4", systemPrompt = "" },
            new { agentId = "bot-2", displayName = "Bot Two", description = "", apiProvider = "openai", modelId = "gpt-4.1", systemPrompt = "" }
        });
        _httpHandler.SetupResponse("/api/agents", refreshedAgents);

        RaiseAgentsChanged(new AgentsChangedPayload("updated", "bot-2"));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("bot-2", cut.Markup);
            Assert.Contains("Agent list updated from server.", cut.Markup);
        });
    }

    [Fact]
    public void Accessible_table_has_aria_label()
    {
        var agents = JsonSerializer.Serialize(new[]
        {
            new { agentId = "bot-1", displayName = "Bot One", description = "", apiProvider = "openai", modelId = "gpt-4", systemPrompt = "" }
        });
        _httpHandler.SetupResponse("/api/agents", agents);
        _httpHandler.SetupResponse("/api/providers", "[]");

        var cut = _ctx.Render<Agents>();
        cut.WaitForState(() => cut.Markup.Contains("bot-1"));

        var table = cut.Find("table.agents-table");
        Assert.Equal("Agent list", table.GetAttribute("aria-label"));
    }

    private void RaiseAgentsChanged(AgentsChangedPayload payload)
    {
        var field = typeof(GatewayHubConnection).GetField("OnAgentsChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        var handler = field?.GetValue(_hub) as Action<AgentsChangedPayload>;
        handler?.Invoke(payload);
    }

    /// <summary>
    /// Simple mock HTTP handler for bUnit tests.
    /// </summary>
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

        public void SetupErrorResponse(string pathSuffix, HttpStatusCode statusCode)
        {
            _responses[pathSuffix] = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("Error", System.Text.Encoding.UTF8, "text/plain")
            };
        }

        public void SetupStatusResponse(string method, string pathSuffix, HttpStatusCode statusCode)
        {
            _responses[$"{method}:{pathSuffix}"] = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(string.Empty, System.Text.Encoding.UTF8, "text/plain")
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            var methodKey = $"{request.Method.Method}:{path}";

            foreach (var (key, response) in _responses)
            {
                if (key.Contains(':') && methodKey.Contains(key, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(response);

                if (path.Contains(key, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
