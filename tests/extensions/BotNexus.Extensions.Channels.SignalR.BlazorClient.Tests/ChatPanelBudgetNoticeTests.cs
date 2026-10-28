using System.Text.Json;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>Exercises the real transcript component, not only the shared event handler.</summary>
public sealed class ChatPanelBudgetNoticeTests : IDisposable
{
    private const string Notice = "The run stopped because its tool-result budget was reached. Work is incomplete and parked. " +
        "Provide new direction to continue; no work will resume automatically.";
    private const string Counts = " Limit: 128. Executed results: 130. Overshoot: 2.";
    private const string SystemSelector = "[data-testid='chat-system-message']";
    private const string AssistantSelector = "[data-message-role='Assistant']";
    private readonly BunitContext _context = new();
    private readonly ClientStateStore _store = new();
    private readonly GatewayEventHandler _handler;

    public ChatPanelBudgetNoticeTests()
    {
        _store.UpsertAgent(new AgentState { AgentId = "agent-1", DisplayName = "Agent", IsConnected = true, SessionId = "session-1" });
        _store.SeedConversations("agent-1", [Conversation("conv-1", "session-1"), Conversation("background", "session-bg")]);
        _store.RegisterSession("agent-1", "session-1");
        _store.RegisterSession("agent-1", "session-bg");
        _store.SelectView("agent-1", "conv-1", SelectionSource.RouteNavigation);
        _handler = new GatewayEventHandler(_store, new GatewayHubConnection(), NullLogger<GatewayEventHandler>.Instance, _store);

        var interaction = Substitute.For<IAgentInteractionService>();
        _context.Services.AddSingleton<IClientStateStore>(_store);
        _context.Services.AddSingleton(interaction);
        _context.Services.AddSingleton<ISlashCommandDispatcher>(new SlashCommandDispatcher(interaction));
        _context.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        _context.Services.AddSingleton(new HttpClient());
        var preferences = Substitute.For<IPortalPreferencesService>();
        preferences.Current.Returns(new PortalPreferences());
        _context.Services.AddSingleton(preferences);
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
        // Assistant rows use JS-rendered HTML; loose interop otherwise caches an empty result.
        _context.JSInterop.Setup<string>("BotNexus.renderMarkdown", "Optional model summary")
            .SetResult("<p>Optional model summary</p>");
        _context.JSInterop.Setup<string>("BotNexus.renderMarkdown", "Ordinary response")
            .SetResult("<p>Ordinary response</p>");
    }

