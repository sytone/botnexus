using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Bunit.TestDoubles;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class AgentDashboardTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IClientStateStore _store;

    public AgentDashboardTests()
    {
        _store = Substitute.For<IClientStateStore>();
        _store.Agents.Returns(new Dictionary<string, AgentState>().AsReadOnly());

        _ctx.Services.AddSingleton(_store);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    // ── Happy paths ────────────────────────────────────────────────────────

    [Fact]
    public void Renders_card_for_each_non_readonly_agent()
    {
        var agents = new Dictionary<string, AgentState>
        {
            ["a1"] = new() { AgentId = "a1", DisplayName = "Alpha" },
            ["a2"] = new() { AgentId = "a2", DisplayName = "Beta" }
        };
        _store.Agents.Returns(agents.AsReadOnly());

        var cut = _ctx.Render<AgentDashboard>();

        var cards = cut.FindAll(".agent-card");
        Assert.Equal(2, cards.Count);
    }

    [Fact]
    public void Shows_agent_name_emoji_and_description()
    {
        var agents = new Dictionary<string, AgentState>
        {
            ["a1"] = new() { AgentId = "a1", DisplayName = "Alpha", Emoji = "🔬", Description = "Platform engineer" }
        };
        _store.Agents.Returns(agents.AsReadOnly());

        var cut = _ctx.Render<AgentDashboard>();

        Assert.Contains("Alpha", cut.Markup);
        Assert.Contains("🔬", cut.Markup);
        Assert.Contains("Platform engineer", cut.Markup);
    }

    [Fact]
    public void Orders_streaming_then_recent_then_user_agents_then_stable_identity()
    {
        var recent = new AgentState { AgentId = "recent", DisplayName = "Zulu" };
        recent.Conversations["recent-conversation"] = new ConversationState
        {
            ConversationId = "recent-conversation",
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };

        var older = new AgentState { AgentId = "older", DisplayName = "Alpha" };
        older.Conversations["older-conversation"] = new ConversationState
        {
            ConversationId = "older-conversation",
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1)
        };

        var agents = new Dictionary<string, AgentState>
        {
            ["built-in"] = new() { AgentId = "built-in", DisplayName = "Able", IsBuiltIn = true },
            ["streaming"] = new() { AgentId = "streaming", DisplayName = "Streaming", IsStreaming = true },
            ["older"] = older,
            ["recent"] = recent,
            ["user-idle"] = new() { AgentId = "user-idle", DisplayName = "Beta" }
        };
        _store.Agents.Returns(agents.AsReadOnly());

        var cut = _ctx.Render<AgentDashboard>();

        var orderedIds = cut.FindAll("[data-testid='agent-card']")
            .Select(card => card.GetAttribute("data-agent-id") ?? string.Empty)
            .ToArray();
        Assert.Equal(["streaming", "recent", "older", "user-idle", "built-in"], orderedIds);
    }

    [Fact]
    public void Activity_order_updates_when_store_changes()
    {
        var alpha = new AgentState { AgentId = "alpha", DisplayName = "Alpha" };
        var beta = new AgentState { AgentId = "beta", DisplayName = "Beta" };
        alpha.Conversations["a"] = new ConversationState
        {
            ConversationId = "a",
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-2)
        };
        beta.Conversations["b"] = new ConversationState
        {
            ConversationId = "b",
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        _store.Agents.Returns(new Dictionary<string, AgentState>
        {
            ["alpha"] = alpha,
            ["beta"] = beta
        }.AsReadOnly());

        var cut = _ctx.Render<AgentDashboard>();
        Assert.Equal("beta", cut.Find("[data-testid='agent-card']").GetAttribute("data-agent-id"));

        alpha.IsStreaming = true;
        _store.OnChanged += Raise.Event<Action>();

        cut.WaitForAssertion(() =>
            Assert.Equal("alpha", cut.Find("[data-testid='agent-card']").GetAttribute("data-agent-id")));
    }

    [Fact]
    public void Active_streaming_agent_card_has_active_class()
    {
        var agents = new Dictionary<string, AgentState>
        {
            ["a1"] = new() { AgentId = "a1", DisplayName = "Alpha", IsStreaming = true }
        };
        _store.Agents.Returns(agents.AsReadOnly());

        var cut = _ctx.Render<AgentDashboard>();

        var card = cut.Find(".agent-card");
        Assert.Contains("agent-card--active", card.ClassList);
    }

    [Fact]
    public void Unread_badge_shown_when_unread_count_greater_than_zero()
    {
        var agents = new Dictionary<string, AgentState>
        {
            ["a1"] = new() { AgentId = "a1", DisplayName = "Alpha", UnreadCount = 3 }
        };
        _store.Agents.Returns(agents.AsReadOnly());

        var cut = _ctx.Render<AgentDashboard>();

        var badge = cut.Find(".agent-card-unread-badge");
        Assert.Contains("3", badge.TextContent);
    }

    [Fact]
    public void Open_conversation_count_reflects_active_status_conversations()
    {
        var agent = new AgentState { AgentId = "a1", DisplayName = "Alpha" };
        agent.Conversations["c1"] = new ConversationState { ConversationId = "c1", Status = "Active" };
        agent.Conversations["c2"] = new ConversationState { ConversationId = "c2", Status = "Active" };
        agent.Conversations["c3"] = new ConversationState { ConversationId = "c3", Status = "Archived" };

        var agents = new Dictionary<string, AgentState> { ["a1"] = agent };
        _store.Agents.Returns(agents.AsReadOnly());

        var cut = _ctx.Render<AgentDashboard>();

        Assert.Contains("2 open", cut.Markup);
    }

    [Fact]
    public void Clicking_card_with_conversation_navigates_to_agent_and_conversation()
    {
        var navMan = _ctx.Services.GetRequiredService<NavigationManager>() as BunitNavigationManager;

        var agent = new AgentState { AgentId = "a1", DisplayName = "Alpha" };
        var updatedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        agent.Conversations["c1"] = new ConversationState
        {
            ConversationId = "c1",
            Status = "Active",
            UpdatedAt = updatedAt
        };

        var agents = new Dictionary<string, AgentState> { ["a1"] = agent };
        _store.Agents.Returns(agents.AsReadOnly());

        var cut = _ctx.Render<AgentDashboard>();
        cut.Find(".agent-card").Click();

        Assert.Equal("http://localhost/chat/a1/c1", navMan?.Uri);
    }

    [Fact]
    public void Clicking_card_without_conversations_navigates_to_agent_only()
    {
        var navMan = _ctx.Services.GetRequiredService<NavigationManager>() as BunitNavigationManager;

        var agents = new Dictionary<string, AgentState>
        {
            ["a1"] = new() { AgentId = "a1", DisplayName = "Alpha" }
        };
        _store.Agents.Returns(agents.AsReadOnly());

        var cut = _ctx.Render<AgentDashboard>();
        cut.Find(".agent-card").Click();

        Assert.Equal("http://localhost/chat/a1", navMan?.Uri);
    }

    // ── Sad paths ──────────────────────────────────────────────────────────

    [Fact]
    public void Shows_empty_message_when_no_agents_registered()
    {
        _store.Agents.Returns(new Dictionary<string, AgentState>().AsReadOnly());

        var cut = _ctx.Render<AgentDashboard>();

        cut.Find(".agent-dashboard-empty");
        Assert.DoesNotContain("agent-card", cut.Markup.Replace("agent-card-grid", ""));
    }

    [Fact]
    public void Readonly_subagent_sessions_are_excluded_from_dashboard()
    {
        var agents = new Dictionary<string, AgentState>
        {
            ["a1"] = new() { AgentId = "a1", DisplayName = "Alpha" },
            ["sub"] = new() { AgentId = "sub", DisplayName = "SubAgent", SessionType = "agent-subagent", IsObserverAgent = true }
        };
        _store.Agents.Returns(agents.AsReadOnly());

        var cut = _ctx.Render<AgentDashboard>();

        var cards = cut.FindAll(".agent-card");
        Assert.Single(cards);
        Assert.Contains("Alpha", cut.Markup);
        Assert.DoesNotContain("SubAgent", cut.Markup);
    }

    [Fact]
    public void Description_hidden_when_null_or_empty()
    {
        var agents = new Dictionary<string, AgentState>
        {
            ["a1"] = new() { AgentId = "a1", DisplayName = "Alpha", Description = null }
        };
        _store.Agents.Returns(agents.AsReadOnly());

        var cut = _ctx.Render<AgentDashboard>();

        Assert.Empty(cut.FindAll(".agent-card-description"));
    }

    [Fact]
    public void Unread_badge_hidden_when_unread_count_is_zero()
    {
        var agents = new Dictionary<string, AgentState>
        {
            ["a1"] = new() { AgentId = "a1", DisplayName = "Alpha", UnreadCount = 0 }
        };
        _store.Agents.Returns(agents.AsReadOnly());

        var cut = _ctx.Render<AgentDashboard>();

        Assert.Empty(cut.FindAll(".agent-card-unread-badge"));
    }

    [Fact]
    public void Dashboard_re_renders_on_store_change_event()
    {
        _store.Agents.Returns(new Dictionary<string, AgentState>().AsReadOnly());

        var cut = _ctx.Render<AgentDashboard>();

        Assert.Empty(cut.FindAll(".agent-card"));

        // Now add an agent and fire OnChanged
        var agents = new Dictionary<string, AgentState>
        {
            ["a1"] = new() { AgentId = "a1", DisplayName = "Alpha" }
        };
        _store.Agents.Returns(agents.AsReadOnly());
        _store.OnChanged += Raise.Event<Action>();

        cut.WaitForState(() => cut.FindAll(".agent-card").Count == 1);
        Assert.Single(cut.FindAll(".agent-card"));
    }
}
