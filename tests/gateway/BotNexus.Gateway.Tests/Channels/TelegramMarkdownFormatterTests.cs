using BotNexus.Extensions.Channels.Telegram;

namespace BotNexus.Gateway.Tests.Channels;

public sealed class TelegramMarkdownFormatterTests
{
    // ─── EscapeMarkdownV2 ────────────────────────────────────────────────────

    [Fact]
    public void EscapeMarkdownV2_EmptyString_ReturnsEmpty()
        => TelegramMarkdownFormatter.EscapeMarkdownV2("").ShouldBeEmpty();

    [Fact]
    public void EscapeMarkdownV2_NullString_ReturnsEmpty()
        => TelegramMarkdownFormatter.EscapeMarkdownV2(null).ShouldBeEmpty();

    [Fact]
    public void EscapeMarkdownV2_PlainText_ReturnsUnchanged()
        => TelegramMarkdownFormatter.EscapeMarkdownV2("hello world").ShouldBe("hello world");

    [Theory]
    [InlineData("_", "\\_")]
    [InlineData("*", "\\*")]
    [InlineData("[", "\\[")]
    [InlineData("]", "\\]")]
    [InlineData("(", "\\(")]
    [InlineData(")", "\\)")]
    [InlineData("~", "\\~")]
    [InlineData("`", "\\`")]
    [InlineData(">", "\\>")]
    [InlineData("#", "\\#")]
    [InlineData("+", "\\+")]
    [InlineData("-", "\\-")]
    [InlineData("=", "\\=")]
    [InlineData("|", "\\|")]
    [InlineData("{", "\\{")]
    [InlineData("}", "\\}")]
    [InlineData(".", "\\.")]
    [InlineData("!", "\\!")]
    [InlineData("\\", "\\\\")]
    public void EscapeMarkdownV2_SpecialChar_IsEscaped(string input, string expected)
        => TelegramMarkdownFormatter.EscapeMarkdownV2(input).ShouldBe(expected);

    [Fact]
    public void EscapeMarkdownV2_ToolNameWithUnderscore_EscapesUnderscore()
        => TelegramMarkdownFormatter.EscapeMarkdownV2("memory_save").ShouldBe("memory\\_save");

    // ─── Convert: empty / null ───────────────────────────────────────────────

    [Fact]
    public void Convert_EmptyString_ReturnsEmpty()
        => TelegramMarkdownFormatter.Convert("").ShouldBeEmpty();

    [Fact]
    public void Convert_NullString_ReturnsEmpty()
        => TelegramMarkdownFormatter.Convert(null).ShouldBeEmpty();

    // ─── Convert: plain text ─────────────────────────────────────────────────

    [Fact]
    public void Convert_PlainText_EscapesSpecialChars()
    {
        var result = TelegramMarkdownFormatter.Convert("Hello world. It costs $1.00!");
        result.ShouldBe("Hello world\\. It costs $1\\.00\\!");
    }

    [Fact]
    public void Convert_PlainTextNoSpecials_ReturnsUnchanged()
        => TelegramMarkdownFormatter.Convert("hello world").ShouldBe("hello world");

    // ─── Convert: bold ───────────────────────────────────────────────────────

    [Fact]
    public void Convert_Bold_ConvertsTelegramBold()
        => TelegramMarkdownFormatter.Convert("**bold text**").ShouldBe("*bold text*");

    [Fact]
    public void Convert_BoldMixedWithPlainText_ConvertsCorrectly()
        => TelegramMarkdownFormatter.Convert("Hello **world** foo").ShouldBe("Hello *world* foo");

    [Fact]
    public void Convert_UnclosedBold_EscapesLiteral()
        => TelegramMarkdownFormatter.Convert("**unclosed").ShouldBe("\\*\\*unclosed");

    // ─── Convert: italic ─────────────────────────────────────────────────────

    [Fact]
    public void Convert_ItalicStar_ConvertsTelegramItalic()
        => TelegramMarkdownFormatter.Convert("*italic text*").ShouldBe("_italic text_");

    [Fact]
    public void Convert_ItalicUnderscore_PreservedAsTelegramItalic()
        => TelegramMarkdownFormatter.Convert("_italic text_").ShouldBe("_italic text_");

    [Fact]
    public void Convert_UnclosedItalicStar_EscapesLiteral()
        => TelegramMarkdownFormatter.Convert("*unclosed").ShouldBe("\\*unclosed");

    [Fact]
    public void Convert_UnclosedItalicUnderscore_EscapesLiteral()
        => TelegramMarkdownFormatter.Convert("_unclosed").ShouldBe("\\_unclosed");

    // ─── Convert: bold italic ────────────────────────────────────────────────

