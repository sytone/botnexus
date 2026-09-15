using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Api.Export;

namespace BotNexus.Gateway.Tests.Export;

public sealed class ExportContentOptionsTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Apply_DefaultsKeepToolsAndRedactionButOmitSensitiveContext()
    {
        var document = Document();

        var filtered = ExportContentFilter.Apply(document, ExportContentOptions.Default);

        filtered.Instructions.ShouldBeNull();
        filtered.Entries.Select(e => e.Role).ShouldBe(["user", "assistant", "tool", "tool"]);
        filtered.Entries.Single(e => e.Role == "assistant").ThinkingContent.ShouldBeNull();
        filtered.ToolCallCount.ShouldBe(1);
        ExportContentOptions.Default.RedactSecrets.ShouldBeTrue();
    }

    [Fact]
    public void Apply_ExplicitOptionsIncludeSensitiveContextAndCanOmitTools()
    {
        var options = new ExportContentOptions(
            IncludeTools: false,
            IncludeThinking: true,
            IncludeSystemMessages: true,
            RedactSecrets: false);

        var filtered = ExportContentFilter.Apply(Document(), options);

        filtered.Instructions.ShouldBe("private instruction");
        filtered.Entries.Select(e => e.Role).ShouldBe(["system", "user", "assistant"]);
        filtered.Entries.Single(e => e.Role == "assistant").ThinkingContent.ShouldBe("private reasoning");
        filtered.ToolCallCount.ShouldBe(0);
        options.RedactSecrets.ShouldBeFalse();
        ExportMarkdownRenderer.Render(filtered, options.RedactSecrets).ShouldContain("private reasoning");
        ExportHtmlRenderer.Render(filtered, options.RedactSecrets).ShouldContain("private reasoning");
    }

    private static ExportDocument Document() => new()
    {
        Scope = ExportScope.Conversation,
        GeneratedAt = Timestamp,
        Instructions = "private instruction",
        ToolCallCount = 1,
        MessageCount = 5,
        Entries =
        [
            Entry("system", "system prompt"),
            Entry("user", "question"),
            Entry("assistant", "answer", thinking: "private reasoning"),
            Entry("tool", "", toolName: "read", toolArgs: "{}"),
            Entry("tool", "result", toolName: "read")
        ]
    };

    private static ConversationHistoryEntry Entry(
        string role,
        string content,
        string? thinking = null,
        string? toolName = null,
        string? toolArgs = null) => new()
        {
            Kind = "message",
            EntryId = Guid.NewGuid().ToString("N"),
            SessionId = "s-1",
            Timestamp = Timestamp,
            Role = role,
            Content = content,
            ThinkingContent = thinking,
            ToolName = toolName,
            ToolArgs = toolArgs
        };
}
