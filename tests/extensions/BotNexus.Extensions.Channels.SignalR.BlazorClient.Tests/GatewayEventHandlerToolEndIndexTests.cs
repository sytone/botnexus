using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for issue #1622 (AC#1): HandleToolEnd must locate the tool-call message via an O(1)
/// id-&gt;index map maintained on the conversation, never an O(n) linear FindIndex scan. These
/// tests pin the behaviour (the tool result lands on the right message even with many messages
/// present, an unknown id is a graceful no-op) and the structural invariant (the map stays in
/// sync with the message list across append / replace / clear / prepend).
/// </summary>
public sealed class GatewayEventHandlerToolEndIndexTests
{
    private readonly ClientStateStore _store = new();
    private readonly GatewayEventHandler _handler;

    public GatewayEventHandlerToolEndIndexTests()
    {
        _handler = new GatewayEventHandler(_store, new GatewayHubConnection(), Microsoft.Extensions.Logging.Abstractions.NullLogger<GatewayEventHandler>.Instance);

        _store.UpsertAgent(new AgentState
        {
            AgentId = "agent-1",
            DisplayName = "Agent 1",
            IsConnected = true,
            SessionId = "sess-1",
            ActiveConversationId = "conv-1"
        });

        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "Conversation 1",
            ActiveSessionId = "sess-1"
        };
        _store.RegisterSession("agent-1", "sess-1");
    }

    private ConversationState Conv => _store.GetAgent("agent-1")!.Conversations["conv-1"];

    // ---- AC#1 behaviour: ToolEnd updates the correct message via the map ----

    [Fact]
    public void HandleToolEnd_updates_the_correct_tool_message_even_with_many_messages_present()
    {
        var conv = Conv;

        // Seed a large amount of history so an O(n) scan would be expensive and any off-by-one
        // locator bug would land the result on the wrong row.
        for (var i = 0; i < 500; i++)
            conv.AppendMessage(new ChatMessage("User", $"history-{i}", DateTimeOffset.UtcNow));

        // A tool starts: HandleToolStart appends the "Calling ..." placeholder and tracks the call.
        _handler.HandleToolStart(new AgentStreamEvent
        {
            SessionId = "sess-1",
            ToolCallId = "tool-mid",
            ToolName = "read"
        });

        // More history after the tool message so the target is in the middle of the list.
        for (var i = 0; i < 500; i++)
            conv.AppendMessage(new ChatMessage("User", $"tail-{i}", DateTimeOffset.UtcNow));

        var toolMessageId = conv.StreamState.ActiveToolCalls["tool-mid"].MessageId;
        var expectedIndex = conv.Messages.ToList().FindIndex(m => m.Id == toolMessageId);

        // The map must locate the tool message at exactly the same index a linear scan would.
        conv.MessageIndex.TryGetValue(toolMessageId!, out var mappedIndex).ShouldBeTrue();
        mappedIndex.ShouldBe(expectedIndex);

        _handler.HandleToolEnd(new AgentStreamEvent
        {
            SessionId = "sess-1",
            ToolCallId = "tool-mid",
            ToolName = "read",
            ToolResult = "file body",
            ToolIsError = false
        });

        // The result landed on the original tool message (same Id, updated in place) -- not appended
        // as a new fallback row, and not on a neighbouring history row.
        var updated = conv.Messages[expectedIndex];
        updated.Id.ShouldBe(toolMessageId);
        updated.ToolResult.ShouldBe("file body");
        updated.Content.ShouldContain("completed");

        // No fallback message was appended: the tool message was located and replaced in place.
        conv.Messages.Count(m => m.ToolCallId == "tool-mid").ShouldBe(1);
    }

    [Fact]
    public void HandleToolEnd_for_unknown_tool_call_id_is_a_no_op_and_does_not_throw()
    {
        var conv = Conv;
        conv.AppendMessage(new ChatMessage("User", "hello", DateTimeOffset.UtcNow));
        var before = conv.Messages.ToList();

        // No matching ActiveToolCall was ever registered -> messageId resolves to null ->
        // the locator path is skipped entirely. This mirrors the old FindIndex == -1 behaviour:
        // nothing is mutated, nothing throws.
        Should.NotThrow(() => _handler.HandleToolEnd(new AgentStreamEvent
        {
            SessionId = "sess-1",
            ToolCallId = "never-started",
            ToolName = "read",
            ToolResult = "orphan"
        }));

        // The single seeded message is untouched (no in-place replace happened on it).
        conv.Messages[0].Id.ShouldBe(before[0].Id);
        conv.Messages[0].Content.ShouldBe("hello");
        conv.Messages[0].ToolResult.ShouldBeNull();
    }

    [Fact]
    public void HandleToolEnd_with_tracked_call_but_missing_message_falls_back_to_new_message()
    {
        var conv = Conv;

        // Track a tool call whose message id is NOT present in the list (e.g. the row was cleared).
        // The old code did FindIndex == -1 -> fall through to the "Fallback: new message" branch.
        conv.StreamState.ActiveToolCalls["ghost"] = new ActiveToolCall
        {
            ToolCallId = "ghost",
            ToolName = "read",
            StartedAt = DateTimeOffset.UtcNow,
            MessageId = "message-that-is-not-in-the-list"
        };

        conv.TryGetMessageIndex("message-that-is-not-in-the-list", out _).ShouldBeFalse();

        _handler.HandleToolEnd(new AgentStreamEvent
        {
            SessionId = "sess-1",
            ToolCallId = "ghost",
            ToolName = "read",
            ToolResult = "fallback body",
            ToolIsError = false
        });

        // Behaviour-preserving: a fallback tool message is appended (exactly like the old -1 path).
        var fallback = conv.Messages.Single(m => m.ToolCallId == "ghost");
        fallback.ToolResult.ShouldBe("fallback body");
        fallback.IsToolCall.ShouldBeTrue();
    }

    // ---- Structural invariant: the map stays consistent across all mutation paths ----

    [Fact]
    public void MessageIndex_matches_a_linear_scan_after_appends()
    {
        var conv = Conv;
        for (var i = 0; i < 50; i++)
            conv.AppendMessage(new ChatMessage("User", $"m-{i}", DateTimeOffset.UtcNow));

        AssertIndexMatchesList(conv);
    }

    [Fact]
    public void MessageIndex_is_rebuilt_after_clear()
    {
        var conv = Conv;
        conv.AppendMessage(new ChatMessage("User", "a", DateTimeOffset.UtcNow));
        conv.AppendMessage(new ChatMessage("User", "b", DateTimeOffset.UtcNow));

        conv.ClearMessages();

        conv.Messages.ShouldBeEmpty();
        conv.MessageIndex.Count.ShouldBe(0);

        // After a clear the map must not retain stale ids.
        conv.AppendMessage(new ChatMessage("User", "c", DateTimeOffset.UtcNow));
        AssertIndexMatchesList(conv);
    }

    [Fact]
    public void MessageIndex_is_rebuilt_after_prepend_shifts_all_indices()
    {
        var conv = Conv;
        var tail = new ChatMessage("Assistant", "tail", DateTimeOffset.UtcNow);
        conv.AppendMessage(tail);

        // Prepending shifts every existing index by the number of prepended items -- the map must
        // be rebuilt so the existing tail id no longer points at its old (now-wrong) index.
        var older = new[]
        {
            new ChatMessage("User", "older-1", DateTimeOffset.UtcNow),
            new ChatMessage("User", "older-2", DateTimeOffset.UtcNow)
        };
        conv.PrependMessages(older);

        AssertIndexMatchesList(conv);
        conv.MessageIndex[tail.Id].ShouldBe(2);
    }

    [Fact]
    public void ReplaceMessageAt_keeps_the_map_consistent_for_the_same_id()
    {
        var conv = Conv;
        conv.AppendMessage(new ChatMessage("User", "x", DateTimeOffset.UtcNow));
        var original = conv.Messages[0];

        // The HandleToolEnd update path replaces a message with `original with { ... }`, preserving Id.
        conv.ReplaceMessageAt(0, original with { Content = "x-updated" });

        conv.Messages[0].Content.ShouldBe("x-updated");
        conv.Messages[0].Id.ShouldBe(original.Id);
        AssertIndexMatchesList(conv);
    }

    private static void AssertIndexMatchesList(ConversationState conv)
    {
        // Every non-empty id maps to the FIRST index it appears at (matching List.FindIndex semantics).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < conv.Messages.Count; i++)
        {
            var id = conv.Messages[i].Id;
            if (string.IsNullOrEmpty(id) || !seen.Add(id))
                continue;

            conv.MessageIndex.TryGetValue(id, out var mapped).ShouldBeTrue($"id {id} missing from MessageIndex");
            mapped.ShouldBe(i, $"id {id} mapped to wrong index");
        }

        // The map must not carry ids that are not in the list.
        foreach (var kvp in conv.MessageIndex)
        {
            (kvp.Value >= 0 && kvp.Value < conv.Messages.Count).ShouldBeTrue($"stale index {kvp.Value} for id {kvp.Key}");
            conv.Messages[kvp.Value].Id.ShouldBe(kvp.Key);
        }
    }
}
