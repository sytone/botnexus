using Bunit;
using Microsoft.AspNetCore.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// bUnit tests for the per-session tool overlay control (issue #3271), the portal half of the
/// gateway-side overlay shipped with #2523.
/// </summary>
/// <remarks>
/// The assertions here are written against the SIX acceptance criteria of #3271 and each test names
/// the clause it pins:
/// <list type="number">
/// <item>a persisted overlay renders its current state without running <c>/tools</c>;</item>
/// <item>no overlay renders as unrestricted with no visual noise;</item>
/// <item>the panel can set AND clear the overlay, and the write is a PERSISTED one (it goes to the
/// REST boundary, not to component-local state);</item>
/// <item>it writes through the EXISTING <c>PUT /api/conversations/{id}/override</c> endpoint;</item>
/// <item>no affordance grants a tool the agent does not have, and a refused entry is displayed as
/// refused rather than as granted.</item>
/// </list>
/// Clause 6 (UI evidence) is discharged on the PR, not here.
/// </remarks>
public sealed class ConversationToolOverlayPanelTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IGatewayRestClient _restClient = Substitute.For<IGatewayRestClient>();

    private static readonly string[] AgentTools = ["read", "write", "exec", "shell"];

    public ConversationToolOverlayPanelTests()
        => _ctx.Services.AddSingleton(_restClient);

    public void Dispose() => _ctx.Dispose();

    private IRenderedComponent<ConversationToolOverlayPanel> Render(
        string? overlayJson = null,
        IReadOnlyList<string>? agentTools = null)
        => _ctx.Render<ConversationToolOverlayPanel>(p => p
            .Add(x => x.ConversationId, "c1")
            .Add(x => x.AgentTools, agentTools ?? AgentTools)
            .Add(x => x.InitialToolOverrideJson, overlayJson));

    // ---- AC2: no overlay renders as unrestricted, with no visual noise -------------------------

    [Fact]
    public void No_overlay_renders_as_unrestricted()
    {
        var cut = Render(overlayJson: null);

        cut.Find("[data-testid='tool-overlay-state']").TextContent
            .ShouldContain("Unrestricted", Case.Insensitive);
    }

    [Fact]
    public void No_overlay_renders_no_narrowed_or_disabled_lists()
    {
        var cut = Render(overlayJson: null);

        // "No visual noise for the common case": the narrowed-to and disabled summaries are the
        // noise, and neither may be emitted when there is nothing to say.
        cut.FindAll("[data-testid='tool-overlay-enabled-list']").ShouldBeEmpty();
        cut.FindAll("[data-testid='tool-overlay-disabled-list']").ShouldBeEmpty();
        cut.FindAll("[data-testid='tool-overlay-refused']").ShouldBeEmpty();
    }

    [Fact]
    public void Corrupt_overlay_json_renders_as_unrestricted_rather_than_throwing()
    {
        // Mirrors SessionToolOverride.FromJson, which fails OPEN on corrupt JSON. The portal must
        // agree with the gateway rather than render a restriction the gateway will not apply.
        var cut = Render(overlayJson: "{ not json");

        cut.Find("[data-testid='tool-overlay-state']").TextContent
            .ShouldContain("Unrestricted", Case.Insensitive);
    }

    // ---- AC1: a persisted overlay renders its current state ------------------------------------

    [Fact]
    public void Persisted_narrowing_overlay_renders_the_narrowed_to_list()
    {
        var cut = Render("""{"enabledTools":["read","write"]}""");

        var list = cut.Find("[data-testid='tool-overlay-enabled-list']").TextContent;
        list.ShouldContain("read");
        list.ShouldContain("write");
        cut.Find("[data-testid='tool-overlay-state']").TextContent
            .ShouldContain("Narrowed", Case.Insensitive);
    }

    [Fact]
    public void Persisted_disable_overlay_renders_the_disabled_list()
    {
        var cut = Render("""{"disabledTools":["exec"]}""");

        cut.Find("[data-testid='tool-overlay-disabled-list']").TextContent.ShouldContain("exec");
    }

    [Fact]
    public void Persisted_overlay_checks_only_the_tools_it_narrows_to()
    {
        var cut = Render("""{"enabledTools":["read"]}""");

        cut.Find("[data-testid='tool-overlay-check-read']")
            .HasAttribute("checked").ShouldBeTrue();
        cut.Find("[data-testid='tool-overlay-check-write']")
            .HasAttribute("checked").ShouldBeFalse();
    }

    // ---- AC5: never grants a tool the agent lacks; refusals display AS refused ------------------

    [Fact]
    public void Offers_a_checkbox_only_for_tools_the_agent_actually_has()
    {
        var cut = Render(agentTools: ["read", "write"]);

        cut.FindAll("[data-testid^='tool-overlay-check-']").Count.ShouldBe(2);
        cut.FindAll("[data-testid='tool-overlay-check-exec']").ShouldBeEmpty();
    }

    [Fact]
    public void Refused_entry_is_displayed_as_refused_not_as_granted()
    {
        // "telemetry" is named by the persisted overlay but is NOT in the agent's configured set,
        // so the resolver refuses it. The portal must say so rather than imply it was granted.
        var cut = Render("""{"enabledTools":["read","telemetry"]}""");

        var refused = cut.Find("[data-testid='tool-overlay-refused']").TextContent;
        refused.ShouldContain("telemetry");
        refused.ShouldContain("Refused", Case.Insensitive);

        // And it must not appear as a granted/narrowed-to entry.
        cut.Find("[data-testid='tool-overlay-enabled-list']").TextContent.ShouldNotContain("telemetry");
        cut.FindAll("[data-testid='tool-overlay-check-telemetry']").ShouldBeEmpty();
    }

    [Fact]
    public async Task Saving_never_sends_a_tool_absent_from_the_agent_set()
    {
        // Even when the persisted overlay carries a refused name, a subsequent save must not echo
        // it back: the portal is not a channel for re-asserting a widening attempt.
        var cut = Render("""{"enabledTools":["read","telemetry"]}""");

        await cut.Find("[data-testid='tool-overlay-save']").ClickAsync(new());

        await _restClient.Received(1).SetConversationOverrideAsync(
            "c1",
            Arg.Is<SetConversationOverrideRequestDto>(r =>
                r.ToolOverrideJson != null && !r.ToolOverrideJson.Contains("telemetry")),
            Arg.Any<CancellationToken>());
    }

    // ---- AC3 + AC4: set and clear, through the EXISTING override endpoint -----------------------

    [Fact]
    public async Task Save_writes_the_overlay_through_the_existing_override_endpoint()
    {
        var cut = Render("""{"enabledTools":["read","write"]}""");

        await cut.Find("[data-testid='tool-overlay-check-write']").ChangeAsync(new ChangeEventArgs { Value = false });
        await cut.Find("[data-testid='tool-overlay-save']").ClickAsync(new());

        await _restClient.Received(1).SetConversationOverrideAsync(
            "c1",
            Arg.Is<SetConversationOverrideRequestDto>(r =>
                r.ToolOverrideJson != null
                && r.ToolOverrideJson.Contains("read")
                && !r.ToolOverrideJson.Contains("\"write\"")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Clear_writes_a_null_overlay_through_the_same_endpoint()
    {
        var cut = Render("""{"enabledTools":["read"]}""");

        await cut.Find("[data-testid='tool-overlay-clear']").ClickAsync(new());

        await _restClient.Received(1).SetConversationOverrideAsync(
            "c1",
            Arg.Is<SetConversationOverrideRequestDto>(r => r.ToolOverrideJson == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Save_round_trips_the_sibling_model_overrides_so_they_are_not_clobbered()
    {
        // PUT /override applies every field it receives, so a tools-only write that omitted the
        // model/thinking/context values would silently CLEAR them. The panel must echo them back.
        var cut = _ctx.Render<ConversationToolOverlayPanel>(p => p
            .Add(x => x.ConversationId, "c1")
            .Add(x => x.AgentTools, AgentTools)
            .Add(x => x.InitialModel, "claude-opus-4")
            .Add(x => x.InitialThinking, "high")
            .Add(x => x.InitialContextWindow, 128000));

        await cut.Find("[data-testid='tool-overlay-check-exec']").ChangeAsync(new ChangeEventArgs { Value = false });
        await cut.Find("[data-testid='tool-overlay-save']").ClickAsync(new());

        await _restClient.Received(1).SetConversationOverrideAsync(
            "c1",
            Arg.Is<SetConversationOverrideRequestDto>(r =>
                r.Model == "claude-opus-4" && r.Thinking == "high" && r.ContextWindow == 128000),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Clearing_every_checkbox_disables_save_rather_than_persisting_an_empty_agent()
    {
        // An overlay narrowing to the empty set leaves the agent with no tools at all. That is a
        // representable-but-useless state; refuse it at the affordance instead of persisting it.
        var cut = Render(agentTools: ["read", "write"]);

        await cut.Find("[data-testid='tool-overlay-check-read']").ChangeAsync(new ChangeEventArgs { Value = false });
        await cut.Find("[data-testid='tool-overlay-check-write']").ChangeAsync(new ChangeEventArgs { Value = false });

        cut.Find("[data-testid='tool-overlay-save']").HasAttribute("disabled").ShouldBeTrue();
    }

    // ---- Sad paths -----------------------------------------------------------------------------

    [Fact]
    public async Task Save_failure_is_surfaced_and_does_not_claim_success()
    {
        _restClient
            .SetConversationOverrideAsync("c1", Arg.Any<SetConversationOverrideRequestDto>(), Arg.Any<CancellationToken>())
            .Returns<Task<ConversationResponseDto?>>(_ => throw new HttpRequestException("boom"));

        var cut = Render("""{"enabledTools":["read"]}""");
        await cut.Find("[data-testid='tool-overlay-save']").ClickAsync(new());

        var status = cut.Find("[data-testid='tool-overlay-status']").TextContent;
        status.ShouldContain("Failed", Case.Insensitive);
        status.ShouldNotContain("saved", Case.Insensitive);
    }

    [Fact]
    public async Task Clear_failure_leaves_the_overlay_rendered_as_still_applied()
    {
        _restClient
            .SetConversationOverrideAsync("c1", Arg.Any<SetConversationOverrideRequestDto>(), Arg.Any<CancellationToken>())
            .Returns<Task<ConversationResponseDto?>>(_ => throw new HttpRequestException("boom"));

        var cut = Render("""{"disabledTools":["exec"]}""");
        await cut.Find("[data-testid='tool-overlay-clear']").ClickAsync(new());

        // The restriction is still persisted server-side, so the portal must keep showing it.
        cut.Find("[data-testid='tool-overlay-state']").TextContent
            .ShouldNotContain("Unrestricted", Case.Insensitive);
        cut.Find("[data-testid='tool-overlay-status']").TextContent
            .ShouldContain("Failed", Case.Insensitive);
    }

    [Fact]
    public void Agent_with_no_configured_tools_renders_without_offering_any_grant()
    {
        var cut = Render(agentTools: []);

        cut.FindAll("[data-testid^='tool-overlay-check-']").ShouldBeEmpty();
        cut.Find("[data-testid='tool-overlay-save']").HasAttribute("disabled").ShouldBeTrue();
    }
}

