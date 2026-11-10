using System.Text.Json;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>Exercises the real transcript component, not only the shared event handler.</summary>
public sealed class MobileChatBudgetNoticeTests : IDisposable
{
    private const string Notice = "The run stopped because its tool-result budget was reached. Work is incomplete and parked. " +
        "Provide new direction to continue; no work will resume automatically.";
    private const string Counts = " Limit: 128. Executed results: 130. Overshoot: 2.";
    // Mobile intentionally maps every non-user role to the assistant-side CSS bubble.
    private const string SystemSelector = ".message.assistant .message-content.plain-text-content";
    private const string AssistantSelector = ".message.assistant";
    private readonly BunitContext _context = new();
    private readonly ClientStateStore _store = new();
    private readonly GatewayEventHandler _handler;

    public MobileChatBudgetNoticeTests()
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
        var portalLoad = Substitute.For<IPortalLoadService>();
        portalLoad.IsReady.Returns(true);
        portalLoad.IsSignalRConnected.Returns(true);
        portalLoad.InitializeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _context.Services.AddSingleton(portalLoad);
        _context.Services.AddSingleton(new MobileHubTuningOptions());
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
        BudgetRows(cut).ShouldBeEmpty();
        await cut.InvokeAsync(() =>
        {
            if (summary)
                _store.AppendMessage("conv-1", new ChatMessage("Assistant", "Optional model summary", DateTimeOffset.UtcNow));
            _handler.HandleRunEnded(End());
            _handler.HandleRunEnded(End());
        });

        BudgetRows(cut).ShouldHaveSingleItem().TextContent.ShouldBe(Notice + Counts);
        AssistantRows(cut).Count.ShouldBe(summary ? 1 : 0);
        if (summary)
            cut.WaitForAssertion(() => AssistantRows(cut).ShouldHaveSingleItem().TextContent.ShouldContain("Optional model summary"));
        AssistantRows(cut).ShouldAllBe(row => !row.TextContent.Contains("tool-result budget", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Completed", null)]
    [InlineData("Parked", "SemanticNonProgress")]
    public async Task RunEnded_OrdinaryOrSemanticCompletionDoesNotRenderBudgetNotice(string status, string? reason)
    {
        _store.AppendMessage("conv-1", new ChatMessage("Assistant", "Ordinary response", DateTimeOffset.UtcNow));
        var cut = RenderChat();
        await cut.InvokeAsync(() => _handler.HandleRunEnded(End(status: status, reason: reason)));

        BudgetRows(cut).ShouldBeEmpty();
        cut.WaitForAssertion(() => AssistantRows(cut).ShouldHaveSingleItem().TextContent.ShouldContain("Ordinary response"));
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
        var rest = Substitute.For<IGatewayRestClient>();
        if (rawSession)
        {
            var agent = _store.GetAgent("agent-1").ShouldNotBeNull();
            agent.Conversations["conv-1"] = new ConversationState
            {
                ConversationId = "conv-1", Title = "Legacy session", ActiveSessionId = "session-1",
                IsLocallySynthesised = true
            };
            rest.GetSessionHistoryAsync("session-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new SessionHistoryResponseDto(0, 200, 1,
                    [JsonSerializer.Deserialize<SessionHistoryEntryDto>(json).ShouldNotBeNull()]));
        }
        else
        {
            rest.GetHistoryAsync("conv-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new ConversationHistoryResponseDto("conv-1", 1, 0, 20,
                    [JsonSerializer.Deserialize<ConversationHistoryEntryDto>(json).ShouldNotBeNull()]));
        }
        // Mobile is a different assembly from the desktop friend tests. Exercise the public
        // selection/history seam rather than exposing internal projection methods for this test.
        var loader = new AgentInteractionService(_store, new GatewayHubConnection(), rest,
            NullLogger<AgentInteractionService>.Instance);
        await loader.SelectConversationAsync("agent-1", "conv-1");
        var restored = _store.GetConversation("conv-1").ShouldNotBeNull().Messages.ShouldHaveSingleItem();
        restored.Kind.ShouldBe("tool-result-budget");
        var cut = RenderChat();
        BudgetRows(cut).ShouldHaveSingleItem().TextContent.ShouldBe(Notice);

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
        BudgetRows(cut).ShouldHaveSingleItem().TextContent.ShouldBe(Notice);
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
        BudgetRows(cut).ShouldBeEmpty();
        cut.Markup.ShouldNotContain("tool-result budget");

        await cut.InvokeAsync(() => _store.SelectView("agent-1", "background", SelectionSource.RouteNavigation));
        cut.Render(parameters => parameters.Add(component => component.AgentId, "agent-1").Add(component => component.ConversationId, "background"));
        BudgetRows(cut).ShouldHaveSingleItem().TextContent.ShouldBe(Notice + Counts);

        await cut.InvokeAsync(() => _store.SelectView("agent-1", "conv-1", SelectionSource.RouteNavigation));
        cut.Render(parameters => parameters.Add(component => component.AgentId, "agent-1").Add(component => component.ConversationId, "conv-1"));
        BudgetRows(cut).ShouldBeEmpty();
    }

    private static IReadOnlyList<AngleSharp.Dom.IElement> BudgetRows(IRenderedComponent<Chat> cut) =>
        cut.FindAll(SystemSelector).Where(row => row.TextContent.StartsWith(Notice, StringComparison.Ordinal)).ToArray();

    private static IReadOnlyList<AngleSharp.Dom.IElement> AssistantRows(IRenderedComponent<Chat> cut) =>
        cut.FindAll(AssistantSelector).Where(row => !row.TextContent.Contains(Notice, StringComparison.Ordinal)).ToArray();

    private IRenderedComponent<Chat> RenderChat() => _context.Render<Chat>(parameters => parameters
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
