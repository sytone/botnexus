using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Api.Export;

namespace BotNexus.Gateway.Tests.Export;

/// <summary>
/// Tests for <see cref="ExportMarkdownRenderer"/> (issue #3278, acceptance criterion 2) and for the
/// download filename contract (acceptance criterion 7).
/// </summary>
public sealed class ExportMarkdownRendererTests
{
    [Fact]
    public void Render_EmitsSummaryHeaderFields()
    {
        var rendered = ExportMarkdownRenderer.Render(SampleDocument());

        rendered.ShouldStartWith("# Conversation Transcript");
        rendered.ShouldContain("- **Title:** Sample conversation");
        rendered.ShouldContain("- **Conversation ID:** `c_sample`");
        rendered.ShouldContain("- **Agent:** `quill`");
        rendered.ShouldContain("- **Status:** Active");
        rendered.ShouldContain("- **Messages:** 1");
        rendered.ShouldContain("## Instructions");
        rendered.ShouldContain("Be concise.");
        rendered.ShouldContain("## Sessions");
        rendered.ShouldContain("`s-1`");
    }

    [Fact]
    public void Render_MultiSession_EmitsVisibleSessionBoundaryMarker()
    {
        // AC2: "with visible session boundary markers". A reader of the file must be able to see
        // where one session ended and the next began.
        var doc = SampleDocument() with
        {
            Entries =
            [
                new ConversationHistoryEntry { Kind = "message", SessionId = "s-1", Role = "user", Content = "first", Timestamp = Ts(0) },
                new ConversationHistoryEntry { Kind = "boundary", SessionId = "s-1", Reason = "session_end", Timestamp = Ts(1) },
                new ConversationHistoryEntry { Kind = "message", SessionId = "s-2", Role = "user", Content = "second", Timestamp = Ts(2) }
            ]
        };

        var rendered = ExportMarkdownRenderer.Render(doc);

        rendered.ShouldContain("Session boundary");
        rendered.ShouldContain("`s-1` ended");
        rendered.ShouldContain("first");
        rendered.ShouldContain("second");
    }

    [Fact]
    public void Render_RendersEachRoleWithTheSessionRendererConventions()
    {
        var doc = SampleDocument() with
        {
            Entries =
            [
                new ConversationHistoryEntry { Kind = "message", SessionId = "s-1", Role = "user", Content = "a question", Timestamp = Ts(0) },
                new ConversationHistoryEntry { Kind = "message", SessionId = "s-1", Role = "assistant", Content = "an answer", Timestamp = Ts(1) },
                new ConversationHistoryEntry { Kind = "message", SessionId = "s-1", Role = "tool", ToolName = "read", ToolArgs = "{\"path\":\"a\"}", Timestamp = Ts(2) },
                new ConversationHistoryEntry { Kind = "message", SessionId = "s-1", Role = "tool", ToolName = "read", Content = "file body", Timestamp = Ts(3) },
                new ConversationHistoryEntry { Kind = "message", SessionId = "s-1", Role = "tool", ToolName = "read", Content = "boom", ToolIsError = true, Timestamp = Ts(4) },
                new ConversationHistoryEntry { Kind = "compaction", SessionId = "s-1", Content = "the summary", Reason = "compaction", Timestamp = Ts(5) }
            ]
        };

        var rendered = ExportMarkdownRenderer.Render(doc);

        rendered.ShouldContain("## 🧑 User");
        rendered.ShouldContain("> a question");
        rendered.ShouldContain("## 🤖 Assistant");
        rendered.ShouldContain("### 🔧 Tool Call: `read`");
        rendered.ShouldContain("### 📋 Tool Result: `read`");
        rendered.ShouldContain("### 📋 Tool Error: `read`");
        rendered.ShouldContain("Compaction summary");
        rendered.ShouldContain("the summary");
    }

    [Fact]
    public void Render_EmptyConversation_StillRendersHeaderAndSaysSo()
    {
        // AC8 for the markdown renderer.
        var doc = SampleDocument() with { Entries = [], MessageCount = 0 };

        var rendered = ExportMarkdownRenderer.Render(doc);

        rendered.ShouldContain("# Conversation Transcript");
        rendered.ShouldContain("- **Title:** Sample conversation");
        rendered.ShouldContain("_This conversation has no messages._");
    }

    [Fact]
    public void Render_SessionScope_UsesSessionHeading()
    {
        var doc = SampleDocument() with { Scope = ExportScope.Session };

        ExportMarkdownRenderer.Render(doc).ShouldStartWith("# Session Transcript");
    }

    private static DateTimeOffset Ts(int minutes)
        => new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

    internal static ExportDocument SampleDocument() => new()
    {
        Scope = ExportScope.Conversation,
        ConversationId = "c_sample",
        Title = "Sample conversation",
        Status = "Active",
        AgentId = "quill",
        Instructions = "Be concise.",
        CreatedAt = Ts(0),
        UpdatedAt = Ts(5),
        Sessions = [new ExportSessionInfo("s-1", "quill", Ts(0), Ts(5), "Active", 1)],
        Entries =
        [
            new ConversationHistoryEntry { Kind = "message", SessionId = "s-1", Role = "user", Content = "hello", Timestamp = Ts(0) }
        ],
        MessageCount = 1,
        ToolCallCount = 0,
        GeneratedAt = Ts(6)
    };
}
