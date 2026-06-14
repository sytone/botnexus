using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Sessions;

namespace BotNexus.Gateway.Sessions.Tests;

public sealed class SessionTranscriptRendererTests
{
    [Fact]
    public void RenderMarkdown_ReturnsNull_WhenHistoryIsEmpty()
    {
        var session = CreateSession();

        var result = SessionTranscriptRenderer.RenderMarkdown(session, "farnsworth");

        Assert.Null(result);
    }

    [Fact]
    public void RenderMarkdown_RendersUserMessage_AsBlockquote()
    {
        var session = CreateSession(new SessionEntry
        {
            Role = MessageRole.User,
            Content = "Hello world",
            Timestamp = new DateTimeOffset(2026, 6, 12, 10, 30, 0, TimeSpan.Zero)
        });

        var result = SessionTranscriptRenderer.RenderMarkdown(session, "farnsworth");

        Assert.NotNull(result);
        Assert.Contains("> Hello world", result);
        Assert.Contains("User [10:30:00]", result);
    }

    [Fact]
    public void RenderMarkdown_RendersAssistantMessage_AsPlainText()
    {
        var session = CreateSession(new SessionEntry
        {
            Role = MessageRole.Assistant,
            Content = "Good news, everyone!",
            Timestamp = new DateTimeOffset(2026, 6, 12, 10, 31, 0, TimeSpan.Zero)
        });

        var result = SessionTranscriptRenderer.RenderMarkdown(session, "farnsworth");

        Assert.NotNull(result);
        Assert.Contains("Good news, everyone!", result);
        Assert.Contains("Assistant [10:31:00]", result);
    }

    [Fact]
    public void RenderMarkdown_RendersToolCall_InCodeBlock()
    {
        var session = CreateSession(new SessionEntry
        {
            Role = MessageRole.Tool,
            Content = "",
            ToolName = "read",
            ToolArgs = "{\"path\":\"test.md\"}",
            Timestamp = new DateTimeOffset(2026, 6, 12, 10, 32, 0, TimeSpan.Zero)
        });

        var result = SessionTranscriptRenderer.RenderMarkdown(session, "farnsworth");

        Assert.NotNull(result);
        Assert.Contains("Tool Call: `read`", result);
        Assert.Contains("{\"path\":\"test.md\"}", result);
    }

    [Fact]
    public void RenderMarkdown_RendersToolResult_InCodeBlock()
    {
        var session = CreateSession(new SessionEntry
        {
            Role = MessageRole.Tool,
            Content = "file content here",
            ToolName = "read",
            Timestamp = new DateTimeOffset(2026, 6, 12, 10, 33, 0, TimeSpan.Zero)
        });

        var result = SessionTranscriptRenderer.RenderMarkdown(session, "farnsworth");

        Assert.NotNull(result);
        Assert.Contains("Tool Result: `read`", result);
        Assert.Contains("file content here", result);
    }

    [Fact]
    public void RenderMarkdown_RendersToolError_WithErrorLabel()
    {
        var session = CreateSession(new SessionEntry
        {
            Role = MessageRole.Tool,
            Content = "Permission denied",
            ToolName = "write",
            ToolIsError = true,
            Timestamp = new DateTimeOffset(2026, 6, 12, 10, 34, 0, TimeSpan.Zero)
        });

        var result = SessionTranscriptRenderer.RenderMarkdown(session, "farnsworth");

        Assert.NotNull(result);
        Assert.Contains("Tool Error: `write`", result);
    }

    [Fact]
    public void RenderMarkdown_SkipsCrashSentinels()
    {
        var session = CreateSession(
            new SessionEntry { Role = MessageRole.User, Content = "sentinel", IsCrashSentinel = true },
            new SessionEntry { Role = MessageRole.User, Content = "real message" }
        );

        var result = SessionTranscriptRenderer.RenderMarkdown(session, "farnsworth");

        Assert.NotNull(result);
        Assert.DoesNotContain("sentinel", result);
        Assert.Contains("real message", result);
    }

    [Fact]
    public void RenderMarkdown_IncludesMetadataHeader()
    {
        var session = CreateSession(new SessionEntry
        {
            Role = MessageRole.User,
            Content = "test"
        });

        var result = SessionTranscriptRenderer.RenderMarkdown(session, "farnsworth");

        Assert.NotNull(result);
        Assert.Contains("**Agent:** `farnsworth`", result);
        Assert.Contains("# Session Transcript", result);
    }

    private static GatewaySession CreateSession(params SessionEntry[] entries)
    {
        var session = new Session
        {
            SessionId = SessionId.From("test-session-1"),
            ConversationId = ConversationId.From("c_test"),
            CreatedAt = new DateTimeOffset(2026, 6, 12, 10, 0, 0, TimeSpan.Zero),
            Status = SessionStatus.Active,
        };
        session.History.AddRange(entries);
        return new GatewaySession(session);
    }
}
