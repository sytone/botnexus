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
    public void Identity_avatar_falls_back_to_robot_when_emoji_missing()
    {
        Seed("no-emoji-agent", "No Emoji Agent", emoji: null);

        var cut = RenderFor("no-emoji-agent");

        Assert.Equal("\U0001F916", cut.Find(".agent-panel-avatar").TextContent.Trim());
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
}
