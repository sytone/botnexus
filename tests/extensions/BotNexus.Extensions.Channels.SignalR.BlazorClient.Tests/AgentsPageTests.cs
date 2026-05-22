using System.Net;
using System.Net.Http.Json;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Bunit;
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
    public void Add_button_shows_form()
    {
        _httpHandler.SetupResponse("/api/agents", "[]");
        _httpHandler.SetupResponse("/api/providers", "[]");

        var cut = _ctx.Render<Agents>();
        cut.WaitForState(() => cut.Markup.Contains("Add Agent"));

        cut.Find("button.primary").Click();

        Assert.Contains("Agent ID", cut.Markup);
        Assert.Contains("Display Name", cut.Markup);
    }

    [Fact]
    public void Form_validation_shows_field_errors()
    {
        _httpHandler.SetupResponse("/api/agents", "[]");
        _httpHandler.SetupResponse("/api/providers", "[]");

        var cut = _ctx.Render<Agents>();
        cut.WaitForState(() => cut.Markup.Contains("Add Agent"));

        cut.Find("button.primary").Click();
        cut.Find(".agents-form-actions button.primary").Click();

        Assert.Contains("Agent ID is required", cut.Markup);
        Assert.Contains("Display Name is required", cut.Markup);
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
    public void Edit_button_populates_form_with_agent_data()
    {
        var agents = JsonSerializer.Serialize(new[]
        {
            new { agentId = "bot-1", displayName = "Bot One", description = "A test bot", apiProvider = "openai", modelId = "gpt-4", systemPrompt = "You are helpful" }
        });
        _httpHandler.SetupResponse("/api/agents", agents);
        _httpHandler.SetupResponse("/api/providers", "[]");
        _httpHandler.SetupResponse("/api/models?provider=openai", "[]");

        var cut = _ctx.Render<Agents>();
        cut.WaitForState(() => cut.Markup.Contains("bot-1"));

        cut.Find("button.agents-btn-edit").Click();

        Assert.Contains("Edit Agent: bot-1", cut.Markup);
    }

    [Fact]
    public void Save_new_agent_refreshes_list_from_api_without_stale_state()
    {
        _httpHandler.SetupResponse("/api/agents", "[]");
        _httpHandler.SetupResponse("/api/providers", "[]");

        var cut = _ctx.Render<Agents>();
        cut.WaitForState(() => cut.Markup.Contains("Add Agent"));

        cut.Find("button.primary").Click();
        cut.Find("#agent-id-input").Input("bot-2");
        cut.Find("#display-name-input").Input("Bot Two");
        cut.Find("#provider-input").Input("openai");
        cut.Find("#model-id-input").Input("gpt-4.1");

        var refreshedAgents = JsonSerializer.Serialize(new[]
        {
            new { agentId = "bot-2", displayName = "Bot Two", description = "", apiProvider = "openai", modelId = "gpt-4.1", systemPrompt = "" }
        });
        _httpHandler.SetupResponse("/api/agents", refreshedAgents);
        _httpHandler.SetupStatusResponse("POST", "/api/agents", HttpStatusCode.Created);

        cut.Find(".agents-form-actions button.primary").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("bot-2", cut.Markup);
            Assert.Contains("Bot Two", cut.Markup);
        });
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

    [Fact]
    public void Clone_button_renders_in_agent_row()
    {
        var agents = JsonSerializer.Serialize(new[]
        {
            new { agentId = "bot-1", displayName = "Bot One", description = "", apiProvider = "openai", modelId = "gpt-4", systemPrompt = "" }
        });
        _httpHandler.SetupResponse("/api/agents", agents);
        _httpHandler.SetupResponse("/api/providers", "[]");

        var cut = _ctx.Render<Agents>();
        cut.WaitForState(() => cut.Markup.Contains("bot-1"));

        Assert.Contains("agents-btn-clone", cut.Markup);
    }

    [Fact]
    public void Clone_button_opens_form_with_copy_display_name()
    {
        var agents = JsonSerializer.Serialize(new[]
        {
            new { agentId = "bot-1", displayName = "Bot One", description = "A bot", apiProvider = "openai", modelId = "gpt-4", systemPrompt = "Be helpful." }
        });
        _httpHandler.SetupResponse("/api/agents", agents);
        _httpHandler.SetupResponse("/api/providers", "[]");

        var cut = _ctx.Render<Agents>();
        cut.WaitForState(() => cut.Markup.Contains("bot-1"));

        cut.Find("button.agents-btn-clone").Click();

        cut.WaitForState(() => cut.Markup.Contains("Copy of Bot One"));
        Assert.Contains("Copy of Bot One", cut.Markup);
    }

    [Fact]
    public void Clone_form_has_empty_agent_id_field()
    {
        var agents = JsonSerializer.Serialize(new[]
        {
            new { agentId = "bot-1", displayName = "Bot One", description = "", apiProvider = "openai", modelId = "gpt-4", systemPrompt = "" }
        });
        _httpHandler.SetupResponse("/api/agents", agents);
        _httpHandler.SetupResponse("/api/providers", "[]");

        var cut = _ctx.Render<Agents>();
        cut.WaitForState(() => cut.Markup.Contains("bot-1"));

        cut.Find("button.agents-btn-clone").Click();

        cut.WaitForState(() => cut.Markup.Contains("Copy of Bot One"));
        var agentIdInput = cut.Find("#agent-id-input");
        Assert.Equal("", agentIdInput.GetAttribute("value"));
        // Agent ID must be editable when cloning (not disabled)
        Assert.Null(agentIdInput.GetAttribute("disabled"));
    }

    [Fact]
    public void Clone_form_copies_provider_and_model()
    {
        var agents = JsonSerializer.Serialize(new[]
        {
            new { agentId = "bot-1", displayName = "Bot One", description = "", apiProvider = "copilot", modelId = "gpt-4o", systemPrompt = "" }
        });
        _httpHandler.SetupResponse("/api/agents", agents);
        _httpHandler.SetupResponse("/api/providers", "[]");

        var cut = _ctx.Render<Agents>();
        cut.WaitForState(() => cut.Markup.Contains("bot-1"));

        cut.Find("button.agents-btn-clone").Click();

        cut.WaitForState(() => cut.Markup.Contains("Copy of Bot One"));
        Assert.Contains("copilot", cut.Markup);
        Assert.Contains("gpt-4o", cut.Markup);
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

