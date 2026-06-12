using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Sessions;

namespace BotNexus.Gateway.Sessions.Tests;

public class ToolResultTrimmerTests
{
    private static ToolResultTrimmingOptions DefaultOptions() => new();

    private static SessionEntry UserEntry(string content = "hello") =>
        new() { Role = MessageRole.User, Content = content };

    private static SessionEntry AssistantEntry(string content = "response") =>
        new() { Role = MessageRole.Assistant, Content = content };

    private static SessionEntry ToolResult(string content, string? toolName = "read", string? toolCallId = null) =>
        new()
        {
            Role = MessageRole.Tool,
            Content = content,
            ToolName = toolName,
            ToolCallId = toolCallId ?? Guid.NewGuid().ToString()
        };

    private static SessionEntry ToolStart(string toolName = "read") =>
        new()
        {
            Role = MessageRole.Tool,
            Content = "",
            ToolName = toolName,
            ToolArgs = """{"path":"test.txt"}""",
            ToolCallId = Guid.NewGuid().ToString()
        };

    private static string LargeContent(int chars = 1000) => new('x', chars);

    [Fact]
    public void Trim_WhenDisabled_ReturnsOriginalList()
    {
        var options = new ToolResultTrimmingOptions { Enabled = false };
        var trimmer = new ToolResultTrimmer(options);
        var entries = new List<SessionEntry>
        {
            UserEntry(), AssistantEntry(), ToolResult(LargeContent()),
            UserEntry(), AssistantEntry(), UserEntry(), AssistantEntry(), UserEntry()
        };

        var result = trimmer.Trim(entries);

        Assert.Same(entries, result);
    }

    [Fact]
    public void Trim_EmptyList_ReturnsSameReference()
    {
        var trimmer = new ToolResultTrimmer(DefaultOptions());
        var entries = Array.Empty<SessionEntry>();

        var result = trimmer.Trim(entries);

        Assert.Same(entries, result);
    }

    [Fact]
    public void Trim_SmallToolResult_NeverTrimmed()
    {
        var trimmer = new ToolResultTrimmer(DefaultOptions());
        // Content under 500 chars
        var entries = new List<SessionEntry>
        {
            UserEntry(), ToolResult("short result", "shell"),
            UserEntry(), AssistantEntry(),
            UserEntry(), AssistantEntry(),
            UserEntry(), AssistantEntry(),
            UserEntry() // 4 turns later
        };

        var result = trimmer.Trim(entries);

        Assert.Same(entries, result);
    }

    [Fact]
    public void Trim_LargeToolResult_UnderAgeThreshold_NotTrimmed()
    {
        var trimmer = new ToolResultTrimmer(DefaultOptions());
        // Only 1 turn old, default threshold is 3
        var entries = new List<SessionEntry>
        {
            UserEntry(), ToolResult(LargeContent(), "read"),
            UserEntry() // 1 turn later
        };

        var result = trimmer.Trim(entries);

        Assert.Same(entries, result);
    }

    [Fact]
    public void Trim_LargeToolResult_ExceedsAgeThreshold_Trimmed()
    {
        var trimmer = new ToolResultTrimmer(DefaultOptions());
        var largeContent = LargeContent(1000);
        var entries = new List<SessionEntry>
        {
            UserEntry(), ToolResult(largeContent, "read"),
            UserEntry(), AssistantEntry(),
            UserEntry(), AssistantEntry(),
            UserEntry() // 3 turns later — equals threshold
        };

        var result = trimmer.Trim(entries);

        Assert.NotSame(entries, result);
        Assert.Equal(entries.Count, result.Count);

        // The tool result should be a tombstone
        var trimmed = result[1];
        Assert.StartsWith(ToolResultTrimmer.TombstoneMarker, trimmed.Content);
        Assert.Contains("read", trimmed.Content);
        Assert.Contains("1000 chars", trimmed.Content);
        Assert.Contains("3 turns ago", trimmed.Content);
    }

    [Fact]
    public void Trim_ShellTool_UsesCustomThreshold_Of2()
    {
        var trimmer = new ToolResultTrimmer(DefaultOptions());
        var entries = new List<SessionEntry>
        {
            UserEntry(), ToolResult(LargeContent(), "shell"),
            UserEntry(), AssistantEntry(),
            UserEntry() // 2 turns — shell threshold
        };

        var result = trimmer.Trim(entries);

        Assert.NotSame(entries, result);
        Assert.StartsWith(ToolResultTrimmer.TombstoneMarker, result[1].Content);
    }

    [Fact]
    public void Trim_MemorySearchTool_HigherThreshold()
    {
        var trimmer = new ToolResultTrimmer(DefaultOptions());
        // 4 turns — under memory_search threshold of 5
        var entries = new List<SessionEntry>
        {
            UserEntry(), ToolResult(LargeContent(), "memory_search"),
            UserEntry(), AssistantEntry(),
            UserEntry(), AssistantEntry(),
            UserEntry(), AssistantEntry(),
            UserEntry() // 4 turns
        };

        var result = trimmer.Trim(entries);

        Assert.Same(entries, result); // Not trimmed — threshold is 5
    }

