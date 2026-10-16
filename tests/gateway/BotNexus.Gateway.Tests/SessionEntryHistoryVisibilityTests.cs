using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;

namespace BotNexus.Gateway.Tests;

public sealed class SessionEntryHistoryVisibilityTests
{
    [Fact]
    public void IsVisible_CrashSentinelsNeverAppear()
    {
        foreach (var role in new[] { MessageRole.User, MessageRole.Assistant, MessageRole.System, MessageRole.Tool, MessageRole.Notification, MessageRole.FromString("custom-role") })
            SessionEntryHistoryVisibility.IsVisible(Entry(role, "recovery") with { IsCrashSentinel = true }).ShouldBeFalse();
    }

    [Fact]
    public void IsVisible_NoReplyAssistantTurnsAreHidden_WithoutHidingOtherRolesOrText()
    {
        foreach (var text in new[] { "NO_REPLY", "  NO_REPLY\r\n" })
            SessionEntryHistoryVisibility.IsVisible(Entry(MessageRole.Assistant, text)).ShouldBeFalse();
        foreach (var text in new[] { "no_reply", "NO_REPLY details", "ordinary reply" })
            SessionEntryHistoryVisibility.IsVisible(Entry(MessageRole.Assistant, text)).ShouldBeTrue();
        foreach (var role in new[] { MessageRole.User, MessageRole.Assistant, MessageRole.System, MessageRole.Tool, MessageRole.Notification, MessageRole.FromString("custom-role") }.Where(role => role != MessageRole.Assistant))
            SessionEntryHistoryVisibility.IsVisible(Entry(role, "NO_REPLY")).ShouldBeTrue();
    }

    [Fact]
    public void IsVisible_ContentlessAssistantGhostsAreHidden()
    {
        foreach (var content in new[] { "", " \r\n" })
            SessionEntryHistoryVisibility.IsVisible(Entry(MessageRole.Assistant, content)).ShouldBeFalse();
    }

    [Fact]
    public void IsVisible_ThinkingAttachmentsAndToolIdentityKeepAssistantRowsVisible()
    {
        var empty = Entry(MessageRole.Assistant, "");
        foreach (var entry in new[]
        {
            empty with { ThinkingContent = "reasoning" },
            empty with { OriginalContentParts = [new TextContentPart { Text = "attachment", FileName = "note.txt", MimeType = "text/plain" }] },
            empty with { ToolCallId = "call-1" },
            empty with { ToolName = "tool" }
        })
            SessionEntryHistoryVisibility.IsVisible(entry).ShouldBeTrue();
        SessionEntryHistoryVisibility.IsVisible(empty with { ThinkingContent = "  ", OriginalContentParts = [] }).ShouldBeFalse();
    }

    [Fact]
    public void IsVisible_FoldedHistoryAndCompactionMarkersRemainReachable()
    {
        SessionEntryHistoryVisibility.IsVisible(Entry(MessageRole.User, "old") with { IsHistory = true }).ShouldBeTrue();
        SessionEntryHistoryVisibility.IsVisible(Entry(MessageRole.Assistant, "") with { IsCompactionSummary = true }).ShouldBeTrue();
        SessionEntryHistoryVisibility.IsVisible(Entry(MessageRole.System, "summary") with { IsCompactionSummary = true, IsHistory = true }).ShouldBeTrue();
    }

    [Fact]
    public void IsVisible_EmptyNonAssistantRowsRetainExistingTranscriptSemantics()
    {
        foreach (var role in new[] { MessageRole.User, MessageRole.Assistant, MessageRole.System, MessageRole.Tool, MessageRole.Notification, MessageRole.FromString("custom-role") }.Where(role => role != MessageRole.Assistant))
            SessionEntryHistoryVisibility.IsVisible(Entry(role, "")).ShouldBeTrue();
    }

    [Fact]
    public void Project_UsesSameVisibilityPredicate_WithoutChangingOrderOrMarkers()
    {
        var entries = new[]
        {
            Entry(MessageRole.User, "hello"),
            Entry(MessageRole.Assistant, "NO_REPLY"),
            Entry(MessageRole.System, "sentinel") with { IsCrashSentinel = true },
            Entry(MessageRole.Assistant, ""),
            Entry(MessageRole.Assistant, "thinking") with { IsHistory = true },
            Entry(MessageRole.Tool, "result") with { ToolCallId = "call-1" },
            Entry(MessageRole.System, "summary") with { IsCompactionSummary = true }
        };
        var session = new GatewaySession(new Session { SessionId = SessionId.From("visibility-session") });
        session.HydrateAgentId(AgentId.From("agent-a"));
        session.AddEntries(entries);

        var projected = ConversationHistoryProjection.Project([session]);

        projected.Select(entry => entry.Content).ShouldBe(entries.Where(SessionEntryHistoryVisibility.IsVisible).Select(entry => entry.Content));
        projected.Select(entry => entry.Content).ShouldBe(new[] { "hello", "thinking", "result", "summary" });
        projected.Select(entry => entry.EntryId).ShouldBe(new[] { "visibility-session#0", "visibility-session#1", "visibility-session#2", "visibility-session#3" });
        projected[1].IsFolded.ShouldBeTrue();
        projected[^1].Kind.ShouldBe("compaction");
    }

    [Fact]
    public void IsVisible_NullEntryIsRejected()
    {
        Should.Throw<ArgumentNullException>(() => SessionEntryHistoryVisibility.IsVisible(null));
    }

    private static SessionEntry Entry(MessageRole role, string content) => new() { Role = role, Content = content };
}