    [Fact]
    public void Convert_BoldItalic_ConvertsTelegramBoldItalic()
        => TelegramMarkdownFormatter.Convert("***bold italic***").ShouldBe("*_bold italic_*");

    // ─── Convert: strikethrough ──────────────────────────────────────────────

    [Fact]
    public void Convert_Strikethrough_ConvertsSingleTilde()
        => TelegramMarkdownFormatter.Convert("~~struck~~").ShouldBe("~struck~");

    [Fact]
    public void Convert_UnclosedStrikethrough_EscapesLiteral()
        => TelegramMarkdownFormatter.Convert("~~unclosed").ShouldBe("\\~\\~unclosed");

    // ─── Convert: inline code ────────────────────────────────────────────────

    [Fact]
    public void Convert_InlineCode_PreservesBackticks()
        => TelegramMarkdownFormatter.Convert("`some code`").ShouldBe("`some code`");

    [Fact]
    public void Convert_InlineCode_EscapesBackslashInsideCode()
        // Inside inline code, only \ and ` need escaping. \ → \\
        => TelegramMarkdownFormatter.Convert("`code with \\ backslash`").ShouldBe("`code with \\\\ backslash`");

    [Fact]
    public void Convert_UnclosedInlineCode_EscapesOpeningBacktick()
        => TelegramMarkdownFormatter.Convert("`unclosed").ShouldBe("\\`unclosed");

    // ─── Convert: code block ─────────────────────────────────────────────────

    [Fact]
    public void Convert_CodeBlock_PreservesBlock()
    {
        var input = "```csharp\nvar x = 1;\n```";
        var result = TelegramMarkdownFormatter.Convert(input);
        result.ShouldBe("```csharp\nvar x = 1;\n```");
    }

    [Fact]
    public void Convert_CodeBlockNoLang_PreservesBlock()
    {
        var input = "```\nhello\n```";
        var result = TelegramMarkdownFormatter.Convert(input);
        result.ShouldBe("```\nhello\n```");
    }

    [Fact]
    public void Convert_UnclosedCodeBlock_EscapesOpeningFence()
    {
        var result = TelegramMarkdownFormatter.Convert("```no closing fence");
        result.ShouldBe("\\`\\`\\`no closing fence");
    }

    // ─── Convert: links ──────────────────────────────────────────────────────

    [Fact]
    public void Convert_Link_PreservesLinkFormat()
        // Per Telegram spec, inside URL only ) and \ need escaping — . is NOT escaped in URLs.
        => TelegramMarkdownFormatter.Convert("[GitHub](https://github.com)").ShouldBe("[GitHub](https://github.com)");

    [Fact]
    public void Convert_NotALink_EscapesBracket()
        => TelegramMarkdownFormatter.Convert("[not a link]").ShouldBe("\\[not a link\\]");

    // ─── Convert: headings ───────────────────────────────────────────────────

    [Fact]
    public void Convert_H1Heading_ConvertsToBold()
        => TelegramMarkdownFormatter.Convert("# My Heading").ShouldBe("*My Heading*");

    [Fact]
    public void Convert_H2Heading_ConvertsToBold()
        => TelegramMarkdownFormatter.Convert("## Section").ShouldBe("*Section*");

    [Fact]
    public void Convert_H3Heading_ConvertsToBold()
        => TelegramMarkdownFormatter.Convert("### Sub").ShouldBe("*Sub*");

    // ─── Convert: tool label lines (structural content) ─────────────────────

    [Fact]
    public void Convert_ToolLabel_EscapesLiteralBrackets()
    {
        // Tool labels like [memory_save] started must render as literal text,
        // not as links or italic markers.
        var result = TelegramMarkdownFormatter.Convert("[memory_save] started");
        result.ShouldBe("\\[memory\\_save\\] started");
    }

    [Fact]
    public void Convert_ToolLabel_WithUnderscore_EscapesUnderscore()
    {
        var result = TelegramMarkdownFormatter.Convert("[write_file] completed");
        result.ShouldBe("\\[write\\_file\\] completed");
    }

    // ─── Convert: mixed content ──────────────────────────────────────────────

    [Fact]
    public void Convert_MixedContent_RendersCorrectly()
    {
        var input = "Here is **bold** and `code` and a [link](https://example.com).";
        var result = TelegramMarkdownFormatter.Convert(input);
        // Per Telegram spec, . is not escaped inside link URLs — only ) and \ are.
        result.ShouldBe("Here is *bold* and `code` and a [link](https://example.com)\\.");
    }

    [Fact]
    public void Convert_MultilineContent_ProcessesEachLine()
    {
        var input = "# Title\n\nSome **bold** text.\n\n`code here`";
        var result = TelegramMarkdownFormatter.Convert(input);
        result.ShouldBe("*Title*\n\nSome *bold* text\\.\n\n`code here`");
    }
}
