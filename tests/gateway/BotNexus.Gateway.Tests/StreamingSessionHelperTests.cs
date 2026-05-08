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

        var entry = result.HistoryEntries.ShouldHaveSingleItem();
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

        var entry = result.HistoryEntries.ShouldHaveSingleItem();
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

        var entry = result.HistoryEntries.ShouldHaveSingleItem();
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

    private static async IAsyncEnumerable<AgentStreamEvent> ToAsyncEnumerable(IEnumerable<AgentStreamEvent> events)
    {
        foreach (var evt in events)
        {
            yield return evt;
            await Task.Yield();
        }
    }
}

