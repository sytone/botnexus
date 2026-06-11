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
        // Write-ahead flush on ToolStart + final save = 2 calls.
        store.Verify(s => s.SaveAsync(session, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ProcessAndSaveAsync_PersistsThinkingContentOnAssistantEntry()
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
        session.History[0].ThinkingContent.ShouldBe("Let me think...");
        callbackTypes.ShouldContain(AgentStreamEventType.ThinkingDelta);
    }

    [Fact]
    public async Task ProcessAndSaveAsync_NoThinking_ThinkingContentIsNull()
    {
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-2b"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1") };
        var store = new Mock<ISessionStore>();

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "Hello" }
            ]),
            session,
            store.Object);

        session.History.ShouldHaveSingleItem();
        session.History[0].ThinkingContent.ShouldBeNull();
    }

    [Fact]
    public async Task ProcessAndSaveAsync_MultipleThinkingDeltas_Accumulated()
    {
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-2c"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1") };
        var store = new Mock<ISessionStore>();

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ThinkingDelta, ThinkingContent = "First " },
                new AgentStreamEvent { Type = AgentStreamEventType.ThinkingDelta, ThinkingContent = "second " },
                new AgentStreamEvent { Type = AgentStreamEventType.ThinkingDelta, ThinkingContent = "third" },
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "Answer" }
            ]),
            session,
            store.Object);

        session.History.ShouldHaveSingleItem();
        session.History[0].ThinkingContent.ShouldBe("First second third");
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

    // ── Write-ahead persistence tests (#1052) ───────────────────────────────

    [Fact]
    public async Task ProcessAndSaveAsync_ToolStart_PersistsImmediately_WriteAhead()
    {
        // Arrange: stream has ToolStart then stalls (no ToolEnd, no TurnEnd).
        // The write-ahead should persist the ToolStart entry before waiting for more events.
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-wa-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1") };
        var store = new Mock<ISessionStore>();
        var saveCallCount = 0;
        var historyAtFirstSave = new List<SessionEntry>();
        store.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Callback<GatewaySession, CancellationToken>((s, _) =>
            {
                saveCallCount++;
                if (saveCallCount == 1)
                    historyAtFirstSave.AddRange(s.History);
            })
            .Returns(Task.CompletedTask);

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ToolStart, ToolCallId = "tc1", ToolName = "read" },
                new AgentStreamEvent { Type = AgentStreamEventType.ToolEnd, ToolCallId = "tc1", ToolName = "read", ToolResult = "file content" }
            ]),
            session,
            store.Object);

        // First save should have the ToolStart entry persisted (write-ahead).
        saveCallCount.ShouldBeGreaterThanOrEqualTo(2);
        historyAtFirstSave.ShouldNotBeEmpty();
        historyAtFirstSave[0].ToolName.ShouldBe("read");
        historyAtFirstSave[0].Content.ShouldContain("started");
    }

    [Fact]
    public async Task ProcessAndSaveAsync_MultipleToolStarts_EachPersistsImmediately()
    {
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-wa-2"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1") };
        var store = new Mock<ISessionStore>();
        var saveCount = 0;
        store.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Callback<GatewaySession, CancellationToken>((_, _) => saveCount++)
            .Returns(Task.CompletedTask);

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ToolStart, ToolCallId = "tc1", ToolName = "read" },
                new AgentStreamEvent { Type = AgentStreamEventType.ToolEnd, ToolCallId = "tc1", ToolName = "read", ToolResult = "ok" },
                new AgentStreamEvent { Type = AgentStreamEventType.ToolStart, ToolCallId = "tc2", ToolName = "write" },
                new AgentStreamEvent { Type = AgentStreamEventType.ToolEnd, ToolCallId = "tc2", ToolName = "write", ToolResult = "ok" }
            ]),
            session,
            store.Object);

        // 2 write-ahead saves (one per ToolStart) + 1 final save = 3 total.
        saveCount.ShouldBe(3);
    }

    [Fact]
    public async Task ProcessAndSaveAsync_WriteAheadFailure_DoesNotAbortStream()
    {
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-wa-3"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1") };
        var store = new Mock<ISessionStore>();
        var callCount = 0;
        store.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Callback<GatewaySession, CancellationToken>((_, _) =>
            {
                callCount++;
                if (callCount == 1)
                    throw new InvalidOperationException("Simulated write-ahead failure");
            })
            .Returns(Task.CompletedTask);

        // Should NOT throw — write-ahead failure is best-effort.
        var result = await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ToolStart, ToolCallId = "tc1", ToolName = "shell" },
                new AgentStreamEvent { Type = AgentStreamEventType.ToolEnd, ToolCallId = "tc1", ToolName = "shell", ToolResult = "output" },
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "Done." }
            ]),
            session,
            store.Object);

        // Stream completed successfully despite write-ahead failure.
        session.History.Count.ShouldBeGreaterThanOrEqualTo(2);
        result.AssistantContent.ShouldBe("Done.");
    }

    // ── Stall watchdog integration tests (#1052) ─────────────────────────────

    [Fact]
    public async Task ProcessAndSaveAsync_WithStallWatchdog_StreamCompletesNormally()
    {
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-wd-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1") };
        var store = new Mock<ISessionStore>();
        var watchdog = new ProviderStallWatchdog(TimeSpan.FromSeconds(5));

        var result = await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "Hello" },
                new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd }
            ]),
            session,
            store.Object,
            new StreamingSessionOptions(StallWatchdog: watchdog));

        result.AssistantContent.ShouldBe("Hello");
        session.History.ShouldHaveSingleItem();
        session.History[0].Content.ShouldBe("Hello");
    }

    [Fact]
    public async Task ProcessAndSaveAsync_WithStallWatchdog_TimeoutSurfacesError()
    {
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-wd-2"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1") };
        var store = new Mock<ISessionStore>();
        var watchdog = new ProviderStallWatchdog(TimeSpan.FromMilliseconds(100));

        var result = await StreamingSessionHelper.ProcessAndSaveAsync(
            StallAfterFirst(),
            session,
            store.Object,
            new StreamingSessionOptions(IncludeErrorsInHistory: true, StallWatchdog: watchdog));

        // The error event from the watchdog should be persisted in history.
        session.History.Any(e => e.Role == MessageRole.System && e.Content!.Contains("Provider stall detected")).ShouldBeTrue();
    }

    private static async IAsyncEnumerable<AgentStreamEvent> StallAfterFirst()
    {
        yield return new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "partial" };
        await Task.Delay(TimeSpan.FromSeconds(30)); // Will be cut short by watchdog
        yield return new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd };
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

