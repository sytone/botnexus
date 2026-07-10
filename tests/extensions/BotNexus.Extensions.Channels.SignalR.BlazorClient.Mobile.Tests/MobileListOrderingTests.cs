using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for issue #1480: the mobile agent dropdown and conversation list must use the same
/// ordering as the desktop portal so the two form factors do not drift.
/// <list type="bullet">
///   <item>Agents: built-ins after user-created agents, then alphabetical by display name.</item>
///   <item>Conversations: default conversation first, then most-recently-updated.</item>
///   <item>The mobile auto-select must use the same comparator as the rendered list.</item>
/// </list>
/// The first group are pure <see cref="PortalListOrdering"/> contract tests; the last pins the
/// rendered DOM order on the mobile <see cref="Chat"/> page.
/// </summary>
public sealed class MobileListOrderingTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    // ── PortalListOrdering — agent ordering ───────────────────────────────────

    [Fact]
    public void Agents_order_user_agents_before_built_ins()
    {
        var agents = new Dictionary<string, AgentState>
        {
            ["b"] = new AgentState { AgentId = "b", DisplayName = "Built", IsBuiltIn = true },
            ["u"] = new AgentState { AgentId = "u", DisplayName = "Userly", IsBuiltIn = false }
        };

        var ordered = agents.OrderForDisplay().Select(kv => kv.Key).ToList();

        Assert.Equal(new[] { "u", "b" }, ordered);
    }

    [Fact]
    public void Agents_order_alphabetically_by_display_name_case_insensitive()
    {
        var agents = new Dictionary<string, AgentState>
        {
            ["z"] = new AgentState { AgentId = "z", DisplayName = "Zebra", IsBuiltIn = false },
            ["a"] = new AgentState { AgentId = "a", DisplayName = "apple", IsBuiltIn = false },
            ["m"] = new AgentState { AgentId = "m", DisplayName = "Mango", IsBuiltIn = false }
        };

        var ordered = agents.OrderForDisplay().Select(kv => kv.Value.DisplayName).ToList();

        // Case-insensitive so "apple" sorts before "Mango"/"Zebra" as a human expects.
        Assert.Equal(new[] { "apple", "Mango", "Zebra" }, ordered);
    }

    [Fact]
    public void Agents_match_desktop_order_user_then_built_in_each_alpha()
    {
        var agents = new Dictionary<string, AgentState>
        {
            ["core"] = new AgentState { AgentId = "core", DisplayName = "Core", IsBuiltIn = true },
            ["analyst"] = new AgentState { AgentId = "analyst", DisplayName = "Analyst", IsBuiltIn = true },
            ["quill"] = new AgentState { AgentId = "quill", DisplayName = "Quill", IsBuiltIn = false },
            ["farns"] = new AgentState { AgentId = "farns", DisplayName = "Farnsworth", IsBuiltIn = false }
        };

        var ordered = agents.OrderForDisplay().Select(kv => kv.Key).ToList();

        // user (Farnsworth, Quill) before built-in (Analyst, Core), alpha within each group.
        Assert.Equal(new[] { "farns", "quill", "analyst", "core" }, ordered);
    }

    // ── PortalListOrdering — conversation ordering ─────────────────────────────

    [Fact]
    public void Conversations_order_default_first_even_when_older()
    {
        var convs = new[]
        {
            Conv("recent", isDefault: false, updated: DateTimeOffset.UtcNow),
            Conv("default", isDefault: true, updated: DateTimeOffset.UtcNow.AddDays(-5))
        };

        var ordered = convs.OrderForDisplay().Select(c => c.ConversationId).ToList();

        Assert.Equal(new[] { "default", "recent" }, ordered);
    }

    [Fact]
    public void Conversations_order_non_default_by_updated_descending()
    {
        var now = DateTimeOffset.UtcNow;
        var convs = new[]
        {
            Conv("old", isDefault: false, updated: now.AddHours(-3)),
            Conv("newest", isDefault: false, updated: now),
            Conv("middle", isDefault: false, updated: now.AddHours(-1))
        };

        var ordered = convs.OrderForDisplay().Select(c => c.ConversationId).ToList();

        Assert.Equal(new[] { "newest", "middle", "old" }, ordered);
    }

    // ── Mobile render order ───────────────────────────────────────────────────

    [Fact]
    public void Mobile_agent_dropdown_renders_in_display_order()
    {
        BuildStore(
            agents: new[]
            {
                new AgentState { AgentId = "core", DisplayName = "Core", IsBuiltIn = true, IsConnected = true },
                new AgentState { AgentId = "quill", DisplayName = "Quill", IsBuiltIn = false, IsConnected = true },
                new AgentState { AgentId = "analyst", DisplayName = "Analyst", IsBuiltIn = true, IsConnected = true }
            },
            activeAgentId: "quill");

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "quill"));

        var agentOptions = cut.Find("select.agent-select").QuerySelectorAll("option")
            .Select(o => o.TextContent.Trim())
            .ToList();

        // user (Quill) first, then built-ins alpha (Analyst, Core).
        Assert.Equal(new[] { "Quill", "Analyst", "Core" }, agentOptions);
    }

    [Fact]
    public void Mobile_conversation_dropdown_renders_default_first_then_updated()
    {
        var now = DateTimeOffset.UtcNow;
        var agent = new AgentState
        {
            AgentId = "quill",
            DisplayName = "Quill",
            IsConnected = true,
            ActiveConversationId = "default"
        };
        agent.Conversations["recent"] = Conv("recent", isDefault: false, updated: now, title: "Recent");
        agent.Conversations["default"] = Conv("default", isDefault: true, updated: now.AddDays(-5), title: "Default");
        agent.Conversations["older"] = Conv("older", isDefault: false, updated: now.AddHours(-2), title: "Older");

        BuildStore(new[] { agent }, "quill");

        var cut = _ctx.Render<Chat>(p => p.Add(c => c.AgentId, "quill"));

        var convOptions = cut.Find("select.conv-select").QuerySelectorAll("option")
            .Select(o => o.TextContent.Trim())
            .ToList();

        Assert.Equal(new[] { "Default", "Recent", "Older" }, convOptions);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ConversationState Conv(string id, bool isDefault, DateTimeOffset updated, string? title = null)
        => new()
        {
            ConversationId = id,
            IsDefault = isDefault,
            UpdatedAt = updated,
            Status = "Active",
            Title = title ?? id
        };

    private void BuildStore(IReadOnlyList<AgentState> agents, string activeAgentId)
    {
        var store = Substitute.For<IClientStateStore>();
        var portalLoad = Substitute.For<IPortalLoadService>();
        var interaction = Substitute.For<IAgentInteractionService>();

        portalLoad.IsReady.Returns(true);
        portalLoad.IsLoading.Returns(false);
        portalLoad.LoadError.Returns((string?)null);
        portalLoad.IsSignalRConnected.Returns(true);
        portalLoad.InitializeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var dict = agents.ToDictionary(a => a.AgentId, a => a);
        store.Agents.Returns(dict.AsReadOnly());
        store.ActiveAgentId.Returns(activeAgentId);
        foreach (var a in agents)
        {
            store.GetAgent(a.AgentId).Returns(a);
        }

        store.GetStreamState(Arg.Any<string>()).Returns(new ConversationStreamState());
        store.GetMessages(Arg.Any<string>()).Returns(new List<ChatMessage>());

        _ctx.Services.AddSingleton(store);
        _ctx.Services.AddSingleton(portalLoad);
        _ctx.Services.AddSingleton(new BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Services.MobileHubTuningOptions());
        _ctx.Services.AddSingleton(interaction);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }
}
