namespace BotNexus.Extensions.WebTools.Tests;

/// <summary>
/// Robustness tests for <see cref="HtmlToText.Convert"/> covering malformed input:
/// unterminated raw-text openers (<c>&lt;script</c>/<c>&lt;style</c>/<c>&lt;noscript</c>)
/// and dangling tag tails with no closing <c>&gt;</c> at end of input (#1825), plus
/// regression coverage for the existing well-formed conversion behaviour.
/// </summary>
[Trait("Category", "Unit")]
public class HtmlToTextTests
{
    [Fact]
    public void Convert_UnterminatedScript_DoesNotLeakContent()
    {
        const string html = "<p>Visible</p><script>var secret = 'do not leak';";

        var result = HtmlToText.Convert(html);

        result.ShouldContain("Visible");
        result.ShouldNotContain("secret");
        result.ShouldNotContain("do not leak");
    }

    [Fact]
    public void Convert_UnterminatedStyle_DoesNotLeakContent()
    {
        const string html = "<p>Visible</p><style>body { color: red; }";

        var result = HtmlToText.Convert(html);

        result.ShouldContain("Visible");
        result.ShouldNotContain("color");
        result.ShouldNotContain("red");
    }

    [Fact]
    public void Convert_UnterminatedNoscript_DoesNotLeakContent()
    {
        const string html = "<p>Visible</p><noscript>enable javascript please";

        var result = HtmlToText.Convert(html);

        result.ShouldContain("Visible");
        result.ShouldNotContain("enable javascript");
    }

    [Fact]
    public void Convert_UnterminatedScriptOpenerWithAttributes_DoesNotLeakContent()
    {
        const string html = "<p>Visible</p><script type=\"text/javascript\" src=\"x.js\"";

        var result = HtmlToText.Convert(html);

        result.ShouldContain("Visible");
        result.ShouldNotContain("javascript");
        result.ShouldNotContain("x.js");
    }

    [Fact]
    public void Convert_DanglingTagAtEndOfInput_IsDropped()
    {
        const string html = "<p>Hello</p><div class=\"trailing";

        var result = HtmlToText.Convert(html);

        result.ShouldContain("Hello");
        result.ShouldNotContain("trailing");
        result.ShouldNotContain("<div");
    }

    [Fact]
    public void Convert_DanglingBareTagAtEndOfInput_IsDropped()
    {
        const string html = "Hello world<span";

        var result = HtmlToText.Convert(html);

        result.ShouldBe("Hello world");
    }

    // ---- Regression cases: well-formed behaviour must be preserved ----

    [Fact]
    public void Convert_WellFormedScript_IsStripped()
    {
        const string html = "<p>Before</p><script>alert('x');</script><p>After</p>";

        var result = HtmlToText.Convert(html);

        result.ShouldContain("Before");
        result.ShouldContain("After");
        result.ShouldNotContain("alert");
    }

    [Fact]
    public void Convert_WellFormedStyle_IsStripped()
    {
        const string html = "<p>Before</p><style>body{color:red}</style><p>After</p>";

        var result = HtmlToText.Convert(html);

        result.ShouldContain("Before");
        result.ShouldContain("After");
        result.ShouldNotContain("color");
    }

    [Fact]
    public void Convert_BlockElements_ProduceLineBreaks()
    {
        const string html = "<p>One</p><p>Two</p>";

        var result = HtmlToText.Convert(html);

        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.ShouldContain("One");
        lines.ShouldContain("Two");
    }

    [Fact]
    public void Convert_Entities_AreDecoded()
    {
        const string html = "<p>Fish &amp; Chips &lt;yum&gt;</p>";

        var result = HtmlToText.Convert(html);

        result.ShouldContain("Fish & Chips <yum>");
    }

    [Fact]
    public void Convert_Link_IsMarkdown()
    {
        const string html = "<a href=\"https://example.com\">Example</a>";

        var result = HtmlToText.Convert(html);

        result.ShouldContain("[Example](https://example.com)");
    }

    [Fact]
    public void Convert_CollapsesWhitespace()
    {
        const string html = "<p>lots    of     spaces</p>";

        var result = HtmlToText.Convert(html);

        result.ShouldBe("lots of spaces");
    }

    [Fact]
    public void Convert_NullOrWhitespace_ReturnsEmpty()
    {
        HtmlToText.Convert(null!).ShouldBe(string.Empty);
        HtmlToText.Convert("   ").ShouldBe(string.Empty);
    }
}
