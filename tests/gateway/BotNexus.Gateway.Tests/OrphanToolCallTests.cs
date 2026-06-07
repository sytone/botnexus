using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Streaming;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Tests for orphan tool call handling in StreamingSessionHelper.
/// When a ToolStart event has no matching ToolEnd, the helper should synthesize
/// a failed tool result entry for transcript consistency while still delivering
/// any assistant content that was produced.
/// </summary>
public sealed class OrphanToolCallTests
{
    [Fact]
    public async Task OrphanToolStart_SynthesizesFailedToolResult()
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From("session-1"),
            AgentId = AgentId.From("agent-1")
        };
        var store = new Mock<ISessionStore>();

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ToolStart, ToolCallId = "tc1", ToolName = "web_fetch" },
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "Here is the result." },
                new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd }
            ]),
            session,
            store.Object);

        // Should have: ToolStart entry, synthesized ToolEnd (failed), and assistant content
        session.History.Count.ShouldBeGreaterThanOrEqualTo(3);

        var synthesized = session.History.FirstOrDefault(e =>
            e.Role.Equals(MessageRole.Tool) &&
            e.ToolCallId == "tc1" &&
            e.ToolIsError);
        synthesized.ShouldNotBeNull();
        synthesized.Content.ShouldContain("did not complete");

        var assistant = session.History.FirstOrDefault(e => e.Role.Equals(MessageRole.Assistant));
        assistant.ShouldNotBeNull();
        assistant.Content.ShouldBe("Here is the result.");
    }

    [Fact]
    public async Task OrphanToolStart_StillDeliversAssistantText()
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From("session-2"),
            AgentId = AgentId.From("agent-2")
        };
        var store = new Mock<ISessionStore>();

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ToolStart, ToolCallId = "tc1", ToolName = "shell" },
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "I encountered an issue but here is what I found." },
                new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd }
            ]),
            session,
            store.Object);

        // Assistant content must be present — orphan tools must not suppress delivery
        var assistantEntry = session.History.FirstOrDefault(e => e.Role.Equals(MessageRole.Assistant));
        assistantEntry.ShouldNotBeNull();
        assistantEntry.Content.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task MatchedToolCalls_NoSynthesizedEntries()
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From("session-3"),
            AgentId = AgentId.From("agent-3")
        };
        var store = new Mock<ISessionStore>();

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ToolStart, ToolCallId = "tc1", ToolName = "read" },
                new AgentStreamEvent { Type = AgentStreamEventType.ToolEnd, ToolCallId = "tc1", ToolName = "read", ToolResult = "file content" },
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "Done." },
                new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd }
            ]),
            session,
            store.Object);

        // No synthesized entries — tool call completed normally
        var toolEntries = session.History.Where(e => e.Role.Equals(MessageRole.Tool)).ToList();
        toolEntries.Count.ShouldBe(2); // start + end
        toolEntries.Any(e => e.ToolIsError).ShouldBeFalse();
    }

    [Fact]
    public async Task MultipleOrphanToolStarts_AllGetSynthesizedResults()
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From("session-4"),
            AgentId = AgentId.From("agent-4")
        };
        var store = new Mock<ISessionStore>();

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ToolStart, ToolCallId = "tc1", ToolName = "shell" },
                new AgentStreamEvent { Type = AgentStreamEventType.ToolStart, ToolCallId = "tc2", ToolName = "read" },
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "Partial results." },
                new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd }
            ]),
            session,
            store.Object);

        var synthesized = session.History.Where(e =>
            e.Role.Equals(MessageRole.Tool) && e.ToolIsError).ToList();
        synthesized.Count.ShouldBe(2);
        synthesized.Select(e => e.ToolCallId).ShouldBe(["tc1", "tc2"], ignoreOrder: true);
    }

    [Fact]
    public async Task MixedOrphanAndCompleted_OnlyOrphansGetSynthesized()
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From("session-5"),
            AgentId = AgentId.From("agent-5")
        };
        var store = new Mock<ISessionStore>();

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ToolStart, ToolCallId = "tc1", ToolName = "read" },
                new AgentStreamEvent { Type = AgentStreamEventType.ToolEnd, ToolCallId = "tc1", ToolName = "read", ToolResult = "ok" },
                new AgentStreamEvent { Type = AgentStreamEventType.ToolStart, ToolCallId = "tc2", ToolName = "shell" },
                // tc2 never gets a ToolEnd — orphaned
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "Partial." },
                new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd }
            ]),
            session,
            store.Object);

        var errorEntries = session.History.Where(e =>
            e.Role.Equals(MessageRole.Tool) && e.ToolIsError).ToList();
        errorEntries.Count.ShouldBe(1);
        errorEntries[0].ToolCallId.ShouldBe("tc2");

        // tc1 should have completed normally
        var tc1End = session.History.FirstOrDefault(e =>
            e.Role.Equals(MessageRole.Tool) && e.ToolCallId == "tc1" && !e.ToolIsError && e.ToolArgs is null);
        tc1End.ShouldNotBeNull();
    }

    [Fact]
    public async Task WhitespaceOnlyAssistantContent_NotDelivered()
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From("session-6"),
            AgentId = AgentId.From("agent-6")
        };
        var store = new Mock<ISessionStore>();

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.ToolStart, ToolCallId = "tc1", ToolName = "shell" },
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "   " },
                new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd }
            ]),
            session,
            store.Object);

        // Whitespace-only content should not be added as an assistant entry
        var assistantEntry = session.History.FirstOrDefault(e => e.Role.Equals(MessageRole.Assistant));
        assistantEntry.ShouldBeNull();

        // But the orphan tool should still get synthesized
        var synthesized = session.History.FirstOrDefault(e =>
            e.Role.Equals(MessageRole.Tool) && e.ToolIsError);
        synthesized.ShouldNotBeNull();
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