    [Fact]
    public void Trim_MemorySearchTool_AtThreshold_Trimmed()
    {
        var trimmer = new ToolResultTrimmer(DefaultOptions());
        var entries = new List<SessionEntry>
        {
            UserEntry(), ToolResult(LargeContent(), "memory_search"),
            UserEntry(), AssistantEntry(),
            UserEntry(), AssistantEntry(),
            UserEntry(), AssistantEntry(),
            UserEntry(), AssistantEntry(),
            UserEntry() // 5 turns
        };

        var result = trimmer.Trim(entries);

        Assert.NotSame(entries, result);
        Assert.StartsWith(ToolResultTrimmer.TombstoneMarker, result[1].Content);
    }

    [Fact]
    public void Trim_TombstonePreview_ContainsFirstNChars()
    {
        var options = new ToolResultTrimmingOptions { TombstonePreviewChars = 50 };
        var trimmer = new ToolResultTrimmer(options);
        var content = "ABCDEFGHIJ" + new string('Z', 990); // 1000 chars, starts with known prefix
        var entries = new List<SessionEntry>
        {
            UserEntry(), ToolResult(content, "read"),
            UserEntry(), AssistantEntry(),
            UserEntry(), AssistantEntry(),
            UserEntry() // 3 turns
        };

        var result = trimmer.Trim(entries);

        var tombstone = result[1].Content;
        Assert.Contains("ABCDEFGHIJ", tombstone);
        // Should NOT contain the full 1000 chars
        Assert.True(tombstone.Length < content.Length);
    }

    [Fact]
    public void Trim_ToolStartEntry_NeverTrimmed()
    {
        var trimmer = new ToolResultTrimmer(DefaultOptions());
        var entries = new List<SessionEntry>
        {
            UserEntry(), ToolStart("read"),
            UserEntry(), AssistantEntry(),
            UserEntry(), AssistantEntry(),
            UserEntry() // 3 turns
        };

        var result = trimmer.Trim(entries);

        Assert.Same(entries, result); // ToolStart entries have ToolArgs, should be skipped
    }

    [Fact]
    public void Trim_AlreadyTombstone_NotRetrimmed()
    {
        var trimmer = new ToolResultTrimmer(DefaultOptions());
        var tombstoneContent = $"{ToolResultTrimmer.TombstoneMarker} — read, 5000 chars, produced 4 turns ago]\npreview…";
        var entries = new List<SessionEntry>
        {
            UserEntry(),
            new() { Role = MessageRole.Tool, Content = tombstoneContent, ToolName = "read" },
            UserEntry(), AssistantEntry(),
            UserEntry(), AssistantEntry(),
            UserEntry() // well past threshold
        };

        var result = trimmer.Trim(entries);

        Assert.Same(entries, result);
    }

    [Fact]
    public void Trim_PreservesOtherEntryProperties()
    {
        var trimmer = new ToolResultTrimmer(DefaultOptions());
        var callId = "call_123";
        var entries = new List<SessionEntry>
        {
            UserEntry(), ToolResult(LargeContent(), "read", callId),
            UserEntry(), AssistantEntry(),
            UserEntry(), AssistantEntry(),
            UserEntry()
        };

        var result = trimmer.Trim(entries);

        var trimmed = result[1];
        Assert.Equal(MessageRole.Tool, trimmed.Role);
        Assert.Equal("read", trimmed.ToolName);
        Assert.Equal(callId, trimmed.ToolCallId);
    }

    [Fact]
    public void Trim_MultipleToolResults_OnlyTrimsEligible()
    {
        var trimmer = new ToolResultTrimmer(DefaultOptions());
        var entries = new List<SessionEntry>
        {
            UserEntry(),
            ToolResult(LargeContent(), "shell"),  // will be 4 turns old → trimmed (shell threshold=2)
            ToolResult("tiny", "grep"),            // too small → not trimmed
            UserEntry(), AssistantEntry(),
            UserEntry(), AssistantEntry(),
            UserEntry(), AssistantEntry(),
            UserEntry()
        };

        var result = trimmer.Trim(entries);

        Assert.NotSame(entries, result);
        Assert.StartsWith(ToolResultTrimmer.TombstoneMarker, result[1].Content);
        Assert.Equal("tiny", result[2].Content); // Preserved
    }

    [Fact]
    public void IsTombstone_DetectsMarker()
    {
        var entry = new SessionEntry
        {
            Role = MessageRole.Tool,
            Content = $"{ToolResultTrimmer.TombstoneMarker} — read, 500 chars, produced 3 turns ago]\npreview…"
        };

        Assert.True(ToolResultTrimmer.IsTombstone(entry));
    }

    [Fact]
    public void IsTombstone_FalseForNormalToolResult()
    {
        var entry = ToolResult("normal output");

        Assert.False(ToolResultTrimmer.IsTombstone(entry));
    }

    [Fact]
    public void IsTombstone_FalseForNonToolEntry()
    {
        var entry = new SessionEntry
        {
            Role = MessageRole.Assistant,
            Content = $"{ToolResultTrimmer.TombstoneMarker} fake"
        };

        Assert.False(ToolResultTrimmer.IsTombstone(entry));
    }
}
