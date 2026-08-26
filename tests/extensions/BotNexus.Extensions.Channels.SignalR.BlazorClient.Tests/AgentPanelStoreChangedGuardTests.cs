using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// #3084: <c>AgentPanel.HandleStoreChanged</c> opens with a "nothing changed, do nothing" early-out
/// guard. The guard body was empty (the <c>return;</c> had been lost in a reformat), so the
/// <c>if</c> silently bound to the following assignment and control fell through on EVERY store
/// notification, re-parsing the URI each time.
///
/// The defect is invisible through the normal bUnit surface because <c>ApplyTabFromUri</c> is
/// idempotent for a given URI — re-running it on an unchanged URL produces an unchanged tab, so a
/// test that merely raises a store change and asserts the tab is stable passes either way and is
/// vacuous.
///
/// These tests make the re-parse OBSERVABLE by moving the URI without raising a location-changed
/// event (<see cref="SilentNavigationManager"/>). A guarded <c>HandleStoreChanged</c> never looks at
/// <c>Nav.Uri</c>, so the tab stays put; an unguarded one re-reads the moved URI and switches tab.
/// That difference is what makes the assertion able to fail — verified by removing the
/// <c>return;</c> and watching <see cref="NoOp_store_change_does_not_reapply_the_tab_from_the_uri"/>
/// go red.
/// </summary>
public sealed class AgentPanelStoreChangedGuardTests : IDisposable
{
    private const string AgentId = "agent-1";
    private const string BaseUri = "http://localhost/";
    private const string ConversationUri = "http://localhost/chat/agent-1/conv-1";

    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();
    private readonly SilentNavigationManager _nav = new(BaseUri, ConversationUri);

