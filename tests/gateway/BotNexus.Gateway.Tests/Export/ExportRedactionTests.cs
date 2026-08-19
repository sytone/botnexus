using AngleSharp.Html.Parser;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Api.Export;
using BotNexus.Gateway.Sessions;

namespace BotNexus.Gateway.Tests.Export;

/// <summary>
/// Adversarial secret-redaction tests for both export renderers (issue #3278, acceptance
/// criterion 5): message content, tool arguments, tool results, conversation instructions and the
/// metadata header fields are each seeded with a real credential shape and asserted scrubbed.
/// </summary>
/// <remarks>
/// Each case also asserts the secret IS present when redaction is switched off. Without that half,
/// a renderer that silently dropped the field entirely - or a test fixture whose secret never
/// reached the output in the first place - would pass vacuously while proving nothing about the
/// redactor.
/// </remarks>
public sealed class ExportRedactionTests
{
    private const string Secret = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";

    public static TheoryData<string, ExportDocument> RedactionCases() => new()
    {
        { "message content", WithEntry(new ConversationHistoryEntry
            { Kind = "message", SessionId = "s-1", Role = "user", Content = $"my token is {Secret}", Timestamp = Ts(0) }) },
        { "tool arguments", WithEntry(new ConversationHistoryEntry
            { Kind = "message", SessionId = "s-1", Role = "tool", ToolName = "curl", ToolArgs = $"{{\"token\":\"{Secret}\"}}", Timestamp = Ts(0) }) },
        { "tool result", WithEntry(new ConversationHistoryEntry
            { Kind = "message", SessionId = "s-1", Role = "tool", ToolName = "curl", Content = $"response contained {Secret}", Timestamp = Ts(0) }) },
        { "compaction summary", WithEntry(new ConversationHistoryEntry
            { Kind = "compaction", SessionId = "s-1", Content = $"user shared {Secret}", Reason = "compaction", Timestamp = Ts(0) }) },
        { "conversation instructions", Base() with { Instructions = $"Always send {Secret}" } },
        { "conversation title", Base() with { Title = $"debugging {Secret}" } },
        { "conversation purpose", Base() with { Purpose = $"rotate {Secret}" } }
    };

    [Theory]
    [MemberData(nameof(RedactionCases))]
    public void Markdown_RedactsSecret_AndWouldOtherwiseLeakIt(string label, ExportDocument document)
    {
        var unredacted = ExportMarkdownRenderer.Render(document, redactSecrets: false);
        unredacted.ShouldContain(Secret, customMessage: $"fixture for '{label}' never reached the markdown output");

        var redacted = ExportMarkdownRenderer.Render(document, redactSecrets: true);

        redacted.ShouldNotContain(Secret, customMessage: $"'{label}' leaked a credential into the markdown export");
        redacted.ShouldContain(TranscriptSecretRedactor.Placeholder);
    }

    [Theory]
    [MemberData(nameof(RedactionCases))]
    public void Html_RedactsSecret_AndWouldOtherwiseLeakIt(string label, ExportDocument document)
    {
        var unredacted = ExportHtmlRenderer.Render(document, redactSecrets: false);
        unredacted.ShouldContain(Secret, customMessage: $"fixture for '{label}' never reached the html output");

        var redacted = ExportHtmlRenderer.Render(document, redactSecrets: true);

        redacted.ShouldNotContain(Secret, customMessage: $"'{label}' leaked a credential into the html export");

        // Also assert over the parsed text content, so a secret hidden in an attribute or in the
        // document title - not just the body prose - is caught.
        var parsed = new HtmlParser().ParseDocument(redacted);
        parsed.DocumentElement.TextContent.ShouldNotContain(Secret);
        foreach (var element in parsed.All)
        {
            foreach (var attribute in element.Attributes)
                attribute.Value.ShouldNotContain(Secret);
        }
    }

    [Fact]
    public void Renderers_DefaultToRedactionEnabled()
    {
        // AC5: "defaults to enabled on every export route". The default argument is the mechanism,
        // so pin it directly - a future edit flipping it would otherwise only be caught by an
        // end-to-end route test.
        var document = WithEntry(new ConversationHistoryEntry
        {
            Kind = "message",
            SessionId = "s-1",
            Role = "user",
            Content = $"token {Secret}",
            Timestamp = Ts(0)
        });

        ExportMarkdownRenderer.Render(document).ShouldNotContain(Secret);
        ExportHtmlRenderer.Render(document).ShouldNotContain(Secret);
    }

    [Fact]
    public void Renderers_RedactMultipleDistinctCredentialShapes()
    {
        var document = WithEntry(new ConversationHistoryEntry
        {
            Kind = "message",
            SessionId = "s-1",
            Role = "user",
            Content = "gh=ghp_abcdefghijklmnopqrstuvwxyz0123456789 "
                    + "openai=sk-abcdefghijklmnopqrstuvwxyz0123456789 "
                    + "aws=AKIAIOSFODNN7EXAMPLE "
                    + "auth=Bearer abcdefghijklmnopqrstuvwxyz",
            Timestamp = Ts(0)
        });

        var markdown = ExportMarkdownRenderer.Render(document);
        var html = ExportHtmlRenderer.Render(document);

        foreach (var shape in new[]
        {
            "ghp_abcdefghijklmnopqrstuvwxyz0123456789",
            "sk-abcdefghijklmnopqrstuvwxyz0123456789",
            "AKIAIOSFODNN7EXAMPLE"
        })
        {
            markdown.ShouldNotContain(shape);
            html.ShouldNotContain(shape);
        }
    }

    private static DateTimeOffset Ts(int minutes)
        => new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

    private static ExportDocument Base() => new()
    {
        Scope = ExportScope.Conversation,
        ConversationId = "c_redact",
        Title = "Redaction fixture",
        Status = "Active",
        AgentId = "quill",
        CreatedAt = Ts(0),
        UpdatedAt = Ts(1),
        Sessions = [new ExportSessionInfo("s-1", "quill", Ts(0), Ts(1), "Active", 1)],
        Entries =
        [
            new ConversationHistoryEntry { Kind = "message", SessionId = "s-1", Role = "user", Content = "ordinary", Timestamp = Ts(0) }
        ],
        MessageCount = 1,
        GeneratedAt = Ts(2)
    };

    private static ExportDocument WithEntry(ConversationHistoryEntry entry)
        => Base() with { Entries = [entry] };
}
