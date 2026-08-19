namespace BotNexus.Extensions.Channels.Matrix.Tests;

/// <summary>
/// Tests for <see cref="MatrixMessageFormatter"/>: Markdown to the Matrix HTML subset, and the
/// <c>m.replace</c> / <c>m.thread</c> content shapes.
/// </summary>
public sealed class MatrixMessageFormatterTests
{
    [Fact]
    public void BuildTextMessage_PlainText_OmitsFormattedBody()
    {
        var content = MatrixMessageFormatter.BuildTextMessage("just plain text");

        content.MsgType.ShouldBe("m.text");
        content.Body.ShouldBe("just plain text");
        content.Format.ShouldBeNull();
        content.FormattedBody.ShouldBeNull();
        content.RelatesTo.ShouldBeNull();
    }

    [Fact]
    public void BuildTextMessage_MultiLinePlainText_StillOmitsFormattedBody()
    {
        // Regression guard: ToHtml necessarily renders line breaks as <br/>, so a naive comparison
        // against the raw escaped text reported "has markup" for every multi-line plain message and
        // attached a redundant formatted_body to all of them.
        var content = MatrixMessageFormatter.BuildTextMessage("line one\nline two");

        content.Format.ShouldBeNull();
        content.FormattedBody.ShouldBeNull();
    }

    [Fact]
    public void BuildTextMessage_PlainTextWithAngleBrackets_OmitsFormattedBody()
    {
        // Escaping alone is not markup: the plain body already carries the literal characters, so a
        // formatted_body would add nothing.
        var content = MatrixMessageFormatter.BuildTextMessage("a < b && c > d");

        content.Format.ShouldBeNull();
        content.FormattedBody.ShouldBeNull();
    }

    [Fact]
    public void BuildTextMessage_Markdown_AttachesHtmlFormattedBody()
    {
        var content = MatrixMessageFormatter.BuildTextMessage("**bold** and `code`");

        content.Body.ShouldBe("**bold** and `code`");
        content.Format.ShouldBe(MatrixMessageFormatter.HtmlFormat);
        content.FormattedBody.ShouldNotBeNull();
        content.FormattedBody!.ShouldContain("<strong>bold</strong>");
        content.FormattedBody.ShouldContain("<code>code</code>");
    }

    [Fact]
    public void BuildTextMessage_WithThreadRoot_AttachesThreadRelation()
    {
        var content = MatrixMessageFormatter.BuildTextMessage("hi", threadRootEventId: "$root1");

        content.RelatesTo.ShouldNotBeNull();
        content.RelatesTo!.RelType.ShouldBe("m.thread");
        content.RelatesTo.EventId.ShouldBe("$root1");
    }

    [Fact]
    public void BuildTextMessage_WithReplacement_BuildsEditWithNewContentAndFallback()
    {
        var content = MatrixMessageFormatter.BuildTextMessage("updated text", replacesEventId: "$orig1");

        content.RelatesTo.ShouldNotBeNull();
        content.RelatesTo!.RelType.ShouldBe("m.replace");
        content.RelatesTo.EventId.ShouldBe("$orig1");

        // The top-level body is the "* text" fallback clients that cannot render edits display,
        // while m.new_content carries the actual replacement.
        content.Body.ShouldBe("* updated text");
        content.NewContent.ShouldNotBeNull();
        content.NewContent!.Body.ShouldBe("updated text");
    }

    [Fact]
    public void BuildTextMessage_ReplacementTakesPrecedenceOverThread()
    {
        // An edit already targets an event; emitting an m.thread relation as well would make the
        // relation ambiguous, and Matrix permits only one rel_type per content.
        var content = MatrixMessageFormatter.BuildTextMessage(
            "text",
            threadRootEventId: "$root1",
            replacesEventId: "$orig1");

        content.RelatesTo!.RelType.ShouldBe("m.replace");
    }

    [Fact]
    public void BuildTextMessage_NullMarkdown_ProducesEmptyBody()
    {
        var content = MatrixMessageFormatter.BuildTextMessage(null);

        content.Body.ShouldBe(string.Empty);
        content.FormattedBody.ShouldBeNull();
    }

    [Fact]
    public void ToHtml_EscapesLiteralMarkupBeforeEmittingTags()
    {
        var html = MatrixMessageFormatter.ToHtml("<script>alert('x')</script>");

        html.ShouldNotContain("<script>");
        html.ShouldContain("&lt;script&gt;");
    }

    [Fact]
    public void ToHtml_EscapesInsideCodeSpan()
    {
        var html = MatrixMessageFormatter.ToHtml("run `<b>x</b>` now");

        html.ShouldContain("<code>&lt;b&gt;x&lt;/b&gt;</code>");
        html.ShouldNotContain("<code><b>");
    }

    [Fact]
    public void ToHtml_HeadingBecomesHeadingTag()
    {
        MatrixMessageFormatter.ToHtml("## Title").ShouldBe("<h2>Title</h2>");
    }

    [Fact]
    public void ToHtml_ListItemBecomesListItemTag()
    {
        MatrixMessageFormatter.ToHtml("- first").ShouldBe("<li>first</li>");
    }

    [Fact]
    public void ToHtml_FencedBlockBecomesPreCode()
    {
        var html = MatrixMessageFormatter.ToHtml("```\nvar x = 1;\n```");

        html.ShouldBe("<pre><code>var x = 1;</code></pre>");
    }

    [Fact]
    public void ToHtml_UnterminatedFence_StillEmitsBufferedContent()
    {
        // Dropping the tail would silently lose the end of an agent's message.
        var html = MatrixMessageFormatter.ToHtml("```\nvar x = 1;");

        html.ShouldContain("var x = 1;");
    }

    [Fact]
    public void ToHtml_EmphasisMarkersInsideCodeSpanAreNotInterpreted()
    {
        var html = MatrixMessageFormatter.ToHtml("`a*b*c`");

        html.ShouldContain("<code>a*b*c</code>");
        html.ShouldNotContain("<em>");
    }

    [Fact]
    public void ToHtml_SafeLinkBecomesAnchor()
    {
        var html = MatrixMessageFormatter.ToHtml("[docs](https://example.com/a)");

        html.ShouldContain("<a href=\"https://example.com/a\">docs</a>");
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("vbscript:msgbox(1)")]
    public void ToHtml_UnsafeLinkSchemeIsDroppedButLabelSurvives(string url)
    {
        var html = MatrixMessageFormatter.ToHtml($"[click]({url})");

        html.ShouldNotContain("href");
        html.ShouldContain("click");
    }

    [Fact]
    public void ToHtml_UnclosedEmphasisMarkerIsLiteral()
    {
        // A lone asterisk is ordinary text; emitting an unclosed <em> would corrupt everything
        // after it in the reader's client.
        var html = MatrixMessageFormatter.ToHtml("2 * 3 = 6");

        html.ShouldNotContain("<em>");
        html.ShouldContain("2 * 3 = 6");
    }

    [Fact]
    public void ToHtml_EmptyInput_ProducesEmptyOutput()
    {
        MatrixMessageFormatter.ToHtml(string.Empty).ShouldBe(string.Empty);
        MatrixMessageFormatter.ToHtml(null).ShouldBe(string.Empty);
    }
}
