using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class SessionControlsTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store;

    public SessionControlsTests()
    {
        _store = new ClientStateStore();
        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private void SeedConversation(string sessionId)
    {
        _store.SeedAgents([new AgentSummary("agent-1", "Agent 1")]);
        _store.SeedConversations("agent-1", [new ConversationSummaryDto(
            "conv-1", "agent-1", "Chat", true, "Active", sessionId, 0,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]);
    }

    [Fact]
    public void Shows_truncated_session_ID_when_session_exists()
    {
        SeedConversation("abcdefghijklmnop");

        var cut = _ctx.Render<SessionControls>(p => p.Add(c => c.AgentId, "agent-1").Add(c => c.ConversationId, "conv-1"));

        Assert.Contains("abcdefgh", cut.Markup);
    }

    [Fact]
    public void Shows_copy_icon_and_tooltip_with_full_session_ID()
    {
        const string fullId = "full-session-id-12345";
        SeedConversation(fullId);

        var cut = _ctx.Render<SessionControls>(p => p.Add(c => c.AgentId, "agent-1").Add(c => c.ConversationId, "conv-1"));

        var span = cut.Find(".session-id");
        Assert.Contains(fullId, span.GetAttribute("title") ?? "");
    }

    [Fact]
    public void Does_not_render_session_id_element_when_session_is_null()
    {
        _store.SeedAgents([new AgentSummary("agent-1", "Agent 1")]);

        var cut = _ctx.Render<SessionControls>(p => p.Add(c => c.AgentId, "agent-1").Add(c => c.ConversationId, "conv-1"));

        Assert.Empty(cut.FindAll(".session-id"));
    }

    [Fact]
    public void Does_not_render_reset_button()
    {
        SeedConversation("some-session-id");

        var cut = _ctx.Render<SessionControls>(p => p.Add(c => c.AgentId, "agent-1").Add(c => c.ConversationId, "conv-1"));

        // Reset button was removed in the refactor — confirm absent
        Assert.Empty(cut.FindAll(".reset-btn"));
        Assert.Empty(cut.FindAll("button"));
    }

    [Fact]
    public void Shows_session_controls_container()
    {
        SeedConversation("some-id");

        var cut = _ctx.Render<SessionControls>(p => p.Add(c => c.AgentId, "agent-1").Add(c => c.ConversationId, "conv-1"));

        cut.Find(".session-controls");
    }

    [Fact]
    public void Session_ID_truncated_to_8_chars_with_ellipsis()
    {
        SeedConversation("1234567890abcdef");

        var cut = _ctx.Render<SessionControls>(p => p.Add(c => c.AgentId, "agent-1").Add(c => c.ConversationId, "conv-1"));

        Assert.Contains("12345678", cut.Markup);
        Assert.Contains("…", cut.Markup);
    }
}
