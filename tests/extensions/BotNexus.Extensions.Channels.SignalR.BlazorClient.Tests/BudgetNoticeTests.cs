using System.Text.Json;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class BudgetNoticeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunEnded_BudgetNoticeDoesNotDependOnModelSummary(bool summary)
    {
        var (store, handler, conv) = Setup();
        using (handler)
        {
            if (summary)
                conv.AppendMessage(new ChatMessage("Assistant", "Model summary", DateTimeOffset.UtcNow));
            handler.HandleRunEnded(End());
            var notice = conv.Messages.Single(row => row.Role == "System");
            AssertNotice(notice);
            conv.Messages.Count.ShouldBe(summary ? 2 : 1);
        }
    }

    [Fact]
    public void RunEnded_DuplicateRunIsDeduplicatedButDifferentRunsRemainDistinct()
    {
        var (_, handler, conv) = Setup();
        using (handler)
        {
            handler.HandleRunEnded(End());
            handler.HandleRunEnded(End());
            conv.Messages.ShouldHaveSingleItem();
            handler.HandleRunEnded(End("run-2"));
            conv.Messages.Count.ShouldBe(2);
        }
    }

    [Fact]
    public void RunEnded_BackgroundNoticeNeverAppearsInDisplayedConversation()
    {
        var (store, handler, displayed) = Setup();
        using (handler)
        {
            var agent = store.GetAgent("agent-1").ShouldNotBeNull();
            var background = new ConversationState { ConversationId = "background", Title = "Background", ActiveSessionId = "session-bg" };
            agent.Conversations[background.ConversationId] = background;
            store.RegisterSession("agent-1", "session-bg");
            handler.HandleRunEnded(End(conversation: "background", session: "session-bg"));
            AssertNotice(background.Messages.ShouldHaveSingleItem());
            displayed.Messages.ShouldBeEmpty();
        }
    }

    [Fact]
    public void RunEnded_UnknownConversationDoesNotLeakNoticeToDisplayedFallback()
    {
        var (_, handler, conv) = Setup();
        using (handler)
        {
            handler.HandleRunEnded(End(conversation: "missing"));
            conv.Messages.ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData("Completed", null, false)]
    [InlineData("Parked", "SemanticNonProgress", false)]
    [InlineData("Parked", "ToolNonProgress", false)]
    public void RunEnded_OrdinaryAndSemanticStopsDoNotProduceBudgetNotice(string status, string? reason, bool absolute)
    {
        var (_, handler, conv) = Setup();
        using (handler)
        {
            var json = JsonSerializer.Serialize(new { sessionId = "session-1", conversationId = "conv-1", agentRunId = "run-normal",
                completion = new { status, stopReason = reason, guardObservations = new[] { new { absoluteLimitReached = absolute, totalResults = 6, stopThreshold = 6 } } } });
            handler.HandleRunEnded(JsonSerializer.Deserialize<AgentStreamEvent>(json).ShouldNotBeNull());
            conv.Messages.ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData("AbsoluteLimitReached", false)]
    [InlineData("ToolNonProgress", true)]
    public void RunEnded_LegacyAbsoluteEvidenceRecognizedWithoutInventingCounts(string reason, bool absolute)
    {
        var (_, handler, conv) = Setup();
        using (handler)
        {
            var json = JsonSerializer.Serialize(new { sessionId = "session-1", conversationId = "conv-1", agentRunId = "legacy-run",
                completion = new { status = "Parked", stopReason = reason, guardObservations = new[] { new { absoluteLimitReached = absolute } } } });
            handler.HandleRunEnded(JsonSerializer.Deserialize<AgentStreamEvent>(json).ShouldNotBeNull());
            var notice = conv.Messages.ShouldHaveSingleItem();
            notice.Content.ShouldContain("tool-result budget");
            notice.Content.ShouldContain("new direction");
            notice.Content.ShouldNotContain("Limit:");
            notice.Content.ShouldNotContain("128");
        }
    }

    [Fact]
    public void HistoryProjection_RefreshAndColdReloadRetainExactlyOneNoticePerRun()
    {
        var (_, handler, conv) = Setup();
        using (handler)
        {
            handler.HandleRunEnded(End());
            var live = conv.Messages.ShouldHaveSingleItem();
            var entry = JsonSerializer.Deserialize<ConversationHistoryEntryDto>(JsonSerializer.Serialize(new {
                entryId = "persisted-row", agentRunId = "run-1", role = "system", messageKind = "tool-result-budget",
                content = live.Content, timestamp = live.Timestamp, kind = "message", sessionId = "session-1"
            })).ShouldNotBeNull();
            var restored = AgentInteractionService.ProjectConversationEntry(entry);
            restored.Kind.ShouldBe("tool-result-budget");
            var refreshed = TranscriptReconciler.Reconcile(conv.Messages.ToArray(), [restored]);
            refreshed.ShouldHaveSingleItem();
            TranscriptReconciler.Reconcile(refreshed, [restored]).ShouldHaveSingleItem();
            var cold = TranscriptReconciler.Reconcile([], [restored]);
            AssertNotice(cold.ShouldHaveSingleItem());
            conv.ClearMessages();
            conv.AppendMessage(restored);
            handler.HandleRunEnded(End());
            conv.Messages.ShouldHaveSingleItem();
        }
    }

    [Fact]
    public void SessionHistoryProjection_RawSessionKindRetainsNoticeIdentity()
    {
        var entry = JsonSerializer.Deserialize<SessionHistoryEntryDto>("""
            {"role":"system","content":"Saved budget notice","kind":"tool-result-budget","agentRunId":"run-1"}
            """).ShouldNotBeNull();
        var message = AgentInteractionService.ToChatMessage(entry);
        message.Kind.ShouldBe("tool-result-budget");
        JsonSerializer.Serialize(message).ShouldContain("\"AgentRunId\":\"run-1\"");
        message.Role.ShouldBe("System");
    }

    [Theory]
    [InlineData(0, 130, 130)]
    [InlineData(128, 130, 0)]
    [InlineData(128, 1, 0)]
    public void RunEnded_InvalidCountsAreNotDisplayed(int limit, long executed, long overshoot)
    {
        var (_, handler, conv) = Setup();
        using (handler)
        {
            var json = JsonSerializer.Serialize(new { sessionId = "session-1", conversationId = "conv-1", agentRunId = "invalid",
                completion = new { stopReason = "ToolResultBudget", toolResultBudget = new { limit, executedResults = executed, overshoot } } });
            handler.HandleRunEnded(JsonSerializer.Deserialize<AgentStreamEvent>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)).ShouldNotBeNull());
            var notice = conv.Messages.ShouldHaveSingleItem();
            notice.Content.ShouldContain("new direction");
            notice.Content.ShouldNotContain("Limit:");
        }
    }

    private static void AssertNotice(ChatMessage notice)
    {
        notice.Role.ShouldBe("System");
        notice.Kind.ShouldBe("tool-result-budget");
        notice.Content.ShouldContain("tool-result budget");
        notice.Content.ShouldContain("incomplete");
        notice.Content.ShouldContain("parked");
        notice.Content.ShouldContain("new direction");
        notice.Content.ShouldContain("Limit: 128");
        notice.Content.ShouldContain("Executed results: 130");
        notice.Content.ShouldContain("Overshoot: 2");
    }

    private static AgentStreamEvent End(string run = "run-1", string conversation = "conv-1", string session = "session-1") =>
        JsonSerializer.Deserialize<AgentStreamEvent>(JsonSerializer.Serialize(new {
            sessionId = session, conversationId = conversation, agentRunId = run,
            completion = new { status = "Parked", stopReason = "ToolResultBudget", toolResultBudget = new { limit = 128, executedResults = 130, overshoot = 2 } }
        }), new JsonSerializerOptions(JsonSerializerDefaults.Web)).ShouldNotBeNull();

    private static (ClientStateStore Store, GatewayEventHandler Handler, ConversationState Conversation) Setup()
    {
        var store = new ClientStateStore();
        store.UpsertAgent(new AgentState { AgentId = "agent-1", DisplayName = "Agent", SessionId = "session-1" });
        var agent = store.GetAgent("agent-1").ShouldNotBeNull();
        var conv = new ConversationState { ConversationId = "conv-1", Title = "Conversation", ActiveSessionId = "session-1" };
        agent.Conversations[conv.ConversationId] = conv;
        store.RegisterSession("agent-1", "session-1");
        store.SelectView("agent-1", "conv-1", SelectionSource.RouteNavigation);
        return (store, new GatewayEventHandler(store, new GatewayHubConnection(), Microsoft.Extensions.Logging.Abstractions.NullLogger<GatewayEventHandler>.Instance, store), conv);
    }
}
