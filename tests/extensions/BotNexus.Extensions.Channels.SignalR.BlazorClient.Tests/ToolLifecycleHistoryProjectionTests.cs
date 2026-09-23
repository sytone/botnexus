using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class ToolLifecycleHistoryProjectionTests
{
    private static readonly DateTimeOffset Base = DateTimeOffset.Parse("2026-09-16T20:41:03Z");

    [Fact]
    public void ProjectConversationEntries_pairs_interleaved_tool_rows_by_call_id_without_hiding_unrelated_rows()
    {
        var entries = new[]
        {
            Entry("start-a", "call-a", "read", "starting a", Base, "tool-start", args: "{\"path\":\"a\"}"),
            Entry("start-b", "call-b", "write", "starting b", Base.AddSeconds(1), "tool-start", args: "{\"path\":\"b\"}"),
            Message("between", Base.AddSeconds(2)),
            Entry("result-b", "call-b", "write", "wrote b", Base.AddSeconds(3), "tool-result"),
            Entry("result-a", "call-a", "read", "read a", Base.AddSeconds(4), "tool-result")
        };

        var projected = AgentInteractionService.ProjectConversationEntries(entries);

        projected.Count.ShouldBe(3);
        projected.Single(message => message.Content == "between").IsToolCall.ShouldBeFalse();
        var a = projected.Single(message => message.ToolCallId == "call-a");
        a.ServerEntryId.ShouldBe("start-a");
        a.ToolArgs.ShouldBe("{\"path\":\"a\"}");
        a.ToolResult.ShouldBe("read a");
        a.ToolStartedAt.ShouldBe(Base);
        a.ToolCompletedAt.ShouldBe(Base.AddSeconds(4));
        a.ToolDuration.ShouldBe(TimeSpan.FromSeconds(4));
        var b = projected.Single(message => message.ToolCallId == "call-b");
        b.ToolStartedAt.ShouldBe(Base.AddSeconds(1));
        b.ToolCompletedAt.ShouldBe(Base.AddSeconds(3));
    }

    [Fact]
    public void ProjectConversationEntries_preserves_incomplete_start_and_orphan_result_without_fabricating_times()
    {
        var entries = new[]
        {
            Entry("start-only", "call-running", "read", "starting", Base, "tool-start", args: "{}"),
            Entry("result-only", "call-orphan", "shell", "done", Base.AddSeconds(8), "tool-result", isError: true)
        };

        var projected = AgentInteractionService.ProjectConversationEntries(entries);

        var running = projected.Single(message => message.ToolCallId == "call-running");
        running.ToolStartedAt.ShouldBe(Base);
        running.ToolCompletedAt.ShouldBeNull();
        running.ToolResult.ShouldBeNull();
        running.ToolDuration.ShouldBeNull();
        var orphan = projected.Single(message => message.ToolCallId == "call-orphan");
        orphan.ToolStartedAt.ShouldBeNull();
        orphan.ToolCompletedAt.ShouldBe(Base.AddSeconds(8));
        orphan.ToolResult.ShouldBe("done");
        orphan.ToolDuration.ShouldBeNull();
        orphan.ToolIsError.ShouldBeTrue();
    }

    private static ConversationHistoryEntryDto Entry(
        string id, string callId, string toolName, string content, DateTimeOffset timestamp,
        string messageKind, string? args = null, bool isError = false) => new()
    {
        Kind = "message", EntryId = id, SessionId = "session-1", Role = "tool", Content = content,
        Timestamp = timestamp, ToolCallId = callId, ToolName = toolName, ToolArgs = args,
        ToolIsError = isError, MessageKind = messageKind
    };

    private static ConversationHistoryEntryDto Message(string content, DateTimeOffset timestamp) => new()
    {
        Kind = "message", EntryId = $"message-{content}", SessionId = "session-1", Role = "assistant",
        Content = content, Timestamp = timestamp, MessageKind = "message"
    };
}
