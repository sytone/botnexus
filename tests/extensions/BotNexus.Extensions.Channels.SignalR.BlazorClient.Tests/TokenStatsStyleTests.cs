using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Verifies the token usage stats styling on assistant messages.
/// Closes #952.
/// </summary>
public sealed class TokenStatsStyleTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store;

    public TokenStatsStyleTests()
    {
        _store = new ClientStateStore();
        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(Substitute.For<IAgentInteractionService>());
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp => new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
        _ctx.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        _ctx.Services.AddSingleton(new HttpClient());
        _ctx.Services.AddSingleton(Substitute.For<IPortalPreferencesService>());
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private IRenderedComponent<ChatPanel> RenderWithMessage(ChatMessage msg)
    {
        var agent = new AgentState { AgentId = "a-1", DisplayName = "Bot" };
        _store.UpsertAgent(agent);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto(
                ConversationId: "c-1", AgentId: "a-1", Title: "T", IsDefault: false,
                Status: "Active", ActiveSessionId: null, BindingCount: 0,
                CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow)
        ]);
        _store.SetActiveConversation("a-1", "c-1");
        _store.AppendMessage("c-1", msg);

        return _ctx.Render<ChatPanel>(p =>
            p.Add(x => x.AgentId, "a-1"));
    }

    [Fact]
    public void Token_stats_not_shown_when_all_null()
    {
        var msg = new ChatMessage("Assistant", "Hello", DateTimeOffset.UtcNow) with
        {
            InputTokens = null, OutputTokens = null, CacheRead = null, CacheWrite = null
        };

        var cut = RenderWithMessage(msg);

        Assert.Empty(cut.FindAll("[data-testid='msg-token-stats']"));
    }

    [Fact]
    public void Token_stats_rendered_with_comma_formatted_numbers()
    {
        var msg = new ChatMessage("Assistant", "Hello", DateTimeOffset.UtcNow) with
        {
            InputTokens = 1234, OutputTokens = 456, CacheRead = 789, CacheWrite = 12
        };

        var cut = RenderWithMessage(msg);

        var stats = cut.Find("[data-testid='msg-token-stats']");
        Assert.NotNull(stats);

        // Stats must be outside .message-meta (message-meta is inside .message-header)
        Assert.Empty(cut.FindAll(".message-meta [data-testid='msg-token-stats']"));

        // Numbers must be comma-formatted
        var statsText = stats.TextContent;
        Assert.Contains("1,234", statsText);
        Assert.Contains("456", statsText);
        Assert.Contains("789", statsText);
        Assert.Contains("12", statsText);
    }

    [Fact]
    public void Cache_tokens_not_shown_when_zero()
    {
        var msg = new ChatMessage("Assistant", "Hello", DateTimeOffset.UtcNow) with
        {
            InputTokens = 100, OutputTokens = 50, CacheRead = 0, CacheWrite = 0
        };

        var cut = RenderWithMessage(msg);

        var stats = cut.Find("[data-testid='msg-token-stats']");
        Assert.NotNull(stats);

        // Zero cache tokens must not render
        Assert.Empty(stats.QuerySelectorAll("[data-testid='token-stat-cache-read']"));
        Assert.Empty(stats.QuerySelectorAll("[data-testid='token-stat-cache-write']"));
    }

    [Fact]
    public void Token_stats_use_msg_token_stats_css_class()
    {
        var msg = new ChatMessage("Assistant", "Hello", DateTimeOffset.UtcNow) with
        {
            InputTokens = 10, OutputTokens = 5
        };

        var cut = RenderWithMessage(msg);

        Assert.NotEmpty(cut.FindAll(".msg-token-stats"));
    }
}
