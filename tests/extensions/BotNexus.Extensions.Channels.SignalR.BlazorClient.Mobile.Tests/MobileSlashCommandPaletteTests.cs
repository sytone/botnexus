using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Issue #1951: the mobile chat palette must consume the SAME shared Core slash-command
/// registry + dispatcher the desktop ChatPanel uses (registry #1949, approval hook #1950),
/// for full command parity. These tests drive the palette behaviour: it shows on '/', filters
/// by prefix, executes through the dispatcher into <see cref="IAgentInteractionService"/>, and
/// routes /new and /clear through the existing mobile confirm-overlay.
/// </summary>
public sealed class MobileSlashCommandPaletteTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IClientStateStore _store;
    private readonly IPortalLoadService _portalLoad;
    private readonly IAgentInteractionService _interaction;

    public MobileSlashCommandPaletteTests()
    {
        _store = Substitute.For<IClientStateStore>();
        _portalLoad = Substitute.For<IPortalLoadService>();
        _interaction = Substitute.For<IAgentInteractionService>();

        _portalLoad.IsReady.Returns(true);
        _portalLoad.IsSignalRConnected.Returns(true);
        _portalLoad.LoadError.Returns((string?)null);
        _portalLoad.InitializeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var convState = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "Test",
            Status = "Active",
            ActiveSessionId = "session-1",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var agentState = new AgentState
        {
            AgentId = "agent-1",
            DisplayName = "Agent 1",
            Emoji = null,
            SessionId = "session-1",
            ActiveConversationId = "conv-1"
        };
        agentState.Conversations["conv-1"] = convState;

        _store.Agents.Returns(new Dictionary<string, AgentState> { ["agent-1"] = agentState }.AsReadOnly());
        _store.ActiveAgentId.Returns("agent-1");
        _store.GetAgent("agent-1").Returns(agentState);
        _store.GetMessages("conv-1").Returns(new List<ChatMessage>().AsReadOnly());
        _store.GetStreamState("conv-1").Returns(new ConversationStreamState());

        _interaction.ResetSessionAsync(Arg.Any<string>()).Returns(Task.CompletedTask);
        _interaction.SendMessageAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.CompletedTask);

        _ctx.Services.AddSingleton(_store);
        _ctx.Services.AddSingleton(_portalLoad);
        _ctx.Services.AddSingleton(new BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Services.MobileHubTuningOptions());
        _ctx.Services.AddSingleton(_interaction);
        // The mobile palette wires the SAME shared Core dispatcher the desktop uses.
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(new SlashCommandDispatcher(_interaction));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private static void TypeInput(IRenderedComponent<Chat> cut, string text)
    {
        var textarea = cut.Find(".input-textarea");
        textarea.Input(text);
    }

    [Fact]
    public void Palette_shows_when_input_starts_with_slash()
    {
        var cut = _ctx.Render<Chat>();

        Assert.Empty(cut.FindAll("[data-testid='command-palette']"));

        TypeInput(cut, "/");

        Assert.NotNull(cut.Find("[data-testid='command-palette']"));
        // Full command surface from the shared registry.
        Assert.Equal(SlashCommandRegistry.All.Count, cut.FindAll("[data-testid='command-item']").Count);
    }

    [Fact]
    public void Palette_hidden_when_input_has_space()
    {
        var cut = _ctx.Render<Chat>();

        TypeInput(cut, "/new now");

        Assert.Empty(cut.FindAll("[data-testid='command-palette']"));
    }

    [Fact]
    public void Palette_filters_by_prefix()
    {
        var cut = _ctx.Render<Chat>();

        TypeInput(cut, "/co");

        var items = cut.FindAll("[data-testid='command-item']");
        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.StartsWith("/co", i.QuerySelector(".command-name")!.TextContent, StringComparison.OrdinalIgnoreCase));
        // /compact and /context both start with /co.
        Assert.Contains(items, i => i.QuerySelector(".command-name")!.TextContent == "/compact");
        Assert.Contains(items, i => i.QuerySelector(".command-name")!.TextContent == "/context");
    }

    [Fact]
    public async Task Palette_executes_send_to_agent_command_through_interaction()
    {
        var cut = _ctx.Render<Chat>();

        TypeInput(cut, "/help");
        cut.FindAll("[data-testid='command-item']")
            .First(i => i.QuerySelector(".command-name")!.TextContent == "/help")
            .Click();

        // /help is a gateway command dispatched as its command text via SendMessageAsync.
        await _interaction.Received(1).SendMessageAsync("agent-1", "/help");
    }

    [Fact]
    public void New_command_routes_through_confirm_overlay()
    {
        var cut = _ctx.Render<Chat>();

        TypeInput(cut, "/new");
        cut.FindAll("[data-testid='command-item']")
            .First(i => i.QuerySelector(".command-name")!.TextContent == "/new")
            .Click();

        // /new must NOT execute directly; it routes through the existing confirm overlay.
        Assert.NotNull(cut.Find(".reset-confirm-overlay"));
    }

    [Fact]
    public async Task New_command_confirm_resets_session()
    {
        var cut = _ctx.Render<Chat>();

        TypeInput(cut, "/new");
        cut.FindAll("[data-testid='command-item']")
            .First(i => i.QuerySelector(".command-name")!.TextContent == "/new")
            .Click();

        cut.Find("[data-testid='new-session-confirm-btn']").Click();

        await _interaction.Received(1).ResetSessionAsync("agent-1");
    }

    [Fact]
    public void Clear_command_routes_through_confirm_overlay()
    {
        var cut = _ctx.Render<Chat>();

        TypeInput(cut, "/clear");
        cut.FindAll("[data-testid='command-item']")
            .First(i => i.QuerySelector(".command-name")!.TextContent == "/clear")
            .Click();

        Assert.NotNull(cut.Find(".reset-confirm-overlay"));
    }

    [Fact]
    public void Clear_command_confirm_clears_local_messages()
    {
        var cut = _ctx.Render<Chat>();

        TypeInput(cut, "/clear");
        cut.FindAll("[data-testid='command-item']")
            .First(i => i.QuerySelector(".command-name")!.TextContent == "/clear")
            .Click();

        cut.Find("[data-testid='new-session-confirm-btn']").Click();

        _interaction.Received(1).ClearLocalMessages("agent-1");
    }
}
