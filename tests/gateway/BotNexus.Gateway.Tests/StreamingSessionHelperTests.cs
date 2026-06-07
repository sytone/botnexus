using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Streaming;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class StreamingSessionHelperTests
{
    [Fact]
    public async Task ProcessAndSaveAsync_AccumulatesAssistantContentAndToolHistory()
    {
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1") };
        var store = new Mock<ISessionStore>();

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.MessageStart, MessageId = "m1" },
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "Hello ", MessageId = "m1" },
                new AgentStreamEvent { Type = AgentStreamEventType.ToolStart, ToolCallId = "tc1", ToolName = "clock", MessageId = "m1" },
                new AgentStreamEvent { Type = AgentStreamEventType.ToolEnd, ToolCallId = "tc1", ToolName = "clock", ToolResult = "12:00", MessageId = "m1" },
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "world", MessageId = "m1" },
                new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd, MessageId = "m1" }
            ]),
            session,
            store.Object);

        session.History.Count().ShouldBe(3);
        session.History[0].Role.ShouldBe(MessageRole.Tool);
        session.History[0].Content.ShouldBe("Tool 'clock' started.");
        session.History[1].Role.ShouldBe(MessageRole.Tool);
        session.History[1].Content.ShouldBe("12:00");
        session.History[2].Role.ShouldBe(MessageRole.Assistant);
        session.History[2].Content.ShouldBe("Hello world");
        store.Verify(s => s.SaveAsync(session, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessAndSaveAsync_DoesNotPersistThinkingContent()
    {
        var callbackTypes = new List<AgentStreamEventType>();
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-2"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1") };
        var store = new Mock<ISessionStore>();

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ThinkingDelta, ThinkingContent = "Let me think..." },
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "Final answer." }
            ]),
            session,
            store.Object,
            new StreamingSessionOptions(OnEventAsync: (evt, _) =>
            {
                callbackTypes.Add(evt.Type);
                return ValueTask.CompletedTask;
            }));

        session.History.ShouldHaveSingleItem();
        session.History[0].Role.ShouldBe(MessageRole.Assistant);
        session.History[0].Content.ShouldBe("Final answer.");
        callbackTypes.ShouldContain(AgentStreamEventType.ThinkingDelta);
    }

    [Fact]
    public async Task ProcessAndSaveAsync_WithEmptyStream_DoesNotPersistAssistantEntry()
    {
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-3"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1") };
        var store = new Mock<ISessionStore>();

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable([]),
            session,
            store.Object);

        session.History.ShouldBeEmpty();
    }

    // ── ToolArgs tests ────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessAndSaveAsync_ToolStart_WithArgs_PopulatesToolArgsAsJson()
    {
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-args"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1") };
        var store = new Mock<ISessionStore>();

        var result = await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent
                {
                    Type = AgentStreamEventType.ToolStart,
                    ToolCallId = "tc1",
                    ToolName = "search",
                    ToolArgs = new Dictionary<string, object?> { ["query"] = "dotnet", ["limit"] = (object?)5 }
                }
            ]),
            session,
            store.Object);

        result.HistoryEntries.Count.ShouldBe(2); // start + synthesized orphan result
        var entry = result.HistoryEntries[0];
        entry.ToolArgs.ShouldNotBeNull();
        entry.ToolArgs.ShouldContain("query");
        entry.ToolArgs.ShouldContain("dotnet");
    }

    [Fact]
    public async Task ProcessAndSaveAsync_ToolStart_WithNoArgs_ToolArgsIsNull()
    {
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-noargs"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1") };
        var store = new Mock<ISessionStore>();

        var result = await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent
                {
                    Type = AgentStreamEventType.ToolStart,
                    ToolCallId = "tc1",
                    ToolName = "noop",
                    ToolArgs = null
                }
            ]),
            session,
            store.Object);

        result.HistoryEntries.Count.ShouldBe(2); // start + synthesized orphan result
        var entry = result.HistoryEntries[0];
        entry.ToolArgs.ShouldBeNull();
    }

    [Fact]
    public async Task ProcessAndSaveAsync_ToolStart_WithEmptyArgs_ToolArgsIsNull()
    {
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-emptyargs"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1") };
        var store = new Mock<ISessionStore>();

        var result = await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent
                {
                    Type = AgentStreamEventType.ToolStart,
                    ToolCallId = "tc1",
                    ToolName = "noop",
                    ToolArgs = new Dictionary<string, object?>()
                }
            ]),
            session,
            store.Object);

        result.HistoryEntries.Count.ShouldBe(2); // start + synthesized orphan result
        var entry = result.HistoryEntries[0];
        entry.ToolArgs.ShouldBeNull();
    }

    // ── ToolIsError tests ─────────────────────────────────────────────────

    [Fact]
    public async Task ProcessAndSaveAsync_ToolEnd_WithToolIsErrorTrue_SetsToolIsErrorTrue()
    {
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-iserr"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1") };
        var store = new Mock<ISessionStore>();

        var result = await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent
                {
                    Type = AgentStreamEventType.ToolEnd,
                    ToolCallId = "tc1",
                    ToolName = "search",
                    ToolResult = "Something went wrong",
                    ToolIsError = true
                }
            ]),
            session,
            store.Object);

        var entry = result.HistoryEntries.ShouldHaveSingleItem();
        entry.ToolIsError.ShouldBeTrue();
    }

    [Fact]
    public async Task ProcessAndSaveAsync_ToolEnd_WithToolIsErrorFalse_SetsToolIsErrorFalse()
    {
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-noerr"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1") };
        var store = new Mock<ISessionStore>();

        var result = await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent
                {
                    Type = AgentStreamEventType.ToolEnd,
                    ToolCallId = "tc1",
                    ToolName = "search",
                    ToolResult = "ok",
                    ToolIsError = false
                }
            ]),
            session,
            store.Object);

        var entry = result.HistoryEntries.ShouldHaveSingleItem();
        entry.ToolIsError.ShouldBeFalse();
    }

    [Fact]
    public async Task ProcessAndSaveAsync_ToolEnd_WithResult_ContentContainsResult()
    {
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-res"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1") };
        var store = new Mock<ISessionStore>();

        var result = await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent
                {
                    Type = AgentStreamEventType.ToolEnd,
                    ToolCallId = "tc1",
                    ToolName = "fetch",
                    ToolResult = "the result text",
                    ToolIsError = false
                }
            ]),
            session,
            store.Object);

        var entry = result.HistoryEntries.ShouldHaveSingleItem();
        entry.Content.ShouldBe("the result text");
    }

    [Fact]
    public async Task ProcessAndSaveAsync_ThinkingOnlyWithMessageEnd_AddsSystemStallEntry()
    {
        // Arrange: provider returned only thinking blocks, no visible text, no tools.
        // MessageEnd signals a clean turn completion — but no user-visible content was produced.
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-think-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1") };
        var store = new Mock<ISessionStore>();

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ThinkingDelta, ThinkingContent = "Let me think deeply..." },
                new AgentStreamEvent { Type = AgentStreamEventType.ThinkingDelta, ThinkingContent = "Still thinking." },
                new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd }
            ]),
            session,
            store.Object);

        // Assert: exactly one system entry with the stall message.
        session.History.ShouldHaveSingleItem();
        session.History[0].Role.ShouldBe(MessageRole.System);
        session.History[0].Content.ShouldContain("only reasoning content");
        store.Verify(s => s.SaveAsync(session, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessAndSaveAsync_ThinkingThenText_DoesNotAddStallEntry()
    {
        // Arrange: provider returned thinking blocks followed by actual text — normal extended-thinking turn.
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-think-2"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1") };
        var store = new Mock<ISessionStore>();

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ThinkingDelta, ThinkingContent = "Let me think..." },
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "Here is my answer." },
                new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd }
            ]),
            session,
            store.Object);

        // Assert: one assistant entry with the visible content, no system stall entry.
        session.History.ShouldHaveSingleItem();
        session.History[0].Role.ShouldBe(MessageRole.Assistant);
        session.History[0].Content.ShouldBe("Here is my answer.");
    }

    [Fact]
    public async Task ProcessAndSaveAsync_ThinkingOnlyWithoutMessageEnd_DoesNotAddStallEntry()
    {
        // Arrange: stream dropped mid-way (no MessageEnd) — no stall sentinel should be added
        // because the session may resume and we don't want spurious system entries.
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-think-3"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1") };
        var store = new Mock<ISessionStore>();

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ThinkingDelta, ThinkingContent = "Let me think..." }
                // No MessageEnd — stream dropped
            ]),
            session,
            store.Object);

        // Assert: history is empty — no stall entry for incomplete streams.
        session.History.ShouldBeEmpty();
    }

    private static async IAsyncEnumerable<AgentStreamEvent> ToAsyncEnumerable(IEnumerable<AgentStreamEvent> events)
    {
        foreach (var evt in events)
        {
            yield return evt;
            await Task.Yield();
        }
    }
}

