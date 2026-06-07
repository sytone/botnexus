using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Sessions;

namespace BotNexus.Gateway.Sessions.Tests;

/// <summary>
/// Tests for <see cref="SessionContextProjector.DetectAbandonedTurn"/> which identifies
/// incomplete tool call sequences left over from stalled turns (#790).
/// </summary>
public sealed class AbandonedTurnDetectionTests
{
    private static SessionEntry User(string content = "hello") => new()
    {
        Role = MessageRole.User,
        Content = content,
    };

    private static SessionEntry Assistant(string content = "response") => new()
    {
        Role = MessageRole.Assistant,
        Content = content,
    };

    private static SessionEntry ToolStart(string toolName = "exec", string callId = "call_1") => new()
    {
        Role = MessageRole.Tool,
        Content = $"Tool '{toolName}' started.",
        ToolName = toolName,
        ToolCallId = callId,
        ToolArgs = "{\"command\":\"ls\"}",
    };

    private static SessionEntry ToolEnd(string toolName = "exec", string callId = "call_1") => new()
    {
        Role = MessageRole.Tool,
        Content = "file1.txt\nfile2.txt",
        ToolName = toolName,
        ToolCallId = callId,
    };

    private static SessionEntry CrashSentinel() => new()
    {
        Role = MessageRole.System,
        Content = "[agent turn in progress — gateway restarted if visible]",
        IsCrashSentinel = true,
    };

    [Fact]
    public void CleanSession_NoAbandonedTurn()
    {
        var history = new List<SessionEntry>
        {
            User("hi"),
            Assistant("hello!"),
            User("thanks"),
        };

        var result = SessionContextProjector.DetectAbandonedTurn(history);

        result.HasAbandonedTurn.ShouldBeFalse();
        result.AbandonedEntryCount.ShouldBe(0);
    }

    [Fact]
    public void CompletedToolCycle_NoAbandonedTurn()
    {
        var history = new List<SessionEntry>
        {
            User("list files"),
            ToolStart("exec", "call_1"),
            ToolEnd("exec", "call_1"),
            Assistant("Here are your files."),
            User("thanks"),
        };

        var result = SessionContextProjector.DetectAbandonedTurn(history);

        result.HasAbandonedTurn.ShouldBeFalse();
    }

    [Fact]
    public void DanglingToolStart_DetectsAbandonedTurn()
    {
        // Turn stalled: tool started but never completed
        var history = new List<SessionEntry>
        {
            User("run a long command"),
            ToolStart("exec", "call_1"),
            // No ToolEnd — turn stalled
            User("thanks"),  // new user message
        };

        var result = SessionContextProjector.DetectAbandonedTurn(history);

        result.HasAbandonedTurn.ShouldBeTrue();
        result.AbandonedEntryCount.ShouldBe(1); // the dangling ToolStart
    }

    [Fact]
    public void MultipleDanglingToolCalls_DetectsAll()
    {
        var history = new List<SessionEntry>
        {
            User("do multiple things"),
            ToolStart("exec", "call_1"),
            ToolEnd("exec", "call_1"),
            ToolStart("read", "call_2"),
            // call_2 never completed, then more tools started
            ToolStart("write", "call_3"),
            User("stop that"),  // new user message
        };

        var result = SessionContextProjector.DetectAbandonedTurn(history);

        result.HasAbandonedTurn.ShouldBeTrue();
        result.AbandonedEntryCount.ShouldBe(2); // call_2 ToolStart + call_3 ToolStart
    }

    [Fact]
    public void CrashSentinelWithDanglingTools_DetectsAbandonedTurn()
    {
        var history = new List<SessionEntry>
        {
            User("run something"),
            ToolStart("exec", "call_1"),
            CrashSentinel(),
            User("hello again"),  // new user message after restart
        };

        var result = SessionContextProjector.DetectAbandonedTurn(history);

        result.HasAbandonedTurn.ShouldBeTrue();
        result.AbandonedEntryCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void EmptyHistory_NoAbandonedTurn()
    {
        var history = new List<SessionEntry>();

        var result = SessionContextProjector.DetectAbandonedTurn(history);

        result.HasAbandonedTurn.ShouldBeFalse();
    }

    [Fact]
    public void SingleUserMessage_NoAbandonedTurn()
    {
        var history = new List<SessionEntry>
        {
            User("hello"),
        };

        var result = SessionContextProjector.DetectAbandonedTurn(history);

        result.HasAbandonedTurn.ShouldBeFalse();
    }

    [Fact]
    public void AssistantResponseWithoutTools_NoAbandonedTurn()
    {
        // Normal conversation flow — assistant responded but user hasn't spoken yet
        var history = new List<SessionEntry>
        {
            User("hi"),
            Assistant("hello!"),
            User("what next?"),
        };

        var result = SessionContextProjector.DetectAbandonedTurn(history);

        result.HasAbandonedTurn.ShouldBeFalse();
    }
}
