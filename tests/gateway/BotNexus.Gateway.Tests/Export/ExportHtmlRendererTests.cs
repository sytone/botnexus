using AngleSharp.Html.Parser;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Api.Export;

namespace BotNexus.Gateway.Tests.Export;

/// <summary>
/// Tests for <see cref="ExportHtmlRenderer"/> (issue #3278, acceptance criterion 3).
/// </summary>
/// <remarks>
/// The inertness assertions PARSE the produced document with AngleSharp and walk the resulting DOM.
/// This is deliberate and is what AC3 requires: a string match such as
/// <c>output.ShouldNotContain("&lt;script")</c> passes on a document containing
/// <c>&lt;SCRIPT&gt;</c>, <c>&lt;script&#x20;&gt;</c>, or a script element assembled by the browser
/// from markup the matcher did not anticipate. Asking a real parser "does this document contain a
/// script element?" asserts the property that actually matters.
/// </remarks>
public sealed class ExportHtmlRendererTests
{
    private static readonly HtmlParser Parser = new();

    [Fact]
    public void Render_ProducesNoScriptElement()
    {
        var document = Parse(ExportHtmlRenderer.Render(SampleDocument()));

        document.QuerySelectorAll("script").ShouldBeEmpty();
    }

    [Fact]
    public void Render_ProducesNoRemoteAssetReference()
    {
        // Self-containment: opening the file offline, or on a machine with no network, must render
        // identically and must not emit a single outbound request. Any element that can fetch is
        // enumerated from the DOM rather than grepped for.
        var document = Parse(ExportHtmlRenderer.Render(SampleDocument()));

        document.QuerySelectorAll("[src]").ShouldBeEmpty();
        document.QuerySelectorAll("link").ShouldBeEmpty();
        document.QuerySelectorAll("iframe, object, embed, video, audio, img, source, track")
            .ShouldBeEmpty();

        // No element may reference an absolute URL through any attribute - that covers href,
        // background, poster, srcset and any attribute a future edit might add.
        foreach (var element in document.All)
        {
            foreach (var attribute in element.Attributes)
            {
                attribute.Value.ShouldNotContain("http://", Case.Insensitive);
                attribute.Value.ShouldNotContain("https://", Case.Insensitive);
                attribute.Value.ShouldNotContain("//", Case.Insensitive);
            }
        }

        // The single inline stylesheet must not @import or url() anything either.
        var style = document.QuerySelector("style")!.TextContent;
        style.ShouldNotContain("@import");
        style.ShouldNotContain("url(");
    }

    [Fact]
    public void Render_MessageContainingMarkup_IsEscapedIntoTextNotElements()
    {
        // The adversarial case behind AC3: a transcript is untrusted content. A message body that
        // contains a script tag must survive the round trip as literal, visible text - so the
        // document still contains no script element, and the user can still read what was said.
        var hostile = "<script>alert('xss')</script><img src=\"https://evil.example/pixel.gif\">";
        var doc = SampleDocument() with
        {
            Entries =
            [
                new ConversationHistoryEntry
                {
                    Kind = "message",
                    SessionId = "s-1",
                    Role = "user",
                    Content = hostile,
                    Timestamp = Ts(0)
                }
            ]
        };

        var document = Parse(ExportHtmlRenderer.Render(doc));

        document.QuerySelectorAll("script").ShouldBeEmpty();
        document.QuerySelectorAll("img").ShouldBeEmpty();
        // The text is preserved verbatim as a text node.
        document.Body!.TextContent.ShouldContain("alert('xss')");
        document.Body.TextContent.ShouldContain("evil.example");
    }

    [Fact]
    public void Render_HostileConversationTitle_DoesNotEscapeTheTitleElement()
    {
        var doc = SampleDocument() with { Title = "</title><script>alert(1)</script>" };

        var document = Parse(ExportHtmlRenderer.Render(doc));

        document.QuerySelectorAll("script").ShouldBeEmpty();
    }

    [Fact]
    public void Render_EmptyConversation_StillProducesAWellFormedDocumentWithHeader()
    {
        // AC8 for the HTML renderer: an empty conversation renders a valid document that says so,
        // rather than an empty file or a crash.
        var doc = SampleDocument() with { Entries = [], MessageCount = 0, ToolCallCount = 0 };

        var document = Parse(ExportHtmlRenderer.Render(doc));

        document.QuerySelector("h1").ShouldNotBeNull();
        document.QuerySelector(".empty").ShouldNotBeNull();
        document.QuerySelectorAll("article").ShouldBeEmpty();
    }

    [Fact]
    public void Render_MultiSessionDocument_EmitsAVisibleBoundaryElement()
    {
        var doc = SampleDocument() with
        {
            Entries =
            [
                new ConversationHistoryEntry { Kind = "message", SessionId = "s-1", Role = "user", Content = "first", Timestamp = Ts(0) },
                new ConversationHistoryEntry { Kind = "boundary", SessionId = "s-1", Reason = "session_end", Timestamp = Ts(1) },
                new ConversationHistoryEntry { Kind = "message", SessionId = "s-2", Role = "user", Content = "second", Timestamp = Ts(2) }
            ]
        };

        var document = Parse(ExportHtmlRenderer.Render(doc));

        document.QuerySelectorAll("hr.boundary").Length.ShouldBe(1);
        document.Body!.TextContent.ShouldContain("Session boundary");
        document.Body.TextContent.ShouldContain("first");
        document.Body.TextContent.ShouldContain("second");
    }

    [Fact]
    public void Render_DeclaresUtf8AndSurvivesNonAsciiContent()
    {
        // AC7 (encoding). The charset declaration and the content must agree, or a transcript
        // containing CJK or emoji renders as mojibake in a browser.
        var doc = SampleDocument() with
        {
            Entries =
            [
                new ConversationHistoryEntry { Kind = "message", SessionId = "s-1", Role = "user", Content = "日本語 — café 🔬", Timestamp = Ts(0) }
            ]
        };

        var rendered = ExportHtmlRenderer.Render(doc);
        var document = Parse(rendered);

        document.QuerySelector("meta[charset]")!.GetAttribute("charset").ShouldBe("utf-8");
        document.Body!.TextContent.ShouldContain("日本語 — café 🔬");
    }

    private static AngleSharp.Dom.IDocument Parse(string html) => Parser.ParseDocument(html);

    private static DateTimeOffset Ts(int minutes)
        => new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

    private static ExportDocument SampleDocument() => new()
    {
        Scope = ExportScope.Conversation,
        ConversationId = "c_sample",
        Title = "Sample conversation",
        Purpose = "A purpose",
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
