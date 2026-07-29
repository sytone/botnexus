using System.Net;
using System.Text;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// bUnit coverage for the portal Home page (issue #2037) - the centralized "start a conversation"
/// landing surface now mounted at the root route. These tests lock the behaviours the issue calls
/// out explicitly: world-default-agent preselection with a safe fallback, provider-scoped model
/// choices that reset when the agent changes (unless the citizen deliberately picked another valid
/// model), capability-aware override controls, Enter/Send submission through the merged #2036
/// orchestration, navigation to the canonical chat route, and - critically - the sad paths where a
/// failed start must NOT lose the drafted message and a second submit must be blocked while the
/// first is still in flight.
/// </summary>
public sealed class LandingPageTests : IDisposable
{
    private const string ApiBase = "http://localhost/api/";

    private readonly BunitContext _ctx = new();
    private readonly IPortalLoadService _portalLoad = Substitute.For<IPortalLoadService>();
    private readonly IClientStateStore _store = Substitute.For<IClientStateStore>();
    private readonly IGatewayRestClient _rest = Substitute.For<IGatewayRestClient>();
    private readonly IStartConversationService _start = Substitute.For<IStartConversationService>();
    private readonly StubModelOptionsProvider _models = new();
    private readonly StubBackendHandler _backend = new();

