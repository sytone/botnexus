using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for compaction boundary rendering in the chat panel.
/// Verifies that entries with Kind="compaction" are correctly identified and labeled.
/// </summary>
public sealed class CompactionBoundaryTests
{
    [Fact]
    public void ChatMessage_WithKindCompaction_IsCompaction_ReturnsTrue()
    {
        var msg = new ChatMessage("System", "Summary content", DateTimeOffset.UtcNow)
        {
            Kind = "compaction",
            BoundaryLabel = "Context compacted \u00b7 Jun 7 10:30"
        };

        Assert.True(msg.IsCompaction);
        Assert.False(msg.IsBoundary);
    }

    [Fact]
    public void ChatMessage_WithKindBoundary_IsCompaction_ReturnsFalse()
    {
        var msg = new ChatMessage("System", string.Empty, DateTimeOffset.UtcNow)
        {
            Kind = "boundary",
            BoundaryLabel = "Session \u00b7 Jun 7 10:30"
        };

        Assert.False(msg.IsCompaction);
        Assert.True(msg.IsBoundary);
    }

    [Fact]
    public void ChatMessage_WithKindMessage_IsCompaction_ReturnsFalse()
    {
        var msg = new ChatMessage("Assistant", "Hello", DateTimeOffset.UtcNow)
        {
            Kind = "message"
        };

        Assert.False(msg.IsCompaction);
        Assert.False(msg.IsBoundary);
    }

    [Fact]
    public void ConversationHistoryEntryDto_CompactionKind_CarriesContent()
    {
        var entry = new ConversationHistoryEntryDto
        {
            Kind = "compaction",
            SessionId = "sess-123",
            Timestamp = DateTimeOffset.UtcNow,
            Reason = "compaction",
            Content = "## Summary\nUser asked about X. Agent resolved Y."
        };

        Assert.Equal("compaction", entry.Kind);
        Assert.Equal("compaction", entry.Reason);
        Assert.NotNull(entry.Content);
        Assert.Contains("Summary", entry.Content);
    }
}