    public AgentPanelStoreChangedGuardTests()
    {
        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton<NavigationManager>(_nav);
        _ctx.Services.AddSingleton(Substitute.For<IPortalPreferencesService>());
        _ctx.Services.AddSingleton(Substitute.For<IAgentInteractionService>());
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp =>
            new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
        _ctx.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        _ctx.Services.AddSingleton(new HttpClient { BaseAddress = new Uri(BaseUri) });
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    /// <summary>
    /// AC1/AC2 (happy path): the classification is unchanged between the initial render and the
    /// store notification, so <c>HandleStoreChanged</c> must return immediately and do no work.
    /// The URI has been moved to <c>?tab=canvas</c> underneath the component; a guarded handler
    /// never reads it, so the Conversation tab remains active.
    ///
    /// This is the assertion that goes RED when the <c>return;</c> is removed: the unguarded
    /// handler falls through to <c>ApplyTabFromUri(Nav.Uri)</c>, parses the moved URI and activates
    /// the Canvas tab.
    /// </summary>
    [Fact]
    public void NoOp_store_change_does_not_reapply_the_tab_from_the_uri()
    {
        SeedRegularAgent();

        var cut = RenderAgentPanel();
        Assert.NotNull(cut.Find(".agent-panel-tab.active[data-tab='conversation']"));

        // Move the URL without notifying, so the ONLY thing that could pick the change up is a
        // re-parse from inside HandleStoreChanged.
        _nav.SetUriSilently(ConversationUri + "?tab=canvas");

        // A store mutation that leaves the sub-agent classification untouched. Note there is no
        // explicit re-render here: forcing one would run OnParametersSet, which re-applies the tab
        // through a DIFFERENT path and would mask what HandleStoreChanged did. A guarded handler
        // renders nothing at all; an unguarded one re-applies the tab and calls StateHasChanged
        // itself, which is precisely the difference being asserted.
        _store.NotifyChanged();

        Assert.NotNull(cut.Find(".agent-panel-tab.active[data-tab='conversation']"));
        Assert.Empty(cut.FindAll(".agent-panel-tab.active[data-tab='canvas']"));
    }

    /// <summary>
    /// AC2 (sad path / non-vacuity anchor): when the classification DOES change — agent data
    /// arriving after the first render flips it from "unknown" to "not a sub-agent" — the guard must
    /// NOT short-circuit, and the tab is re-applied from the URI exactly as the surrounding comment
    /// intends.
    ///
    /// Without this test the happy-path assertion above could be satisfied by a handler that never
    /// re-applies the tab at all (or by a silent URI move that is simply not observable), which
    /// would pin the wrong contract. It proves the moved URI IS reachable through this code path.
    /// </summary>
    [Fact]
    public void Store_change_that_alters_the_classification_still_reapplies_the_tab()
    {
        // No agent in the store yet, so the first render records a null classification.
        var cut = RenderAgentPanel();
        Assert.NotNull(cut.Find(".agent-panel-tab.active[data-tab='conversation']"));

        _nav.SetUriSilently(ConversationUri + "?tab=canvas");

        // Agent data arrives: null -> false is a real classification change, so the guard must let
        // control through.
        SeedRegularAgent();
        _store.NotifyChanged();

        cut.WaitForAssertion(() =>
            Assert.NotNull(cut.Find(".agent-panel-tab.active[data-tab='canvas']")));
    }

    /// <summary>
    /// AC1/AC3 (structural): pins the restored <c>return;</c> and the absence of the whitespace-only
    /// residue it left behind. The behavioural tests above are the primary contract, but the shape
    /// of this defect — an <c>if</c> whose body is empty, which the compiler accepts silently
    /// because the next statement becomes the body — is invisible to both the compiler and review,
    /// so it is worth pinning directly.
    /// </summary>
    [Fact]
    public void HandleStoreChanged_guard_body_contains_the_return()
    {
        var source = File.ReadAllText(AgentPanelRazorPath());

        var start = source.IndexOf("private void HandleStoreChanged()", StringComparison.Ordinal);
        Assert.True(start >= 0, "HandleStoreChanged was not found in AgentPanel.razor.");

        var end = source.IndexOf("private bool IsActiveTab", start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not delimit the HandleStoreChanged body.");

        var body = source[start..end];

        var guard = body.IndexOf("if (currentIsSubAgent == _lastIsSubAgent)", StringComparison.Ordinal);
        Assert.True(guard >= 0, "The early-out guard was not found in HandleStoreChanged.");

        var assignment = body.IndexOf("_lastIsSubAgent = currentIsSubAgent;", guard, StringComparison.Ordinal);
        Assert.True(assignment > guard, "The classification assignment was not found after the guard.");

        // The return must sit BETWEEN the guard and the assignment — i.e. it is the guard's body,
        // not some later statement.
        var guardBody = body[guard..assignment];
        Assert.Contains("return;", guardBody, StringComparison.Ordinal);

        // AC3: no whitespace-only line survives INSIDE the guard body. The window runs from the
        // `if` to its closing brace; the final split segment is the indentation that PRECEDES that
        // brace on its own line, not a line of its own, so it is dropped rather than counted.
        var close = guardBody.IndexOf('}');
        Assert.True(close > 0, "The guard body is not brace-delimited.");
        var guardLines = guardBody[..close].Split('\n');
        Assert.DoesNotContain(guardLines[..^1], line => line.Trim('\r').Length > 0 && line.Trim().Length == 0);
    }

    private IRenderedComponent<AgentPanel> RenderAgentPanel() =>
        _ctx.Render<AgentPanel>(p => p.Add(c => c.AgentId, AgentId));

    private void SeedRegularAgent()
    {
        _store.UpsertAgent(new AgentState
        {
            AgentId = AgentId,
            DisplayName = "Alpha",
            IsObserverAgent = false,
            IsConnected = true
        });
    }

    private static string AgentPanelRazorPath() => Path.Combine(
        FindRepositoryRoot(),
        "src",
        "extensions",
        "BotNexus.Extensions.Channels.SignalR.BlazorClient",
        "Components",
        "AgentPanel.razor");

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Directory.Packages.props")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate Directory.Packages.props from test base directory.");
    }

    /// <summary>
    /// A <see cref="NavigationManager"/> that can move its <see cref="NavigationManager.Uri"/>
    /// WITHOUT raising <c>LocationChanged</c>. bUnit's own navigation manager always notifies, which
    /// would drive <c>HandleLocationChanged</c> and re-apply the tab through a different code path
    /// entirely — masking whether <c>HandleStoreChanged</c> re-parsed anything.
    /// </summary>
    private sealed class SilentNavigationManager : NavigationManager
    {
        public SilentNavigationManager(string baseUri, string uri) => Initialize(baseUri, uri);

        public void SetUriSilently(string uri) => Uri = uri;

        protected override void NavigateToCore(string uri, bool forceLoad) =>
            Uri = ToAbsoluteUri(uri).ToString();
    }
}
