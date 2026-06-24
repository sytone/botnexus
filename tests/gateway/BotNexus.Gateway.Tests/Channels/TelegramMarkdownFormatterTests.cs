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
        // CommonMark parses *** as italic wrapping bold; Telegram renders _*x*_ identically to
        // *_x_*. The Markdig-based formatter emits the parsed nesting (italic-outer) — both are
        // valid MarkdownV2 and display the same bold-italic text.
        => TelegramMarkdownFormatter.Convert("***bold italic***").ShouldBe("_*bold italic*_");

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
    public void Convert_UnclosedCodeBlock_RendersAsOpenCodeBlock()
    {
        // CommonMark: an unterminated fenced code block runs to end-of-input and IS a valid
        // code block. Telegram renders it as a code block (the previous scanner wrongly escaped
        // the fence into literal backticks). The trailing text on the fence line becomes the
        // info string.
        var result = TelegramMarkdownFormatter.Convert("```no closing fence");
        result.ShouldStartWith("```");
        result.ShouldEndWith("```");
        result.ShouldNotContain("\\`");
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
        // Markdig separates distinct blocks (heading / paragraph / code) with a blank line.
        result.ShouldBe("*Title*\n\nSome *bold* text\\.\n\n`code here`");
    }

    // ─── Convert: heading placement ──────────────────────────────────────────

    [Fact]
    public void Convert_HashMidLine_EscapesLiteralHash()
    {
        // A # that is not at the start of a line must not be treated as a heading.
        var result = TelegramMarkdownFormatter.Convert("Not a # heading");
        result.ShouldBe("Not a \\# heading");
    }

    [Fact]
    public void Convert_HeadingOnSecondLine_ConvertsToBold()
    {
        // A heading after a paragraph is a separate block; Markdig separates blocks with a
        // blank line, so the rendered output has an empty line between the two.
        var result = TelegramMarkdownFormatter.Convert("intro\n# Section");
        result.ShouldBe("intro\n\n*Section*");
    }

    // ─── Convert: multiple formatting spans ──────────────────────────────────

    [Fact]
    public void Convert_MultipleBoldSpans_BothConverted()
    {
        // Both bold spans in the same line must be individually converted.
        var result = TelegramMarkdownFormatter.Convert("**one** and **two**");
        result.ShouldBe("*one* and *two*");
    }

    [Fact]
    public void Convert_AdjacentBoldAndItalic_BothConverted()
    {
        // No space between bold and italic tokens — both must still be converted.
        var result = TelegramMarkdownFormatter.Convert("**bold**_italic_");
        result.ShouldBe("*bold*_italic_");
    }

    // ─── Convert: special chars inside formatting spans ──────────────────────

    [Fact]
    public void Convert_BoldWithDotInContent_DotIsEscaped()
    {
        // Special chars inside bold content must be escaped per MarkdownV2 rules.
        var result = TelegramMarkdownFormatter.Convert("**hello.world**");
        result.ShouldBe("*hello\\.world*");
    }

    [Fact]
    public void Convert_BoldWithUnderscoreInContent_UnderscoreIsEscaped()
    {
        // An underscore inside ** bold ** must not trigger italic processing.
        var result = TelegramMarkdownFormatter.Convert("**hello_world**");
        result.ShouldBe("*hello\\_world*");
    }

    [Fact]
    public void Convert_ItalicWithSpecialCharsInContent_CharsAreEscaped()
    {
        var result = TelegramMarkdownFormatter.Convert("_cost: $1.00_");
        result.ShouldBe("_cost: $1\\.00_");
    }

    // ─── Convert: code suppresses markdown processing ────────────────────────

    [Fact]
    public void Convert_FencedCodeBlock_PreservesInternalAsterisks()
    {
        // Inside a fenced code block, ** must NOT be converted to Telegram bold.
        // Only backtick and backslash need escaping inside code blocks.
        var input = "```\n**not bold**\n```";
        var result = TelegramMarkdownFormatter.Convert(input);
        result.ShouldBe("```\n**not bold**\n```");
    }

    [Fact]
    public void Convert_FencedCodeBlock_PreservesInternalUnderscores()
    {
        // Underscores inside a code block must not trigger italic processing.
        var input = "```python\nsome_variable = True\n```";
        var result = TelegramMarkdownFormatter.Convert(input);
        result.ShouldBe("```python\nsome_variable = True\n```");
    }

    [Fact]
    public void Convert_InlineCode_PreservesInternalAsterisks()
    {
        // Inside a backtick span, * must NOT be converted.
        var result = TelegramMarkdownFormatter.Convert("`*not italic*`");
        result.ShouldBe("`*not italic*`");
    }

    [Fact]
    public void Convert_InlineCode_PreservesInternalSpecialChars()
    {
        // Most MarkdownV2 special chars are NOT escaped inside inline code.
        var result = TelegramMarkdownFormatter.Convert("`x.y[0]`");
        result.ShouldBe("`x.y[0]`");
    }

    // ─── Convert: empty / degenerate formatting tokens ───────────────────────

    [Fact]
    public void Convert_EmptyBoldSpan_DoesNotThrow()
    {
        // Two consecutive ** pairs with nothing between them must not crash.
        var act = () => TelegramMarkdownFormatter.Convert("****");
        act.ShouldNotThrow();
    }

    [Fact]
    public void Convert_BoldWithNoClosingOnSameLine_EscapesAsterisks()
    {
        // An opening ** followed by content but no closing ** should be treated as
        // literal characters — not leave dangling MarkdownV2 syntax.
        var result = TelegramMarkdownFormatter.Convert("** not bold");
        result.ShouldNotContain("**");
    }

    // ─── Convert: real LLM output patterns ───────────────────────────────────

    [Fact]
    public void Convert_NumberedListItem_EscapesDot()
    {
        // Numbered list items like "1. Item" — the dot after a digit must be escaped
        // because "." is a MarkdownV2 special char.
        var result = TelegramMarkdownFormatter.Convert("1. First item\n2. Second item");
        result.ShouldBe("1\\. First item\n2\\. Second item");
    }

    [Fact]
    public void Convert_BulletListItem_RendersBulletGlyph()
    {
        // Unordered list items now render with a bullet glyph (\u2022) instead of an escaped
        // dash, so Telegram shows a proper list rather than literal "- " punctuation.
        var result = TelegramMarkdownFormatter.Convert("- item one\n- item two");
        result.ShouldBe("\u2022 item one\n\u2022 item two");
    }

    [Fact]
    public void Convert_LlmTypicalOutput_RendersCorrectly()
    {
        // Typical LLM output mixing bold headers, inline code, and plain text.
        var input = "**Summary**\n\nUse `git status` to check changes. Cost: $0.00.";
        var result = TelegramMarkdownFormatter.Convert(input);
        result.ShouldBe("*Summary*\n\nUse `git status` to check changes\\. Cost: $0\\.00\\.");
    }

    // ─── Convert: lists (Markdig-based) ──────────────────────────────────────

    [Fact]
    public void Convert_OrderedList_KeepsNumbersAndEscapesDots()
    {
        var result = TelegramMarkdownFormatter.Convert("1. First\n2. Second\n3. Third");
        result.ShouldBe("1\\. First\n2\\. Second\n3\\. Third");
    }

    [Fact]
    public void Convert_OrderedList_RespectsStartNumber()
    {
        var result = TelegramMarkdownFormatter.Convert("5. Five\n6. Six");
        result.ShouldBe("5\\. Five\n6\\. Six");
    }

    [Fact]
    public void Convert_UnorderedList_WithAsteriskMarker_RendersBullets()
    {
        // A '* ' list marker must NOT be mistaken for italic (the old scanner's failure mode).
        var result = TelegramMarkdownFormatter.Convert("* alpha\n* beta");
        result.ShouldBe("\u2022 alpha\n\u2022 beta");
    }

    [Fact]
    public void Convert_NestedUnorderedList_IndentsNested()
    {
        var input = "- parent\n  - child\n  - child two";
        var result = TelegramMarkdownFormatter.Convert(input);
        result.ShouldBe("\u2022 parent\n  \u2022 child\n  \u2022 child two");
    }

    [Fact]
    public void Convert_ListItemWithBoldAndCode_FormatsInline()
    {
        var result = TelegramMarkdownFormatter.Convert("- run `git status` for **changes**");
        result.ShouldBe("\u2022 run `git status` for *changes*");
    }

    // ─── Convert: blockquotes ────────────────────────────────────────────────

    [Fact]
    public void Convert_Blockquote_RendersMarkdownV2Quote()
    {
        var result = TelegramMarkdownFormatter.Convert("> quoted text");
        result.ShouldBe(">quoted text");
    }

    [Fact]
    public void Convert_MultilineBlockquote_PrefixesEachLine()
    {
        var result = TelegramMarkdownFormatter.Convert("> line one\n> line two");
        result.ShouldBe(">line one\n>line two");
    }

    [Fact]
    public void Convert_BlockquoteWithBold_FormatsInsideQuote()
    {
        var result = TelegramMarkdownFormatter.Convert("> a **bold** quote");
        result.ShouldBe(">a *bold* quote");
    }

    // ─── Convert: nested inline formatting ───────────────────────────────────

    [Fact]
    public void Convert_BoldContainingInlineCode_PreservesBoth()
    {
        // The old scanner escaped the inner backticks as literals; Markdig keeps the code span.
        var result = TelegramMarkdownFormatter.Convert("**use `code` here**");
        result.ShouldBe("*use `code` here*");
    }

    [Fact]
    public void Convert_BoldContainingLink_PreservesBoth()
    {
        var result = TelegramMarkdownFormatter.Convert("**see [docs](https://example.com)**");
        result.ShouldBe("*see [docs](https://example.com)*");
    }

    [Fact]
    public void Convert_LinkWithBoldText_FormatsLinkLabel()
    {
        var result = TelegramMarkdownFormatter.Convert("[**bold link**](https://example.com)");
        result.ShouldBe("[*bold link*](https://example.com)");
    }

    // ─── Convert: unbalanced tokens are safe (no greedy span) ─────────────────

    [Fact]
    public void Convert_LoneAsteriskInProse_DoesNotItaliciseAcrossText()
    {
        // "5 * 3 = 15 and 2 * 4 = 8": the old scanner greedily italicised between the asterisks.
        // Markdig treats them as literal '*' (no valid emphasis), so they are escaped, not paired.
        var result = TelegramMarkdownFormatter.Convert("5 * 3 = 15 and 2 * 4 = 8");
        result.ShouldNotContain("_");
        result.ShouldContain("\\*");
    }

    // ─── Convert: tables (no native Telegram support → monospace grid) ────────

    [Fact]
    public void Convert_PipeTable_RendersAsAlignedCodeBlock()
    {
        var input = "| Name | Age |\n| --- | --- |\n| Alice | 30 |\n| Bob | 5 |";
        var result = TelegramMarkdownFormatter.Convert(input);

        // Rendered inside a code block so columns line up in Telegram's monospace font.
        result.ShouldStartWith("```");
        result.ShouldEndWith("```");
        // No raw pipe markup leaks as MarkdownV2 formatting — pipes live inside the code block.
        result.ShouldContain("Name");
        result.ShouldContain("Alice");
        result.ShouldContain("Bob");
        // Columns are padded so the shorter "Bob"/"5" row aligns with the header widths.
        result.ShouldContain("Alice | 30");
    }

    [Fact]
    public void Convert_PipeTable_DoesNotLeakRawPipesAsFormatting()
    {
        var input = "| A | B |\n| --- | --- |\n| 1 | 2 |";
        var result = TelegramMarkdownFormatter.Convert(input);
        // The only backtick fences are the wrapping code block; pipes are plain text inside.
        result.ShouldStartWith("```");
        result.ShouldNotContain("\\|"); // pipes inside a pre block are not escaped, just literal
    }

    [Fact]
    public void Convert_PipeTable_ExactAlignedGrid()
    {
        // Pins the exact monospace grid a Telegram user receives: header row, dashed separator,
        // and padded data rows inside a code block, columns separated by " | ".
        var input = "| Name | Age |\n| --- | --- |\n| Alice | 30 |\n| Bob | 5 |";
        var result = TelegramMarkdownFormatter.Convert(input);
        result.ShouldBe(
            "```\n" +
            "Name  | Age\n" +
            "------+----\n" +
            "Alice | 30\n" +
            "Bob   | 5\n" +
            "```");
    }

    // ─── Convert: escape completeness (Telegram-400 safety property) ──────────

    [Theory]
    [InlineData("a_b")]
    [InlineData("x*y")]
    [InlineData("f(x)")]
    [InlineData("a.b.c")]
    [InlineData("1+1=2")]
    [InlineData("path/to-file.cs")]
    [InlineData("hello! world?")]
    [InlineData("[bracket] {brace}")]
    public void Convert_LiteralProse_EscapesAllSpecialChars(string input)
    {
        // Outside any recognised entity, every MarkdownV2 special char in literal prose must be
        // backslash-escaped, otherwise Telegram rejects the message with HTTP 400. This is a
        // safety property: no special char may appear unescaped in plain-text output.
        var result = TelegramMarkdownFormatter.Convert(input);
        const string specials = "_*[]()~`>#+-=|{}.!";
        for (var i = 0; i < result.Length; i++)
        {
            var c = result[i];
            if (specials.IndexOf(c) >= 0)
            {
                // Must be immediately preceded by a backslash (which itself may be escaped).
                (i > 0 && result[i - 1] == '\\').ShouldBeTrue(
                    $"Unescaped '{c}' at index {i} in \"{result}\" (input \"{input}\")");
            }
        }
    }
}