    public void Dispose()
    {
        _handler.Dispose();
        _context.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunEnded_BudgetNoticeRendersAsSystemRowWithOrWithoutSummary(bool summary)
    {
        var cut = RenderChat();
        cut.FindAll(SystemSelector).ShouldBeEmpty();
        await cut.InvokeAsync(() =>
        {
            if (summary)
                _store.AppendMessage("conv-1", new ChatMessage("Assistant", "Optional model summary", DateTimeOffset.UtcNow));
            _handler.HandleRunEnded(End());
            _handler.HandleRunEnded(End());
        });

        cut.FindAll(SystemSelector).ShouldHaveSingleItem().TextContent.ShouldBe(Notice + Counts);
        cut.FindAll(AssistantSelector).Count.ShouldBe(summary ? 1 : 0);
        if (summary)
            cut.WaitForAssertion(() => cut.Find(AssistantSelector).TextContent.ShouldContain("Optional model summary"));
        cut.FindAll(AssistantSelector).ShouldAllBe(row => !row.TextContent.Contains("tool-result budget", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Completed", null)]
    [InlineData("Parked", "SemanticNonProgress")]
    public async Task RunEnded_OrdinaryOrSemanticCompletionDoesNotRenderBudgetNotice(string status, string? reason)
    {
        _store.AppendMessage("conv-1", new ChatMessage("Assistant", "Ordinary response", DateTimeOffset.UtcNow));
        var cut = RenderChat();
        await cut.InvokeAsync(() => _handler.HandleRunEnded(End(status: status, reason: reason)));

        cut.FindAll(SystemSelector).ShouldBeEmpty();
        cut.WaitForAssertion(() => cut.Find(AssistantSelector).TextContent.ShouldContain("Ordinary response"));
        cut.Markup.ShouldNotContain("tool-result budget");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task History_ColdLegacyNoticeRendersAndLiveReplayOrRefreshDoesNotDuplicate(bool rawSession)
    {
        // Old runs have no measured budget tuple. Both REST shapes must render without invented counts.
        var json = JsonSerializer.Serialize(new
        {
            kind = rawSession ? "tool-result-budget" : "message", messageKind = "tool-result-budget",
            entryId = "saved-notice", agentRunId = "run-1", sessionId = "session-1", role = "system",
            content = Notice, timestamp = DateTimeOffset.UtcNow
        });
        var restored = rawSession
            ? AgentInteractionService.ToChatMessage(JsonSerializer.Deserialize<SessionHistoryEntryDto>(json).ShouldNotBeNull())
            : AgentInteractionService.ProjectConversationEntry(JsonSerializer.Deserialize<ConversationHistoryEntryDto>(json).ShouldNotBeNull());
        _store.AppendMessage("conv-1", restored);
        var cut = RenderChat();
        cut.FindAll(SystemSelector).ShouldHaveSingleItem().TextContent.ShouldBe(Notice);

        await cut.InvokeAsync(() =>
        {
            _handler.HandleRunEnded(End(reason: "AbsoluteLimitReached", measured: false));
            var conversation = _store.GetConversation("conv-1").ShouldNotBeNull();
            var refreshed = TranscriptReconciler.Reconcile(conversation.Messages.ToArray(), [restored]);
            conversation.ClearMessages();
            foreach (var row in refreshed)
                conversation.AppendMessage(row);
            _store.NotifyChanged();
        });
        cut.FindAll(SystemSelector).ShouldHaveSingleItem().TextContent.ShouldBe(Notice);
        cut.Markup.ShouldNotContain("Limit:");
    }

    [Fact]
    public async Task RunEnded_BackgroundAndUnknownNoticesStayOffDisplayedRoute()
    {
        var cut = RenderChat();
        await cut.InvokeAsync(() =>
        {
            _handler.HandleRunEnded(End(conversation: "background", session: "session-bg"));
            _handler.HandleRunEnded(End(conversation: "unknown"));
        });
        cut.FindAll(SystemSelector).ShouldBeEmpty();
        cut.Markup.ShouldNotContain("tool-result budget");

        await cut.InvokeAsync(() => _store.SelectView("agent-1", "background", SelectionSource.RouteNavigation));
        cut.Render(parameters => parameters.Add(component => component.AgentId, "agent-1").Add(component => component.ConversationId, "background"));
        cut.FindAll(SystemSelector).ShouldHaveSingleItem().TextContent.ShouldBe(Notice + Counts);

        await cut.InvokeAsync(() => _store.SelectView("agent-1", "conv-1", SelectionSource.RouteNavigation));
        cut.Render(parameters => parameters.Add(component => component.AgentId, "agent-1").Add(component => component.ConversationId, "conv-1"));
        cut.FindAll(SystemSelector).ShouldBeEmpty();
    }

    private IRenderedComponent<ChatPanel> RenderChat() => _context.Render<ChatPanel>(parameters => parameters
        .Add(component => component.AgentId, "agent-1").Add(component => component.ConversationId, "conv-1"));

    private static ConversationSummaryDto Conversation(string id, string session) => new(
        id, "agent-1", id, false, "Active", session, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    // JSON keeps this test compatible with the clean-base DTO for remote behavioral RED replay.
    private static AgentStreamEvent End(string conversation = "conv-1", string session = "session-1",
        string status = "Parked", string? reason = "ToolResultBudget", bool measured = true) =>
        JsonSerializer.Deserialize<AgentStreamEvent>(JsonSerializer.Serialize(new
        {
            sessionId = session, conversationId = conversation, agentRunId = "run-1",
            completion = new { status, stopReason = reason, toolResultBudget = measured ? new { limit = 128, executedResults = 130, overshoot = 2 } : null }
        }), new JsonSerializerOptions(JsonSerializerDefaults.Web)).ShouldNotBeNull();
}
