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
    public async Task ProcessAndSaveAsync_ThinkingOnlyWithMessageEnd_AddsEmptyAssistantEntry()
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

        // Assert: exactly one empty assistant entry to close the turn (no user-visible message).
        session.History.ShouldHaveSingleItem();
        session.History[0].Role.ShouldBe(MessageRole.Assistant);
        session.History[0].Content.ShouldBe(string.Empty);
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

    [Fact]
    public async Task ProcessAndSaveAsync_PrePersistedToolStart_DoesNotDuplicateEntry()
    {
        var session = new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("session-prepersisted"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1")
        };
        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.Tool,
            Content = "Tool 'exec' started.",
            ToolName = "exec",
            ToolCallId = "tc-prepersisted",
            ToolArgs = "{\"command\":\"git status\"}"
        });
        var store = new Mock<ISessionStore>();

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent
                {
                    Type = AgentStreamEventType.ToolStart,
                    ToolCallId = "tc-prepersisted",
                    ToolName = "exec",
                    ToolArgs = new Dictionary<string, object?> { ["command"] = "git status" }
                },
                new AgentStreamEvent
                {
                    Type = AgentStreamEventType.ToolEnd,
                    ToolCallId = "tc-prepersisted",
                    ToolName = "exec",
                    ToolResult = "clean"
                }
            ]),
            session,
            store.Object);

        session.GetHistorySnapshot().Count(entry =>
            entry.ToolCallId == "tc-prepersisted" && entry.ToolArgs is not null).ShouldBe(1);
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

    // ── Tool-result write-time size cap tests (#1598) ───────────────────────

    [Fact]
    public async Task ProcessAndSaveAsync_OversizedToolResult_IsTruncatedWithMarkerAtWriteTime()
    {
        // Arrange: a tool result far larger than the configured byte cap.
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-cap-1"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1") };
        var store = new Mock<ISessionStore>();
        var big = new string('x', 5000);

        var result = await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ToolEnd, ToolCallId = "tc1", ToolName = "shell", ToolResult = big }
            ]),
            session,
            store.Object,
            new StreamingSessionOptions(MaxPersistedToolResultBytes: 1000));

        // The persisted entry must NOT contain the full 5000-byte result.
        var entry = result.HistoryEntries.ShouldHaveSingleItem();
        entry.Role.ShouldBe(MessageRole.Tool);
        System.Text.Encoding.UTF8.GetByteCount(entry.Content!).ShouldBeLessThan(big.Length);
        entry.Content.ShouldContain("[truncated");
        entry.Content.ShouldContain("bytes]");
        // The same truncated entry must be what landed in session history (write-time, not display-only).
        session.History.ShouldHaveSingleItem();
        session.History[0].Content.ShouldBe(entry.Content);
        System.Text.Encoding.UTF8.GetByteCount(session.History[0].Content!).ShouldBeLessThan(big.Length);
    }

    [Fact]
    public async Task ProcessAndSaveAsync_TruncationMarker_ReportsOmittedByteCount()
    {
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-cap-2"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1") };
        var store = new Mock<ISessionStore>();
        var big = new string('a', 4000);

        var result = await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ToolEnd, ToolCallId = "tc1", ToolName = "read", ToolResult = big }
            ]),
            session,
            store.Object,
            new StreamingSessionOptions(MaxPersistedToolResultBytes: 1000));

        var entry = result.HistoryEntries.ShouldHaveSingleItem();
        // Marker reports how many bytes were dropped (original 4000 - retained ~1000 = ~3000).
        var match = System.Text.RegularExpressions.Regex.Match(entry.Content!, @"\[truncated (\d+) bytes\]");
        match.Success.ShouldBeTrue();
        var omitted = int.Parse(match.Groups[1].Value);
        omitted.ShouldBeGreaterThan(0);
        omitted.ShouldBeLessThan(4000);
    }

    [Fact]
    public async Task ProcessAndSaveAsync_UnderCapToolResult_IsNotTruncated()
    {
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-cap-3"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1") };
        var store = new Mock<ISessionStore>();

        var result = await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ToolEnd, ToolCallId = "tc1", ToolName = "clock", ToolResult = "12:00" }
            ]),
            session,
            store.Object,
            new StreamingSessionOptions(MaxPersistedToolResultBytes: 1000));

        var entry = result.HistoryEntries.ShouldHaveSingleItem();
        entry.Content.ShouldBe("12:00");
        entry.Content.ShouldNotContain("[truncated");
    }

    [Fact]
    public async Task ProcessAndSaveAsync_CapDisabled_LeavesOversizedResultIntact()
    {
        // Default StreamingSessionOptions (MaxPersistedToolResultBytes = 0) disables the cap.
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-cap-4"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1") };
        var store = new Mock<ISessionStore>();
        var big = new string('z', 5000);

        var result = await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ToolEnd, ToolCallId = "tc1", ToolName = "shell", ToolResult = big }
            ]),
            session,
            store.Object);

        var entry = result.HistoryEntries.ShouldHaveSingleItem();
        entry.Content.ShouldBe(big);
        entry.Content.ShouldNotContain("[truncated");
    }

    [Fact]
    public async Task ProcessAndSaveAsync_TruncationCountsBytesNotChars_ForMultibyteContent()
    {
        // Multibyte UTF-8 content: each 'あ' is 3 bytes. A 1000-char string is 3000 bytes,
        // so a 1000-byte cap must truncate it even though the char count is at the boundary.
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-cap-5"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1") };
        var store = new Mock<ISessionStore>();
        var multibyte = new string('\u3042', 1000); // 3000 UTF-8 bytes

        var result = await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ToolEnd, ToolCallId = "tc1", ToolName = "read", ToolResult = multibyte }
            ]),
            session,
            store.Object,
            new StreamingSessionOptions(MaxPersistedToolResultBytes: 1000));

        var entry = result.HistoryEntries.ShouldHaveSingleItem();
        entry.Content.ShouldContain("[truncated");
        // Retained content (excluding the marker) must be within the byte cap.
        System.Text.Encoding.UTF8.GetByteCount(entry.Content!).ShouldBeLessThan(3000);
    }

    [Fact]
    public async Task ProcessAndSaveAsync_Truncation_DoesNotLeaveLoneSurrogate()
    {
        // A run of astral (surrogate-pair) emoji straddling the byte cap must never leave a
        // dangling high surrogate at the cut — the slice must fall on a rune boundary.
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-cap-6"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1") };
        var store = new Mock<ISessionStore>();
        var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 1000)); // 4 UTF-8 bytes each

        var result = await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ToolEnd, ToolCallId = "tc1", ToolName = "shell", ToolResult = emoji }
            ]),
            session,
            store.Object,
            new StreamingSessionOptions(MaxPersistedToolResultBytes: 1003)); // off-boundary cap

        var entry = result.HistoryEntries.ShouldHaveSingleItem();
        // No char in the persisted content may be a high surrogate without a following low surrogate.
        var content = entry.Content!;
        for (int i = 0; i < content.Length; i++)
        {
            if (char.IsHighSurrogate(content[i]))
                (i + 1 < content.Length && char.IsLowSurrogate(content[i + 1])).ShouldBeTrue($"lone high surrogate at index {i}");
            if (char.IsLowSurrogate(content[i]))
                (i > 0 && char.IsHighSurrogate(content[i - 1])).ShouldBeTrue($"lone low surrogate at index {i}");
        }
    }

    // ── Crash-sentinel lease across multi-turn streams (#2135) ────────────

    private static SessionEntry NewCrashSentinel() => new()
    {
        Role = MessageRole.System,
        Content = "[agent turn in progress - gateway restarted if visible]",
        IsCrashSentinel = true
    };

    [Fact]
    public async Task ProcessAndSaveAsync_MultiTurnStream_RetainsCrashSentinelAfterIntermediateTurnEnd()
    {
        // A crash sentinel written before the run must survive an intermediate TurnEnd
        // snapshot so a process death during a following tool/model turn still leaves a
        // replayable marker (#2135).
        var session = new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("session-sentinel-mid"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1")
        };
        session.AddEntry(NewCrashSentinel());

        var sentinelPresentAtTurnEndSave = (bool?)null;
        var store = new Mock<ISessionStore>();
        var saveCount = 0;
        store
            .Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Callback<GatewaySession, CancellationToken>((s, _) =>
            {
                saveCount++;
                // Capture sentinel presence at the FIRST (intermediate TurnEnd) save only.
                if (saveCount == 1)
                    sentinelPresentAtTurnEndSave = s.History.Any(e => e.IsCrashSentinel);
            })
            .Returns(Task.CompletedTask);

        // Stream: first turn ends (intermediate TurnEnd), then a second model turn starts.
        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.MessageStart, MessageId = "m1" },
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "first ", MessageId = "m1" },
                new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd, MessageId = "m1" },
                new AgentStreamEvent { Type = AgentStreamEventType.TurnEnd, MessageId = "m1" },
                new AgentStreamEvent { Type = AgentStreamEventType.MessageStart, MessageId = "m2" },
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "second", MessageId = "m2" },
                new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd, MessageId = "m2" }
            ]),
            session,
            store.Object);

        // The intermediate TurnEnd save must have retained the sentinel.
        sentinelPresentAtTurnEndSave.ShouldBe(true);
    }

    [Fact]
    public async Task ProcessAndSaveAsync_CleanFinalCompletion_RemovesCrashSentinel()
    {
        // The sentinel is removed only when the whole run reaches its authoritative final save.
        var session = new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("session-sentinel-final"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1")
        };
        session.AddEntry(NewCrashSentinel());
        var store = new Mock<ISessionStore>();

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.MessageStart, MessageId = "m1" },
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "first ", MessageId = "m1" },
                new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd, MessageId = "m1" },
                new AgentStreamEvent { Type = AgentStreamEventType.TurnEnd, MessageId = "m1" },
                new AgentStreamEvent { Type = AgentStreamEventType.MessageStart, MessageId = "m2" },
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "second", MessageId = "m2" },
                new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd, MessageId = "m2" }
            ]),
            session,
            store.Object);

        // After the run completes cleanly, no sentinel may remain.
        session.History.Any(e => e.IsCrashSentinel).ShouldBeFalse();
    }

    [Fact]
    public async Task ProcessAndSaveAsync_ToolStartWriteAhead_RetainsCrashSentinel()
    {
        // A mid-run tool-start write-ahead is a continuation, not a terminal boundary; it must
        // preserve the sentinel so a crash during tool execution still leaves a replayable marker.
        var session = new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("session-sentinel-tool"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1")
        };
        session.AddEntry(NewCrashSentinel());

        var sentinelPresentAtToolStartSave = (bool?)null;
        var store = new Mock<ISessionStore>();
        var saveCount = 0;
        store
            .Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Callback<GatewaySession, CancellationToken>((s, _) =>
            {
                saveCount++;
                if (saveCount == 1)
                    sentinelPresentAtToolStartSave = s.History.Any(e => e.IsCrashSentinel);
            })
            .Returns(Task.CompletedTask);

        // Simulate a hard death: enumerate only up to the tool-start write-ahead by stopping
        // the stream after ToolStart. Since there is no ToolEnd/final, the sentinel written at
        // the tool-start save is the durable replay marker.
        await StreamingSessionHelper.ProcessAndSaveAsync(
            DeathAfterToolStart(),
            session,
            store.Object);

        // The first (tool-start write-ahead) save must have retained the sentinel.
        sentinelPresentAtToolStartSave.ShouldBe(true);
    }

    private static async IAsyncEnumerable<AgentStreamEvent> DeathAfterToolStart()
    {
        yield return new AgentStreamEvent { Type = AgentStreamEventType.MessageStart, MessageId = "m1" };
        yield return new AgentStreamEvent { Type = AgentStreamEventType.ToolStart, ToolCallId = "tc1", ToolName = "shell", MessageId = "m1" };
        await Task.Yield();
    }
    private static async IAsyncEnumerable<AgentStreamEvent> StallAfterFirst()
    {
        yield return new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "partial" };
        await Task.Delay(TimeSpan.FromSeconds(30)); // Will be cut short by watchdog
        yield return new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd };
    }

    [Fact]
    public async Task ProcessAndSaveAsync_WhenAssistantMessageKindSet_StampsStreamedAssistantEntry()
    {
        // #2149: a streaming parent turn produced while handling a sub-agent completion must stamp
        // the assistant entry with subagent-response so history replay agrees with live delivery.
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-kind"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1") };
        var store = new Mock<ISessionStore>();

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "parent " },
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "reply" },
                new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd }
            ]),
            session,
            store.Object,
            new StreamingSessionOptions(AssistantMessageKind: BotNexus.Domain.Primitives.MessageKind.SubAgentResponse));

        var entry = session.History.ShouldHaveSingleItem();
        entry.Role.ShouldBe(MessageRole.Assistant);
        entry.Content.ShouldBe("parent reply");
        entry.ResolveKind().ShouldBe(BotNexus.Domain.Primitives.MessageKind.SubAgentResponse);
    }

    [Fact]
    public async Task ProcessAndSaveAsync_WhenNoAssistantMessageKind_DefaultsToUnstampedMessageEntry()
    {
        // #2149 sad path: an ordinary streamed turn must NOT stamp a non-default kind - the entry
        // stays unstamped (null Kind) and resolves to MessageKind.Message.
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("session-kind-2"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-1") };
        var store = new Mock<ISessionStore>();

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "hi" },
                new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd }
            ]),
            session,
            store.Object);

        var entry = session.History.ShouldHaveSingleItem();
        entry.Kind.ShouldBeNull();
        entry.ResolveKind().ShouldBe(BotNexus.Domain.Primitives.MessageKind.Message);
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

