using System.Net;
using System.Text.Json;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// #2795: the Agent Configuration panel rendered <c>MODEL —</c>, <c>PROVIDER —</c>,
/// <c>TOOL COUNT 0</c> and <c>SYSTEM PROMPT SIZE —</c> for every agent because its private DTOs
/// declared field names that appeared in neither endpoint payload. These tests drive the panel
/// against payloads shaped exactly like the real endpoints.
/// </summary>
public sealed class AgentConfigPanelTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly StubHandler _http = new();
    private readonly ClientStateStore _store = new();

    public AgentConfigPanelTests()
    {
        var rest = Substitute.For<IGatewayRestClient>();
        rest.ApiBaseUrl.Returns("http://localhost/api/");
        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(rest);
        _ctx.Services.AddSingleton(new HttpClient(_http) { BaseAddress = new Uri("http://localhost/") });
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private const string AgentId = "farnsworth";

    /// <summary>Exactly the shape <c>AgentsController.Get</c> serializes from an AgentDescriptor.</summary>
    private static string DescriptorJson => JsonSerializer.Serialize(new
    {
        agentId = AgentId,
        displayName = "Farnsworth",
        modelId = "claude-opus-5",
        apiProvider = "github-copilot",
        toolIds = new[] { "read", "write", "shell" },
        memory = new { enabled = true },
        heartbeat = new { enabled = true, intervalMinutes = 45 },
    });

    /// <summary>Exactly the nested shape <c>AgentsController.GetContext</c> serializes.</summary>
    private static string ContextJson => JsonSerializer.Serialize(new
    {
        agentId = AgentId,
        sessionId = "sess-1",
        totalEstimatedTokens = 20000,
        sections = new
        {
            systemPrompt = new { tokens = 12345, chars = 50000 },
            toolDefinitions = new { tokens = 900, toolCount = 3 },
            conversationHistory = new { tokens = 400, entryCount = 5 },
        },
    });

    private void SeedAgentWithConversation(string conversationId = "conv-abc", string? sessionId = "sess-1")
    {
        _store.SeedAgents([new AgentSummary(AgentId, "Farnsworth")]);
        _store.SeedConversations(AgentId, [new ConversationSummaryDto(
            conversationId, AgentId, "Chat", true, "Active", sessionId, 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]);
        _store.SetActiveConversation(AgentId, conversationId);
    }

    private async Task<IRenderedComponent<AgentConfigPanel>> OpenAsync()
    {
        var cut = _ctx.Render<AgentConfigPanel>(p => p.Add(c => c.AgentId, AgentId));
        await cut.InvokeAsync(() => cut.Instance.Open());
        return cut;
    }

    // ── AC1: model and provider ──────────────────────────────────────────────

    [Fact]
    public async Task Panel_shows_model_from_the_descriptors_modelId_field()
    {
        _http.Setup("/api/agents/farnsworth", DescriptorJson);
        SeedAgentWithConversation();

        var cut = await OpenAsync();

        cut.Find("[data-field='model'] .config-value").TextContent.Trim().ShouldBe("claude-opus-5");
    }

    [Fact]
    public async Task Panel_shows_provider_from_the_descriptors_apiProvider_field()
    {
        _http.Setup("/api/agents/farnsworth", DescriptorJson);
        SeedAgentWithConversation();

        var cut = await OpenAsync();

        cut.Find("[data-field='provider'] .config-value").TextContent.Trim().ShouldBe("github-copilot");
    }

    // ── AC2: tool count ──────────────────────────────────────────────────────

    [Fact]
    public async Task Panel_shows_tool_count_equal_to_the_number_of_toolIds()
    {
        _http.Setup("/api/agents/farnsworth", DescriptorJson);
        SeedAgentWithConversation();

        var cut = await OpenAsync();

        cut.Find("[data-field='toolCount'] .config-value").TextContent.Trim().ShouldBe("3");
    }

    [Fact]
    public async Task Panel_shows_tool_count_as_unavailable_when_the_payload_omits_toolIds()
    {
        // A missing field must NEVER read as the measurement "0" - that is the #2795 misreading.
        _http.Setup("/api/agents/farnsworth", JsonSerializer.Serialize(new { agentId = AgentId }));
        SeedAgentWithConversation();

        var cut = await OpenAsync();

        cut.Find("[data-field='toolCount'] .config-value").TextContent.Trim()
            .ShouldBe(AgentConfigField.Unavailable);
    }

    // ── AC3: system prompt size, from the NESTED shape ───────────────────────

    [Fact]
    public async Task Panel_shows_system_prompt_size_from_nested_sections_systemPrompt_tokens()
    {
        _http.Setup("/api/agents/farnsworth", DescriptorJson);
        _http.Setup("/sessions/sess-1/context", ContextJson);
        SeedAgentWithConversation();

        var cut = await OpenAsync();

        cut.Find("[data-field='systemPromptSize'] .config-value").TextContent.Trim()
            .ShouldBe("12,345 tokens");
    }

    // ── AC4: conversation ID ─────────────────────────────────────────────────

    [Fact]
    public async Task Panel_shows_the_active_conversation_id_next_to_the_session_id()
    {
        _http.Setup("/api/agents/farnsworth", DescriptorJson);
        SeedAgentWithConversation("conv-abc");

        var cut = await OpenAsync();

        var conv = cut.Find("[data-field='conversationId'] .config-value");
        conv.TextContent.Trim().ShouldBe("conv-abc");
        // Same monospace treatment as the session ID (AC4).
        conv.ClassList.ShouldContain("mono");
        cut.Find("[data-field='sessionId'] .config-value").ClassList.ShouldContain("mono");

        // Adjacency: conversation ID immediately precedes session ID.
        var keys = cut.FindAll("[data-field]").Select(e => e.GetAttribute("data-field")).ToList();
        keys.IndexOf("sessionId").ShouldBe(keys.IndexOf("conversationId") + 1);
    }

    [Fact]
    public async Task Panel_shows_a_clear_placeholder_when_there_is_no_active_conversation()
    {
        _http.Setup("/api/agents/farnsworth", DescriptorJson);
        _store.SeedAgents([new AgentSummary(AgentId, "Farnsworth")]);

        var cut = await OpenAsync();

        cut.Find("[data-field='conversationId'] .config-value").TextContent.Trim()
            .ShouldBe(AgentConfigSnapshotBuilder.NoActiveConversation);
    }

    // ── AC5/AC6: copy ────────────────────────────────────────────────────────

    [Fact]
    public async Task Copy_button_is_present_and_copies_a_structured_payload()
    {
        _http.Setup("/api/agents/farnsworth", DescriptorJson);
        _http.Setup("/sessions/sess-1/context", ContextJson);
        SeedAgentWithConversation();
        var invocation = _ctx.JSInterop.Setup<bool>("BotNexus.copyToClipboard", _ => true).SetResult(true);

        var cut = await OpenAsync();
        await cut.Find(".config-copy-btn").ClickAsync(new());

        var payload = (string)invocation.Invocations.Single().Arguments[0]!;
        using var doc = JsonDocument.Parse(payload);
        var fields = doc.RootElement.GetProperty("fields");
        fields.GetProperty("agentId").GetString().ShouldBe(AgentId);
        fields.GetProperty("conversationId").GetString().ShouldBe("conv-abc");
        fields.GetProperty("sessionId").GetString().ShouldBe("sess-1");
        fields.GetProperty("model").GetString().ShouldBe("claude-opus-5");
        fields.GetProperty("provider").GetString().ShouldBe("github-copilot");
        fields.GetProperty("toolCount").GetString().ShouldBe("3");
        fields.GetProperty("systemPromptSize").GetString().ShouldBe("12,345 tokens");
    }

    [Fact]
    public async Task Copied_payload_contains_exactly_the_values_the_panel_rendered()
    {
        // AC6 is satisfied BY CONSTRUCTION (one AgentConfigSnapshot feeds both paths); this test
        // is the regression fence against someone reintroducing a second field list for the copy.
        _http.Setup("/api/agents/farnsworth", DescriptorJson);
        _http.Setup("/sessions/sess-1/context", ContextJson);
        SeedAgentWithConversation();
        var invocation = _ctx.JSInterop.Setup<bool>("BotNexus.copyToClipboard", _ => true).SetResult(true);

        var cut = await OpenAsync();
        await cut.Find(".config-copy-btn").ClickAsync(new());

        var payload = (string)invocation.Invocations.Single().Arguments[0]!;
        using var doc = JsonDocument.Parse(payload);
        var copied = doc.RootElement.GetProperty("fields");

        var rendered = cut.FindAll("[data-field]")
            .ToDictionary(
                e => e.GetAttribute("data-field")!,
                e => e.QuerySelector(".config-value")!.TextContent.Trim(),
                StringComparer.Ordinal);

        rendered.ShouldNotBeEmpty();
        foreach (var (key, value) in rendered)
            copied.GetProperty(key).GetString().ShouldBe(value, $"copied '{key}' must equal the rendered value");
        copied.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
            .ShouldBe(rendered.Keys.ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>Path-suffix keyed stub, matching the pattern used elsewhere in this suite.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responses = new(StringComparer.OrdinalIgnoreCase);

        public void Setup(string pathSuffix, string json) => _responses[pathSuffix] = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            // Longest key first so "/api/agents/farnsworth" does not swallow a context request.
            foreach (var key in _responses.Keys.OrderByDescending(k => k.Length))
            {
                if (path.Contains(key, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(_responses[key], System.Text.Encoding.UTF8, "application/json"),
                    });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    // Twice an operator read the three empty rows as "the agent failed to start" and asked why it
    // was broken. It was not: an agent nobody has messaged has no conversation, so those rows are
    // empty by design. The note is what rules that reading out.
    [Fact]
    public async Task Says_the_agent_is_fine_when_no_conversation_is_open()
    {
        _http.Setup("/api/agents/farnsworth", DescriptorJson);
        _store.SeedAgents([new AgentSummary(AgentId, "Farnsworth")]);

        var cut = await OpenAsync();

        var note = cut.Find("[data-testid='agent-config-no-conversation']").TextContent;
        Assert.Contains("loaded and ready", note);
        Assert.Contains("once you send it a message", note);
    }

    // And gets out of the way once there IS one - otherwise it becomes furniture nobody reads.
    [Fact]
    public async Task The_note_is_absent_once_a_conversation_is_open()
    {
        _http.Setup("/api/agents/farnsworth", DescriptorJson);
        _http.Setup("/sessions/sess-1/context", ContextJson);
        SeedAgentWithConversation();

        var cut = await OpenAsync();

        Assert.Empty(cut.FindAll("[data-testid='agent-config-no-conversation']"));
    }

    // The wording must not describe the AGENT as inactive - that is the misreading itself.
    [Fact]
    public void The_placeholders_describe_the_view_not_the_agent()
    {
        var snapshot = AgentConfigSnapshotBuilder.Build(
            "agent-1", "Agent One", conversationId: null, sessionId: null,
            channelType: "signalr", descriptor: null, context: null);

        var conversation = snapshot.Field(AgentConfigSnapshotBuilder.ConversationIdKey)!.Display;
        Assert.DoesNotContain("no active", conversation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("view", conversation);
    }
}
