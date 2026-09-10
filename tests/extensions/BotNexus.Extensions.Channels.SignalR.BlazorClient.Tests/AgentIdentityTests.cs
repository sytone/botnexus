using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Migrated from AgentPanelHeaderTests (#2441): the agent identity block moved out of
/// AgentPanel's own header row and into the portal top bar as <see cref="AgentIdentity"/>.
/// The behavioural contract is unchanged - description visible, id in the DOM for hover
/// reveal, id as the meta tooltip, emoji avatar with robot fallback - so those assertions
/// migrate with the markup rather than being deleted.
///
/// New coverage for #2441: adversarial input (300-char strings, empty values, ZWJ emoji and
/// embedded newline/tab/carriage-return control characters) must never leak literal control
/// characters into a single-line row.
/// </summary>
public sealed class AgentIdentityTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();

    public AgentIdentityTests()
    {
        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private IRenderedComponent<AgentIdentity> RenderFor(string agentId) =>
        _ctx.Render<AgentIdentity>(p => p.Add(c => c.AgentId, agentId));

    private void Seed(string agentId, string displayName, string? description = null, string? emoji = null)
    {
        _store.UpsertAgent(new AgentState
        {
            AgentId = agentId,
            DisplayName = displayName,
            Description = description,
            Emoji = emoji,
            IsConnected = true
        });
        _store.SelectView(agentId, string.Empty, SelectionSource.UserClick);
    }

    [Fact]
    public void Identity_renders_description_as_visible_sublabel()
    {
        Seed("desc-agent", "Desc Agent", "Handles widget triage");

        var cut = RenderFor("desc-agent");

        Assert.Equal("Handles widget triage", cut.Find(".agent-panel-description").TextContent.Trim());
    }

    // ── The chip's one line: role beats prose ──────────────────────────────

    [Fact]
    public void Identity_prefers_what_the_agent_owns_over_its_description()
    {
        // The chip is a single ellipsis-truncated line. Responsibility is defined as one short
        // line naming what the agent owns, so it survives that truncation; a description is prose
        // and gets cut mid-sentence.
        _store.UpsertAgent(new AgentState
        {
            AgentId = "role-agent",
            DisplayName = "Role Agent",
            Responsibility = "Owns the billing pipeline",
            Description = "Reconciles invoices nightly and escalates mismatches to the on-call.",
            IsConnected = true
        });
        _store.SelectView("role-agent", string.Empty, SelectionSource.UserClick);

        var cut = RenderFor("role-agent");

        Assert.Equal("Owns the billing pipeline", cut.Find(".agent-panel-description").TextContent.Trim());
    }

    [Fact]
    public void Identity_still_shows_the_description_when_no_role_is_set()
    {
        // Most existing agents have only a description. Preferring the new field must not blank
        // the line for every one of them.
        Seed("desc-only", "Desc Only", "Handles widget triage");

        var cut = RenderFor("desc-only");

        Assert.Equal("Handles widget triage", cut.Find(".agent-panel-description").TextContent.Trim());
    }

    [Fact]
    public void A_whitespace_only_role_falls_through_to_the_description()
    {
        // A field cleared to spaces rather than to null is a real state - the persona form writes
        // it - and treating it as "set" would blank the chip's only sublabel.
        _store.UpsertAgent(new AgentState
        {
            AgentId = "blank-role",
            DisplayName = "Blank Role",
            Responsibility = "   ",
            Description = "Handles widget triage",
            IsConnected = true
        });
        _store.SelectView("blank-role", string.Empty, SelectionSource.UserClick);

        var cut = RenderFor("blank-role");

        Assert.Equal("Handles widget triage", cut.Find(".agent-panel-description").TextContent.Trim());
    }

    // ── The wiring behind the avatar's tool state ─────────────────────────

    [Fact]
    public void An_agent_running_a_tool_shows_it_on_the_chip()
    {
        // THE test for this state. AgentActivityModel.For(..., activeToolCalls: 2) was already
        // covered and passing, but the chip fed it a count that could only ever be zero -
        // AgentState carried its own ActiveToolCalls dictionary that nothing in the repo ever
        // wrote to, so "Using tools" was unreachable in the UI while its unit test was green.
        // Tool calls are tracked per CONVERSATION, which is what this seeds.
        _store.UpsertAgent(new AgentState
        {
            AgentId = "busy-agent",
            DisplayName = "Busy Agent",
            IsConnected = true
        });

        var agent = _store.GetAgent("busy-agent")!;
        var conversation = new ConversationState { ConversationId = "c-1" };
        conversation.StreamState.ActiveToolCalls["call-1"] = new ActiveToolCall
        {
            ToolCallId = "call-1",
            ToolName = "bash",
            StartedAt = DateTimeOffset.UtcNow,
            MessageId = "m-1"
        };
        agent.Conversations["c-1"] = conversation;
        _store.SelectView("busy-agent", "c-1", SelectionSource.UserClick);

        var cut = RenderFor("busy-agent");

        Assert.Equal(
            "Using tools",
            cut.Find("[data-testid=agent-identity-activity]").TextContent.Trim());
    }

    [Fact]
    public void A_tool_running_in_any_of_an_agents_conversations_counts()
    {
        // An agent is a roster entry; its conversations are where runs happen. A tool running in
        // one the user is not currently looking at is still that agent being busy.
        _store.UpsertAgent(new AgentState
        {
            AgentId = "multi-agent",
            DisplayName = "Multi Agent",
            IsConnected = true
        });

        var agent = _store.GetAgent("multi-agent")!;
        agent.Conversations["idle"] = new ConversationState { ConversationId = "idle" };

        var busy = new ConversationState { ConversationId = "busy" };
        busy.StreamState.ActiveToolCalls["call-1"] = new ActiveToolCall
        {
            ToolCallId = "call-1",
            ToolName = "bash",
            StartedAt = DateTimeOffset.UtcNow,
            MessageId = "m-1"
        };
        agent.Conversations["busy"] = busy;

        // Viewing the IDLE one.
        _store.SelectView("multi-agent", "idle", SelectionSource.UserClick);

        var cut = RenderFor("multi-agent");

        Assert.Equal(
            "Using tools",
            cut.Find("[data-testid=agent-identity-activity]").TextContent.Trim());
    }

    [Fact]
    public void An_agent_with_no_tool_running_does_not_claim_to_be_using_tools()
    {
        _store.UpsertAgent(new AgentState
        {
            AgentId = "quiet-agent",
            DisplayName = "Quiet Agent",
            IsConnected = true
        });
        _store.GetAgent("quiet-agent")!.Conversations["c-1"] =
            new ConversationState { ConversationId = "c-1" };
        _store.SelectView("quiet-agent", "c-1", SelectionSource.UserClick);

        var cut = RenderFor("quiet-agent");

        Assert.NotEqual(
            "Using tools",
            cut.Find("[data-testid=agent-identity-activity]").TextContent.Trim());
    }
    [Fact]
    public void Identity_keeps_agent_id_in_dom_for_hover_reveal()
    {
        Seed("desc-agent", "Desc Agent", "Handles widget triage");

        var cut = RenderFor("desc-agent");

        Assert.Equal("desc-agent", cut.Find(".agent-panel-id").TextContent.Trim());
    }

    [Fact]
    public void Identity_exposes_agent_id_as_meta_title_tooltip()
    {
        Seed("desc-agent", "Desc Agent", "Handles widget triage");

        var cut = RenderFor("desc-agent");

        Assert.Equal("desc-agent", cut.Find(".agent-panel-meta").GetAttribute("title"));
    }

    [Fact]
    public void Identity_omits_description_element_when_description_is_empty()
    {
        Seed("no-desc-agent", "No Desc Agent", description: null);

        var cut = RenderFor("no-desc-agent");

        Assert.Empty(cut.FindAll(".agent-panel-description"));
        Assert.Equal("no-desc-agent", cut.Find(".agent-panel-id").TextContent.Trim());
        Assert.Equal("no-desc-agent", cut.Find(".agent-panel-meta").GetAttribute("title"));
    }

    [Fact]
    public void Identity_avatar_uses_agent_emoji_when_set()
    {
        Seed("emoji-agent", "Emoji Agent", emoji: "\U0001F52C");

        var cut = RenderFor("emoji-agent");

        Assert.Equal("\U0001F52C", cut.Find(".agent-panel-avatar").TextContent.Trim());
    }

    [Fact]
    public void Identity_avatar_falls_back_to_a_generated_monogram_when_emoji_missing()
    {
        // Was the shared robot glyph. Every agent without an emoji drew the same mark, so on a
        // roster of sixteen the avatar carried no identity at all; it now falls back to initials
        // tinted by a hue derived from the agent id.
        Seed("no-emoji-agent", "No Emoji Agent", emoji: null);

        var cut = RenderFor("no-emoji-agent");
        var avatar = cut.Find(".agent-panel-avatar");

        Assert.Equal("NE", avatar.TextContent.Trim());
        Assert.Contains("is-monogram", avatar.ClassName);
        Assert.Equal(
            AgentAvatarModel.HueFor("no-emoji-agent").ToString(),
            avatar.GetAttribute("data-agent-hue"));
    }

    [Fact]
    public void Two_agents_without_emoji_do_not_share_an_avatar()
    {
        // The property that makes a generated mark worth having. Before this change both rendered
        // an identical robot and the assertion below could not have been written.
        Seed("gantry-manager", "Gantry Manager", emoji: null);
        Seed("harbor-relay", "Harbor Relay Service", emoji: null);

        var first = RenderFor("gantry-manager").Find(".agent-panel-avatar");
        var second = RenderFor("harbor-relay").Find(".agent-panel-avatar");

        Assert.NotEqual(first.TextContent.Trim(), second.TextContent.Trim());
        Assert.NotEqual(first.GetAttribute("data-agent-hue"), second.GetAttribute("data-agent-hue"));
    }

    // ---------------------------------------------------------------- #2441 adversarial input

    [Fact]
    public void Identity_renders_multi_codepoint_zwj_emoji_intact()
    {
        // Family: man + ZWJ + woman + ZWJ + girl + ZWJ + boy. Must survive normalisation whole -
        // the ZWJ (U+200D) is a format character, not whitespace, so it must not be stripped.
        const string family = "\U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466";
        Seed("zwj-agent", "ZWJ Agent", emoji: family);

        var cut = RenderFor("zwj-agent");

        Assert.Equal(family, cut.Find(".agent-panel-avatar").TextContent.Trim());
    }

    [Fact]
    public void Identity_collapses_control_characters_in_display_name()
    {
        Seed("ctrl-agent", "Line\nOne\tTabbed\rReturned");

        var cut = RenderFor("ctrl-agent");

        var name = cut.Find("[data-testid='agent-identity-name']").TextContent;
        Assert.Equal("Line One Tabbed Returned", name);
        Assert.DoesNotContain("\n", name);
        Assert.DoesNotContain("\t", name);
        Assert.DoesNotContain("\r", name);
    }

    [Fact]
    public void Identity_collapses_control_characters_in_description()
    {
        Seed("ctrl-desc-agent", "Ctrl", "First line\n\nSecond\tline\r\nThird");

        var cut = RenderFor("ctrl-desc-agent");

        var description = cut.Find(".agent-panel-description").TextContent;
        Assert.Equal("First line Second line Third", description);
        Assert.DoesNotContain("\n", description);
        Assert.DoesNotContain("\r", description);
        Assert.DoesNotContain("\t", description);
    }

    [Fact]
    public void Identity_renders_very_long_name_on_a_single_line()
    {
        var longName = new string('W', 300);
        Seed("long-agent", longName);

        var cut = RenderFor("long-agent");

        var name = cut.Find("[data-testid='agent-identity-name']").TextContent;
        Assert.Equal(300, name.Length);
        // Truncation itself is a CSS concern; the durable structural guarantee is that the
        // value is one line with no embedded breaks for the ellipsis rule to work against.
        Assert.DoesNotContain("\n", name);
    }

    [Fact]
    public void Identity_falls_back_to_agent_id_when_display_name_is_blank()
    {
        Seed("blank-name-agent", "   ");

        var cut = RenderFor("blank-name-agent");

        Assert.Equal("blank-name-agent", cut.Find("[data-testid='agent-identity-name']").TextContent.Trim());
    }

    [Fact]
    public void Identity_omits_description_when_it_is_only_control_characters()
    {
        Seed("ws-desc-agent", "WS", "\n\t\r ");

        var cut = RenderFor("ws-desc-agent");

        Assert.Empty(cut.FindAll(".agent-panel-description"));
    }

    // ── Persona trigger (slide-out entry point) ───────────────────────────────
    // The name became a <button> so it can open the persona slide-out. These facts pin the parts
    // that are contract: the button exists, the name test-id stays ON the name element, the click
    // raises the callback exactly once, and an unset callback is a harmless no-op - the last one
    // matters because every existing fixture renders this chip without supplying it.

    [Fact]
    public void Identity_renders_the_name_inside_a_persona_trigger_button()
    {
        Seed("trigger-agent", "Trigger Agent");

        var cut = RenderFor("trigger-agent");

        var trigger = cut.Find("[data-testid='agent-persona-trigger']");
        Assert.Equal("BUTTON", trigger.TagName);
        Assert.Equal("button", trigger.GetAttribute("type"));
        // Migrated from an exact TextContent match: the trigger is now the whole chip, so its text
        // also carries the monogram, description and id. The name being INSIDE it is the contract.
        Assert.Contains("Trigger Agent", trigger.TextContent);
    }

    [Fact]
    public void The_whole_identity_chip_is_the_trigger_not_just_the_name()
    {
        // The first cut wrapped only the name: a 67x20px target nobody found. The avatar is the
        // part people instinctively click, so it has to be inside the button.
        Seed("trigger-agent", "Trigger Agent");

        var cut = RenderFor("trigger-agent");

        var trigger = cut.Find("[data-testid='agent-persona-trigger']");
        Assert.NotNull(trigger.QuerySelector(".agent-panel-avatar"));
        Assert.NotNull(trigger.QuerySelector(".agent-panel-meta"));
        Assert.NotNull(trigger.QuerySelector("[data-testid='agent-identity-name']"));
    }

    [Fact]
    public void Identity_shows_a_persistent_affordance_that_the_chip_is_clickable()
    {
        // Always rendered, not hover-only - an affordance you only see once you are already
        // hovering cannot tell you the control is there.
        Seed("trigger-agent", "Trigger Agent");

        var cut = RenderFor("trigger-agent");

        Assert.NotNull(cut.Find("[data-testid='agent-persona-chevron']"));
    }

    [Fact]
    public void The_trigger_contains_no_block_elements_so_the_button_markup_stays_valid()
    {
        // A button's content model is phrasing content. .agent-panel-meta was a <div>, which
        // browsers tolerate inside a button but which is invalid and degrades accessibility.
        Seed("trigger-agent", "Trigger Agent", "Handles widget triage");

        var cut = RenderFor("trigger-agent");

        var trigger = cut.Find("[data-testid='agent-persona-trigger']");
        Assert.Empty(trigger.QuerySelectorAll("div, p, section, ul, ol, h1, h2, h3, h4, h5, h6"));
    }

    [Fact]
    public void Identity_keeps_the_name_testid_on_the_name_element()
    {
        Seed("trigger-agent", "Trigger Agent");

        var cut = RenderFor("trigger-agent");

        Assert.Equal("Trigger Agent", cut.Find("[data-testid='agent-identity-name']").TextContent.Trim());
        Assert.NotNull(cut.Find("[data-testid='agent-identity']"));
    }

    [Fact]
    public void Identity_persona_trigger_raises_its_callback_once_per_click()
    {
        Seed("trigger-agent", "Trigger Agent");
        var raised = 0;

        var cut = _ctx.Render<AgentIdentity>(p => p
            .Add(c => c.AgentId, "trigger-agent")
            .Add(c => c.OnPersonaRequested, () => raised++));

        cut.Find("[data-testid='agent-persona-trigger']").Click();

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Identity_persona_trigger_is_a_no_op_when_no_callback_is_supplied()
    {
        Seed("trigger-agent", "Trigger Agent");

        var cut = RenderFor("trigger-agent");

        // Every pre-existing fixture renders the chip this way. Clicking must not throw.
        cut.Find("[data-testid='agent-persona-trigger']").Click();

        Assert.Equal("Trigger Agent", cut.Find("[data-testid='agent-identity-name']").TextContent.Trim());
    }

    // ── activity indicator (Interface Review P2) ──────────────────────────────

    private void SeedWithActivity(string agentId, bool connected, bool streaming)
    {
        _store.UpsertAgent(new AgentState
        {
            AgentId = agentId,
            DisplayName = "Trigger Agent",
            IsConnected = connected,
            IsStreaming = streaming
        });
        _store.SelectView(agentId, string.Empty, SelectionSource.UserClick);
    }

    [Fact]
    public void A_working_agent_shows_an_activity_dot_on_its_avatar()
    {
        SeedWithActivity("busy-agent", connected: true, streaming: true);

        var cut = RenderFor("busy-agent");

        Assert.NotNull(cut.Find("[data-testid='agent-avatar-activity']"));
    }

    [Fact]
    public void An_idle_agent_shows_no_dot()
    {
        // Sixteen dots distinguish nothing; the point is that the working one stands out.
        SeedWithActivity("calm-agent", connected: true, streaming: false);

        var cut = RenderFor("calm-agent");

        Assert.Empty(cut.FindAll("[data-testid='agent-avatar-activity']"));
    }

    [Fact]
    public void The_state_is_announced_in_text_because_colour_is_not_an_announcement()
    {
        SeedWithActivity("busy-agent", connected: true, streaming: true);

        var cut = RenderFor("busy-agent");

        Assert.Equal("Working", cut.Find("[data-testid='agent-identity-activity']").TextContent.Trim());
    }

    [Fact]
    public void The_state_label_sits_outside_the_trigger_button()
    {
        // The button carries an explicit aria-label, and aria-label REPLACES an element's inner
        // text for assistive technology. A label nested inside would render, satisfy a naive
        // "the label exists" assertion, and never be announced.
        SeedWithActivity("busy-agent", connected: true, streaming: true);

        var cut = RenderFor("busy-agent");

        var trigger = cut.Find("[data-testid='agent-persona-trigger']");
        Assert.Empty(trigger.QuerySelectorAll("[data-testid='agent-identity-activity']"));
        Assert.NotNull(cut.Find("[data-testid='agent-identity-activity']"));
    }

    [Fact]
    public void A_disconnected_agent_reads_as_offline_rather_than_whatever_it_was_doing()
    {
        SeedWithActivity("gone-agent", connected: false, streaming: true);

        var cut = RenderFor("gone-agent");

        Assert.Equal("Offline", cut.Find("[data-testid='agent-identity-activity']").TextContent.Trim());
    }
}