    public LandingPageTests()
    {
        _portalLoad.IsReady.Returns(true);
        _portalLoad.IsLoading.Returns(false);
        _portalLoad.LoadError.Returns((string?)null);
        _portalLoad.InitializeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        _rest.ApiBaseUrl.Returns(ApiBase);

        SeedAgents(("alpha", "Alpha"), ("beta", "Beta"));

        _backend.DefaultAgentId = "beta";
        _backend.AgentConfigs["alpha"] = ("openai", "gpt-4o");
        _backend.AgentConfigs["beta"] = ("anthropic", "claude-sonnet-4");

        _models.Models["openai"] =
        [
            new ModelOption("gpt-4o", "GPT-4o", [], []),
            new ModelOption("gpt-4o-mini", "GPT-4o mini", [], []),
        ];
        _models.Models["anthropic"] =
        [
            new ModelOption("claude-sonnet-4", "Claude Sonnet 4", ["low", "high"], [200000]),
            new ModelOption("claude-opus-4", "Claude Opus 4", ["medium", "max"], [200000, 1000000]),
        ];

        var http = new HttpClient(_backend) { BaseAddress = new Uri("http://localhost/") };

        _ctx.Services.AddSingleton(_portalLoad);
        _ctx.Services.AddSingleton(_store);
        _ctx.Services.AddSingleton(_rest);
        _ctx.Services.AddSingleton(_start);
        _ctx.Services.AddSingleton<IModelOptionsProvider>(_models);
        _ctx.Services.AddSingleton(http);
        _ctx.Services.AddSingleton(new GatewayInfoService(http, _rest));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private void SeedAgents(params (string Id, string Name)[] agents)
    {
        var dict = agents.ToDictionary(
            a => a.Id,
            a => new AgentState { AgentId = a.Id, DisplayName = a.Name },
            StringComparer.Ordinal);
        _store.Agents.Returns(dict);
        foreach (var (id, _) in agents)
            _store.GetAgent(id).Returns(dict[id]);
    }

    private IRenderedComponent<Landing> RenderPage()
    {
        var cut = _ctx.Render<Landing>();
        cut.WaitForState(() => cut.FindAll("[data-testid='home-model-select'] option").Count > 0);
        return cut;
    }

    /// <summary>
    /// Reads the value of the option actually marked <c>selected</c>. Asserting this (rather than a
    /// <c>value</c> attribute on the &lt;select&gt;, which browsers ignore) is what proves
    /// preselection really works in a real browser.
    /// </summary>
    private static string? SelectedValue(IRenderedComponent<Landing> cut, string testId) =>
        cut.FindAll($"[data-testid='{testId}'] option")
            .FirstOrDefault(o => o.HasAttribute("selected"))
            ?.GetAttribute("value");

    /// <summary>
    /// Current draft text. Blazor renders a bound &lt;textarea&gt; through its <c>value</c> attribute
    /// rather than as child text, so read the attribute (falling back to text content).
    /// </summary>
    private static string DraftText(IRenderedComponent<Landing> cut)
    {
        var textarea = cut.Find("textarea[data-testid='home-message-input']");
        return textarea.GetAttribute("value") ?? textarea.TextContent;
    }

    private void ArrangeSuccessfulStart(string agentId = "beta", string conversationId = "conv-9")
    {
        _start.StartAsync(Arg.Any<StartConversationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(StartConversationResult.Started(agentId, conversationId, null)));
    }

    // -- Loading / summary ----------------------------------------------------------

    [Fact]
    public void Shows_connecting_spinner_when_portal_not_ready()
    {
        _portalLoad.IsReady.Returns(false);

        var cut = _ctx.Render<Landing>();

        cut.Find(".portal-loading");
        Assert.Contains("Connecting", cut.Markup);
    }

    [Fact]
    public void Shows_load_error_when_portal_load_failed()
    {
        _portalLoad.IsReady.Returns(false);
        _portalLoad.LoadError.Returns("Connection refused");

        var cut = _ctx.Render<Landing>();

        cut.Find(".portal-load-error");
        Assert.Contains("Connection refused", cut.Markup);
    }

    [Fact]
    public void Renders_site_and_agent_summary_reusing_the_platform_stats_panel()
    {
        var cut = RenderPage();

        cut.Find("[data-testid='home-summary']");
        // Reuses the existing stats pipeline rather than adding a second one.
        cut.Find("[data-testid='platform-stats-panel']");
        cut.Find("[data-testid='home-agent-count']");
    }

    [Fact]
    public void Renders_conversation_starter_controls()
    {
        var cut = RenderPage();

        cut.Find("[data-testid='home-page']");
        cut.Find("textarea[data-testid='home-message-input']");
        cut.Find("[data-testid='home-agent-select']");
        cut.Find("[data-testid='home-model-select']");
        cut.Find("[data-testid='home-send']");
    }

    [Fact]
    public void Renders_empty_agent_state_when_no_agents_are_available()
    {
        SeedAgents();

        var cut = _ctx.Render<Landing>();

        cut.Find("[data-testid='home-no-agents']");
        Assert.Empty(cut.FindAll("[data-testid='home-send']"));
    }

    // -- Agent + model preselection -------------------------------------------------

    [Fact]
    public void Preselects_the_configured_world_default_agent()
    {
        var cut = RenderPage();

        Assert.Equal("beta", SelectedValue(cut, "home-agent-select"));
    }

    [Fact]
    public void Falls_back_to_the_first_agent_when_the_configured_default_is_unavailable()
    {
        _backend.DefaultAgentId = "ghost-agent-not-in-roster";

        var cut = RenderPage();

        Assert.Equal("alpha", SelectedValue(cut, "home-agent-select"));
    }

    [Fact]
    public void Falls_back_to_the_first_agent_when_no_default_is_configured()
    {
        _backend.DefaultAgentId = null;

        var cut = RenderPage();

        Assert.Equal("alpha", SelectedValue(cut, "home-agent-select"));
    }

    [Fact]
    public void Applies_the_configured_default_agent_even_when_gateway_info_resolves_after_the_roster()
    {
        // Regression (#2037): live gateways populate the agent roster over the hub BEFORE the
        // gateway info document (which carries defaultAgentId) has been fetched. The first render
        // therefore falls back to the alphabetically-first agent; that fallback is provisional and
        // must yield to the configured world default once it becomes known. Observed against a live
        // gateway with defaultAgentId=nova preselecting "assistant" instead.
        // Reproduce the live ordering: the very first info fetch happens before the hub handshake has
        // published the API base URL, so it yields nothing and the page can only fall back to the
        // first agent. defaultAgentId only becomes reachable once the base URL exists.
        _rest.ApiBaseUrl.Returns((string?)null);

        var cut = _ctx.Render<Landing>();
        cut.WaitForAssertion(() => Assert.Equal("alpha", SelectedValue(cut, "home-agent-select")));

        // Hub handshake completes: the base URL appears and a roster push arrives. The provisional
        // fallback must now yield to the configured world default.
        _rest.ApiBaseUrl.Returns(ApiBase);
        _store.OnChanged += Raise.Event<Action>();

        // Explicit timeout: this path chains the gated info fetch, the agent-config fetch and two
        // roster refreshes, which exceeds bUnit's 1s default when the whole suite is running.
        cut.WaitForAssertion(
            () => Assert.Equal("beta", SelectedValue(cut, "home-agent-select")),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void A_deliberate_agent_pick_is_not_overridden_by_the_configured_default()
    {
        var cut = RenderPage();

        cut.Find("[data-testid='home-agent-select']").Change("alpha");
        cut.WaitForState(() => SelectedValue(cut, "home-model-select") == "gpt-4o");

        // A background roster refresh must not drag the selection back to the world default.
        _store.OnChanged += Raise.Event<Action>();

        cut.WaitForAssertion(() => Assert.Equal("alpha", SelectedValue(cut, "home-agent-select")));
    }

    [Fact]
    public void Preselects_the_selected_agents_configured_model()
    {
        var cut = RenderPage();

        Assert.Equal("claude-sonnet-4", SelectedValue(cut, "home-model-select"));
    }

    [Fact]
    public void Model_choices_are_scoped_to_the_selected_agents_provider()
    {
        var cut = RenderPage();

        var values = cut.FindAll("[data-testid='home-model-select'] option")
            .Select(o => o.GetAttribute("value"))
            .ToList();

        Assert.Equal(["claude-sonnet-4", "claude-opus-4"], values);
        Assert.DoesNotContain("gpt-4o", values);
    }

    [Fact]
    public void Changing_the_agent_refreshes_models_and_resets_to_that_agents_default()
    {
        var cut = RenderPage();

        cut.Find("[data-testid='home-agent-select']").Change("alpha");

        cut.WaitForState(() => SelectedValue(cut, "home-model-select") == "gpt-4o");
        var values = cut.FindAll("[data-testid='home-model-select'] option")
            .Select(o => o.GetAttribute("value"))
            .ToList();
        Assert.Equal(["gpt-4o", "gpt-4o-mini"], values);
    }

    [Fact]
    public void Deliberate_model_choice_survives_when_it_is_still_valid_for_the_new_agent()
    {
        // Both providers publish "shared-model", so a deliberate pick stays honoured across an
        // agent change; only a selection invalid for the new provider resets to the agent default.
        _models.Models["openai"] = [.. _models.Models["openai"], new ModelOption("shared-model", "Shared", [], [])];
        _models.Models["anthropic"] = [.. _models.Models["anthropic"], new ModelOption("shared-model", "Shared", [], [])];

        var cut = RenderPage();
        cut.WaitForState(() => cut.FindAll("[data-testid='home-model-select'] option").Count == 3);

        cut.Find("[data-testid='home-model-select']").Change("shared-model");
        cut.Find("[data-testid='home-agent-select']").Change("alpha");

        cut.WaitForState(() =>
            cut.FindAll("[data-testid='home-model-select'] option").Any(o => o.GetAttribute("value") == "gpt-4o-mini"));
        Assert.Equal("shared-model", SelectedValue(cut, "home-model-select"));
    }

    // -- Capability-aware override controls -----------------------------------------

    [Fact]
    public void Shows_capability_controls_only_when_the_model_metadata_supports_them()
    {
        var cut = RenderPage();
        cut.WaitForState(() => cut.FindAll("[data-testid='home-thinking-select']").Count > 0);

        // claude-sonnet-4 advertises thinking levels but only one context size.
        var thinking = cut.FindAll("[data-testid='home-thinking-select'] option")
            .Select(o => o.GetAttribute("value"))
            .ToList();
        Assert.Contains("low", thinking);
        Assert.Contains("high", thinking);
        Assert.Empty(cut.FindAll("[data-testid='home-context-select']"));

        // claude-opus-4 advertises two context sizes, so the context control appears.
        cut.Find("[data-testid='home-model-select']").Change("claude-opus-4");
        cut.WaitForState(() => cut.FindAll("[data-testid='home-context-select']").Count > 0);
        var contexts = cut.FindAll("[data-testid='home-context-select'] option")
            .Select(o => o.GetAttribute("value"))
            .ToList();
        Assert.Contains("200000", contexts);
        Assert.Contains("1000000", contexts);
    }

    [Fact]
    public void Hides_capability_controls_when_the_model_publishes_no_metadata()
    {
        var cut = RenderPage();

        cut.Find("[data-testid='home-agent-select']").Change("alpha");

        cut.WaitForState(() => SelectedValue(cut, "home-model-select") == "gpt-4o");
        Assert.Empty(cut.FindAll("[data-testid='home-thinking-select']"));
        Assert.Empty(cut.FindAll("[data-testid='home-context-select']"));
    }

    [Fact]
    public void Selected_capability_overrides_are_passed_to_the_start_orchestration()
    {
        ArrangeSuccessfulStart();
        var cut = RenderPage();

        cut.WaitForState(() => cut.FindAll("[data-testid='home-thinking-select']").Count > 0);
        cut.Find("[data-testid='home-thinking-select']").Change("high");
        cut.Find("[data-testid='home-message-input']").Input("think hard");
        cut.Find("[data-testid='home-send']").Click();

        cut.WaitForAssertion(() => _start.Received(1).StartAsync(
            Arg.Is<StartConversationRequest>(r => r.SelectedThinking == "high"),
            Arg.Any<CancellationToken>()));
    }

    // -- Submission (happy path) ----------------------------------------------------

    [Fact]
    public void Send_starts_the_conversation_through_the_shared_orchestration()
    {
        ArrangeSuccessfulStart();
        var cut = RenderPage();

        cut.Find("[data-testid='home-message-input']").Input("hello world");
        cut.Find("[data-testid='home-send']").Click();

        cut.WaitForAssertion(() => _start.Received(1).StartAsync(
            Arg.Is<StartConversationRequest>(r =>
                r.AgentId == "beta" &&
                r.FirstMessage == "hello world" &&
                r.SelectedModel == "claude-sonnet-4" &&
                r.AgentDefaultModel == "claude-sonnet-4"),
            Arg.Any<CancellationToken>()));
    }

    [Fact]
    public void Send_passes_a_deliberately_selected_model_so_it_is_persisted_as_the_override()
    {
        ArrangeSuccessfulStart();
        var cut = RenderPage();

        cut.Find("[data-testid='home-model-select']").Change("claude-opus-4");
        cut.Find("[data-testid='home-message-input']").Input("switch me");
        cut.Find("[data-testid='home-send']").Click();

        cut.WaitForAssertion(() => _start.Received(1).StartAsync(
            Arg.Is<StartConversationRequest>(r =>
                r.SelectedModel == "claude-opus-4" && r.AgentDefaultModel == "claude-sonnet-4"),
            Arg.Any<CancellationToken>()));
    }

    [Fact]
    public void Enter_submits_and_shift_enter_does_not()
    {
        ArrangeSuccessfulStart();
        var cut = RenderPage();
        cut.Find("[data-testid='home-message-input']").Input("via keyboard");

        cut.Find("[data-testid='home-message-input']").KeyDown(new KeyboardEventArgs { Key = "Enter", ShiftKey = true });
        _start.DidNotReceiveWithAnyArgs().StartAsync(default!, default);

        cut.Find("[data-testid='home-message-input']").KeyDown(new KeyboardEventArgs { Key = "Enter", ShiftKey = false });
        cut.WaitForAssertion(() => _start.ReceivedWithAnyArgs(1).StartAsync(default!, default));
    }

    [Fact]
    public void Successful_start_navigates_to_the_canonical_chat_route()
    {
        ArrangeSuccessfulStart("beta", "conv-9");
        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        var cut = RenderPage();

        cut.Find("[data-testid='home-message-input']").Input("go");
        cut.Find("[data-testid='home-send']").Click();

        cut.WaitForAssertion(() => Assert.EndsWith("/chat/beta/conv-9", nav.Uri, StringComparison.Ordinal));
    }

    [Fact]
    public void Successful_start_clears_the_draft()
    {
        ArrangeSuccessfulStart();
        var cut = RenderPage();

        cut.Find("[data-testid='home-message-input']").Input("consume me");
        cut.Find("[data-testid='home-send']").Click();

        cut.WaitForAssertion(() => Assert.Equal(string.Empty, DraftText(cut)));
    }

    // -- Submission (sad paths) -----------------------------------------------------

    [Fact]
    public void Empty_input_does_not_start_a_conversation()
    {
        var cut = RenderPage();

        cut.Find("[data-testid='home-message-input']").Input("   ");
        cut.Find("[data-testid='home-message-input']").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        _start.DidNotReceiveWithAnyArgs().StartAsync(default!, default);
        Assert.True(cut.Find("[data-testid='home-send']").HasAttribute("disabled"));
    }

    [Fact]
    public void Failed_start_shows_an_actionable_error_and_preserves_the_drafted_message()
    {
        _start.StartAsync(Arg.Any<StartConversationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(StartConversationResult.Failed("Gateway unreachable")));
        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        var startUri = nav.Uri;
        var cut = RenderPage();

        cut.Find("[data-testid='home-message-input']").Input("precious draft");
        cut.Find("[data-testid='home-send']").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("Gateway unreachable", cut.Find("[data-testid='home-error']").TextContent));
        // The draft must survive so the citizen can retry without retyping.
        Assert.Equal("precious draft", DraftText(cut));
        Assert.Equal(startUri, nav.Uri);
    }

    [Fact]
    public void Thrown_start_failure_shows_an_error_and_preserves_the_drafted_message()
    {
        _start.StartAsync(Arg.Any<StartConversationRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<StartConversationResult>>(_ => throw new HttpRequestException("boom"));
        var cut = RenderPage();

        cut.Find("[data-testid='home-message-input']").Input("still here");
        cut.Find("[data-testid='home-send']").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid='home-error']"));
        Assert.Equal("still here", DraftText(cut));
    }

    [Fact]
    public void Duplicate_submission_is_blocked_while_a_start_is_in_progress()
    {
        var gate = new TaskCompletionSource<StartConversationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _start.StartAsync(Arg.Any<StartConversationRequest>(), Arg.Any<CancellationToken>()).Returns(gate.Task);
        var cut = RenderPage();

        cut.Find("[data-testid='home-message-input']").Input("only once");
        cut.Find("[data-testid='home-send']").Click();

        // Send is disabled while in flight, and a second click / Enter cannot re-enter the workflow.
        cut.WaitForAssertion(() =>
            Assert.True(cut.Find("[data-testid='home-send']").HasAttribute("disabled")));
        cut.Find("[data-testid='home-send']").Click();
        cut.Find("[data-testid='home-message-input']").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        _start.ReceivedWithAnyArgs(1).StartAsync(default!, default);

        gate.SetResult(StartConversationResult.Failed("nope"));
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid='home-send']").HasAttribute("disabled")));
    }

    [Fact]
    public void Send_is_disabled_until_a_message_is_drafted()
    {
        var cut = RenderPage();

        Assert.True(cut.Find("[data-testid='home-send']").HasAttribute("disabled"));

        cut.Find("[data-testid='home-message-input']").Input("now enabled");

        Assert.False(cut.Find("[data-testid='home-send']").HasAttribute("disabled"));
    }

    // -- Stubs ----------------------------------------------------------------------

    /// <summary>
    /// In-memory <see cref="IModelOptionsProvider"/> so tests drive provider-scoped model choices
    /// (and their capability metadata) without an HTTP round trip.
    /// </summary>
    private sealed class StubModelOptionsProvider : IModelOptionsProvider
    {
        public Dictionary<string, IReadOnlyList<ModelOption>> Models { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<ModelOption>> GetModelsAsync(string provider) =>
            Task.FromResult(Models.TryGetValue(provider, out var models) ? models : []);
    }

    /// <summary>
    /// Minimal gateway stand-in for the two reads the Home page performs: the gateway info document
    /// (which carries the world default agent id merged by #2035) and each agent's configured
    /// provider/model.
    /// </summary>
    private sealed class StubBackendHandler : HttpMessageHandler
    {
        public string? DefaultAgentId { get; set; }

        public Dictionary<string, (string Provider, string Model)> AgentConfigs { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            string json;

            if (path.EndsWith("/gateway/info", StringComparison.Ordinal))
            {
                var defaultAgent = DefaultAgentId is null ? "null" : $"\"{DefaultAgentId}\"";
                json = $$"""
                    {
                      "startedAt": "2026-01-01T00:00:00+00:00",
                      "uptimeSeconds": 1,
                      "commitSha": "abc123",
                      "commitShort": "abc123",
                      "version": "1.0.0",
                      "defaultAgentId": {{defaultAgent}}
                    }
                    """;
            }
            else if (path.Contains("/agents/", StringComparison.Ordinal))
            {
                var agentId = path[(path.LastIndexOf('/') + 1)..];
                if (!AgentConfigs.TryGetValue(agentId, out var config))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

                json = $$"""
                    { "agentId": "{{agentId}}", "apiProvider": "{{config.Provider}}", "modelId": "{{config.Model}}" }
                    """;
            }
            else
            {
                json = "{}";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
