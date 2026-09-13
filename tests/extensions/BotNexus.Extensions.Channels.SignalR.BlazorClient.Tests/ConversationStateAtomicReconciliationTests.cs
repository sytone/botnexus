using System.Reflection;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Issue #3996: transcript refresh must commit through the conversation timeline seam rather than
/// clearing and rebuilding through separately locked mutations. These tests pin the minimal atomic
/// contract needed by the service: reconcile a server page against the current timeline while using
/// the caller's earlier snapshot only as optimistic state identity.
/// </summary>
public sealed class ConversationStateAtomicReconciliationTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReconcileMessages_AppendAfterSnapshotBeforeCommit_SurvivesExactlyOnceWithRepairedServerRow()
    {
        var conversation = new ConversationState { ConversationId = "conv-1" };
        var original = Message("m-1", "user", "one", 1);
        var repaired = Message("m-2", "assistant", "two", 2);
        var liveAppend = Message("m-3", "assistant", "live", 3);
        conversation.AppendMessage(original);

        var refreshSnapshot = conversation.Messages;
        conversation.AppendMessage(liveAppend);

        var inserted = ReconcileMessages(conversation, refreshSnapshot, [original, repaired]);

        inserted.ShouldBe(1);
        conversation.Messages.Select(message => message.Id).ShouldBe(["m-1", "m-2", "m-3"]);
        conversation.Messages.Count(message => message.Id == "m-2").ShouldBe(1);
        conversation.Messages.Count(message => message.Id == "m-3").ShouldBe(1);
    }

    [Fact]
    public void ReconcileMessages_TerminalToolReplacementAfterSnapshotBeforeCommit_SurvivesWithConsistentIndex()
    {
        var conversation = new ConversationState { ConversationId = "conv-1" };
        var original = Message("m-1", "user", "one", 1);
        var calling = ToolMessage("tool-row", "Calling read", result: null);
        var tail = Message("m-4", "assistant", "tail", 4);
        conversation.AppendMessage(original);
        conversation.AppendMessage(calling);
        conversation.AppendMessage(tail);

        var refreshSnapshot = conversation.Messages;
        var terminal = calling with { Content = "read completed", ToolResult = "file body", ToolIsError = false };
        conversation.ReplaceMessageAt(1, terminal);

        var repaired = Message("m-2", "assistant", "two", 2);
        var staleServerTool = ToolMessage("server-tool-row", "server result", "stale body");
        var inserted = ReconcileMessages(
            conversation,
            refreshSnapshot,
            [original, repaired, staleServerTool, tail]);

        inserted.ShouldBe(1);
        var messages = conversation.Messages;
        var tool = messages.Single(message => message.ToolCallId == "tc-1");
        tool.Id.ShouldBe("tool-row");
        tool.Content.ShouldBe("read completed");
        tool.ToolResult.ShouldBe("file body");
        messages.Count(message => message.ToolCallId == "tc-1").ShouldBe(1);

        conversation.MessageIndex.TryGetValue("tool-row", out var toolIndex).ShouldBeTrue();
        toolIndex.ShouldBe(messages.ToList().IndexOf(tool));
        messages[toolIndex].ShouldBeSameAs(tool);
    }

    private static int ReconcileMessages(
        ConversationState conversation,
        IReadOnlyList<ChatMessage> refreshSnapshot,
        IReadOnlyList<ChatMessage> serverPage)
    {
        var method = typeof(ConversationState).GetMethod(
            "ReconcileMessages",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: [typeof(IReadOnlyList<ChatMessage>), typeof(IReadOnlyList<ChatMessage>)],
            modifiers: null);

        method.ShouldNotBeNull(
            "ConversationState must expose the atomic state-seam contract " +
            "int ReconcileMessages(IReadOnlyList<ChatMessage> snapshot, IReadOnlyList<ChatMessage> serverPage)");
        if (method is null)
            return -1;

        var result = method.Invoke(conversation, [refreshSnapshot, serverPage]);
        return result.ShouldBeOfType<int>();
    }

    private static ChatMessage Message(string id, string role, string content, int minute) =>
        new(role, content, BaseTime.AddMinutes(minute)) { Id = id };

    private static ChatMessage ToolMessage(string id, string content, string? result) =>
        new("Tool", content, BaseTime.AddMinutes(3))
        {
            Id = id,
            ToolName = "read",
            ToolCallId = "tc-1",
            IsToolCall = true,
            ToolResult = result
        };
}
