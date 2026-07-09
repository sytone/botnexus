using System.Net;
using System.Text.Json;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Covers the create/clone editor modes and the Allowed Models control added for the
/// full <c>AgentDefinitionConfig</c> editor (issue #290). The existing edit-mode behaviour
/// is covered by <see cref="AgentDetailPanelTests"/>.
/// </summary>
public sealed class AgentEditorModeTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly MockHttpMessageHandler _httpHandler = new();

    public AgentEditorModeTests()
    {
        var httpClient = new HttpClient(_httpHandler) { BaseAddress = new Uri("http://localhost/") };
        _ctx.Services.AddSingleton(httpClient);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void CreateMode_StartsWithBlankEditableId_AndNoDeleteZone()
    {
        _httpHandler.SetupResponse("/api/agents", "[]");
        _httpHandler.SetupResponse("/api/providers", "[]");
        _httpHandler.SetupResponse("/api/models", "[]");

        var cut = _ctx.Render<AgentDetailPanel>(p => p.Add(c => c.Mode, "create"));

        cut.WaitForState(() => cut.Markup.Contains("New Agent"), TimeSpan.FromSeconds(3));

        var idInput = cut.Find("#agent-id-input");
        Assert.False(idInput.HasAttribute("disabled"));
        Assert.Equal(string.Empty, idInput.GetAttribute("value") ?? string.Empty);
        // Create mode never offers the destructive delete zone.
        Assert.DoesNotContain("Delete Agent", cut.Markup);
        Assert.Contains("Create Agent", cut.Markup);
    }

    [Fact]
    public void CreateMode_RequiresAgentId_ShowsValidationError()
    {
        _httpHandler.SetupResponse("/api/agents", "[]");
        _httpHandler.SetupResponse("/api/providers", "[]");
        _httpHandler.SetupResponse("/api/models", "[]");

        var cut = _ctx.Render<AgentDetailPanel>(p => p.Add(c => c.Mode, "create"));
        cut.WaitForState(() => cut.Markup.Contains("New Agent"), TimeSpan.FromSeconds(3));

        // Save with an empty ID must surface the required-field error and not POST.
        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Create Agent"));
        saveBtn.Click();

        Assert.Contains("Agent ID is required", cut.Markup);
    }

    [Fact]
    public void CloneMode_PrefillsCopyOfName_AndClearsId()
    {
        var sourceJson = JsonSerializer.Serialize(new
        {
            agentId = "source-bot",
            displayName = "Source Bot",
            description = "Original",
            enabled = true,
            apiProvider = "openai",
            modelId = "gpt-4",
            toolIds = new[] { "read", "write" }
        });
        _httpHandler.SetupResponse("/api/agents/source-bot", sourceJson);
        _httpHandler.SetupResponse("/api/agents", "[]");
        _httpHandler.SetupResponse("/api/providers", "[]");
        _httpHandler.SetupResponse("/api/models", "[]");

        var cut = _ctx.Render<AgentDetailPanel>(p =>
        {
            p.Add(c => c.Mode, "clone");
            p.Add(c => c.AgentId, "source-bot");
        });

        cut.WaitForState(() => cut.Markup.Contains("Clone Agent"), TimeSpan.FromSeconds(3));

        Assert.Contains("Create Clone", cut.Markup);
        // Cloned tools are carried over from the source agent.
        Assert.Contains("read", cut.Markup);
        Assert.Contains("write", cut.Markup);

        var idInput = cut.Find("#agent-id-input");
        Assert.Equal(string.Empty, idInput.GetAttribute("value") ?? string.Empty);
        var nameInput = cut.FindAll("input.cfg-input")
            .FirstOrDefault(i => i.GetAttribute("value") == "Copy of Source Bot");
        Assert.NotNull(nameInput);
    }

    [Fact]
    public void AllowedModels_RendersCheckboxes_WhenModelsAvailable()
    {
        var agentJson = JsonSerializer.Serialize(new
        {
            agentId = "bot-1",
            displayName = "Bot One",
            enabled = true,
            apiProvider = "openai",
            modelId = "gpt-4",
            allowedModelIds = new[] { "gpt-4" }
        });
        var modelsJson = JsonSerializer.Serialize(new[]
        {
            new { name = "GPT-4", modelId = "gpt-4", id = "gpt-4", provider = "openai" },
            new { name = "GPT-4.1", modelId = "gpt-4.1", id = "gpt-4.1", provider = "openai" }
        });
        _httpHandler.SetupResponse("/api/agents/bot-1", agentJson);
        _httpHandler.SetupResponse("/api/agents", "[]");
        _httpHandler.SetupResponse("/api/providers", "[]");
        _httpHandler.SetupResponse("/api/models", modelsJson);

        var cut = _ctx.Render<AgentDetailPanel>(p => p.Add(c => c.AgentId, "bot-1"));

        cut.WaitForState(() => cut.Markup.Contains("Allowed Models"), TimeSpan.FromSeconds(3));

        Assert.Contains("Allowed Models", cut.Markup);
        var checkboxes = cut.FindAll(".agent-checkbox-item input[type=checkbox]");
        Assert.Equal(2, checkboxes.Count);
        // gpt-4 is pre-selected from allowedModelIds.
        Assert.Contains(checkboxes, cb => cb.HasAttribute("checked"));
    }

    /// <summary>Simple mock HTTP handler reused from the AgentDetailPanelTests pattern.</summary>
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
