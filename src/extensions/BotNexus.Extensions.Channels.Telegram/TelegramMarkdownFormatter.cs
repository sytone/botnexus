using System.Text;

namespace BotNexus.Extensions.Channels.Telegram;

/// <summary>
/// Converts LLM Markdown output to Telegram MarkdownV2 format so the Telegram client
/// renders bold, italic, code, links, and headings rather than displaying raw punctuation.
/// </summary>
/// <remarks>
/// Telegram MarkdownV2 reference: https://core.telegram.org/bots/api#markdownv2-style
///
/// Conversion rules applied:
/// - LLM **bold** → Telegram *bold* (single asterisk)
/// - LLM *italic* → Telegram _italic_ (underscore)
/// - LLM _italic_ → Telegram _italic_ (unchanged)
/// - LLM ***bold italic*** → Telegram *_bold italic_*
/// - LLM ~~strike~~ → Telegram ~strike~ (single tilde)
/// - LLM `code` → Telegram `code`
/// - LLM ```lang\n...\n``` → Telegram ```lang\n...\n```
/// - LLM [text](url) → Telegram [text](url)
/// - LLM # Heading → Telegram *Heading* (mapped to bold, no native heading support)
/// - All literal special chars are escaped with a preceding backslash.
/// </remarks>
internal static class TelegramMarkdownFormatter
{
    // All characters that must be escaped in MarkdownV2 literal text regions.
    private static readonly HashSet<char> EscapeSet =
    [
        '_', '*', '[', ']', '(', ')', '~', '`', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!', '\\'
    ];

    /// <summary>
    /// Converts a markdown string (as produced by LLMs) into Telegram MarkdownV2 format.
    /// Recognized formatting is converted; all other special characters are escaped.
    /// Returns an empty string for null/empty input.
    /// </summary>
    public static string Convert(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return string.Empty;

        var sb = new StringBuilder(markdown.Length + markdown.Length / 4);
        var i = 0;

        while (i < markdown.Length)
        {
            var c = markdown[i];

            // Fenced code block: ```[lang]\n...\n```
            if (c == '`' && Peek(markdown, i + 1) == '`' && Peek(markdown, i + 2) == '`')
            {
                i = ProcessCodeBlock(markdown, i, sb);
                continue;
            }

            // Inline code: `...`
            if (c == '`')
            {
                i = ProcessInlineCode(markdown, i, sb);
                continue;
            }

            // Heading at line start: # text
            if (c == '#' && (i == 0 || markdown[i - 1] == '\n'))
            {
                i = ProcessHeading(markdown, i, sb);
                continue;
            }

            // Bold+italic: ***...***
            if (c == '*' && Peek(markdown, i + 1) == '*' && Peek(markdown, i + 2) == '*')
            {
                i = ProcessBoldItalic(markdown, i, sb);
                continue;
            }

            // Bold: **...**
            if (c == '*' && Peek(markdown, i + 1) == '*')
            {
                i = ProcessBold(markdown, i, sb);
                continue;
            }

            // Strikethrough: ~~...~~
            if (c == '~' && Peek(markdown, i + 1) == '~')
            {
                i = ProcessStrikethrough(markdown, i, sb);
                continue;
            }

            // Link: [text](url)
            if (c == '[')
            {
                i = ProcessLink(markdown, i, sb);
                continue;
            }

            // Italic with single asterisk: *...*
            if (c == '*')
            {
                i = ProcessItalicStar(markdown, i, sb);
                continue;
            }

            // Italic with underscore: _..._
            if (c == '_')
            {
                i = ProcessItalicUnderscore(markdown, i, sb);
                continue;
            }

            // Literal character — escape if required by MarkdownV2
            if (EscapeSet.Contains(c)) sb.Append('\\');
            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Escapes all MarkdownV2 special characters in a plain-text string.
    /// Use this for structural strings (display prefixes, tool names, labels) that
    /// must appear as literal text with no formatting applied.
    /// </summary>
    public static string EscapeMarkdownV2(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var sb = new StringBuilder(text.Length + text.Length / 4);
        foreach (var c in text)
        {
            if (EscapeSet.Contains(c)) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    // Processes a fenced code block starting at `start` (the first `).
    // Emits ```lang\ncontent\n``` with ` and \ escaped inside the block.
    private static int ProcessCodeBlock(string text, int start, StringBuilder sb)
    {
        var openEnd = start + 3; // position after the opening ```

        // Find the newline that terminates the opening fence (and optional lang tag).
        var newlineIndex = text.IndexOf('\n', openEnd);
        if (newlineIndex < 0)
        {
            // No newline found — treat ``` as escaped literal
            sb.Append("\\`\\`\\`");
            return start + 3;
        }

        var lang = text.Substring(openEnd, newlineIndex - openEnd).Trim();
        var contentStart = newlineIndex + 1;

        // Find the closing ```, which must appear after a newline.
        const string ClosePattern = "\n```";
        var closeIndex = text.IndexOf(ClosePattern, contentStart, StringComparison.Ordinal);
        if (closeIndex < 0)
        {
            // No closing fence — treat opening as escaped literal
            sb.Append("\\`\\`\\`");
            return start + 3;
        }

        var content = text.Substring(contentStart, closeIndex - contentStart);

        sb.Append("```");
        if (!string.IsNullOrEmpty(lang)) sb.Append(lang);
        sb.Append('\n');

        // Inside pre/code blocks only ` and \ must be escaped.
        foreach (var c in content)
        {
            if (c == '`' || c == '\\') sb.Append('\\');
            sb.Append(c);
        }

        sb.Append("\n```");

        // closeIndex points at the \n before ```; skip past \n + ```
        return closeIndex + ClosePattern.Length;
    }

    // Processes an inline code span starting at `start` (the opening `).
    private static int ProcessInlineCode(string text, int start, StringBuilder sb)
    {
        var contentStart = start + 1;

        // Locate the closing backtick; do not cross line boundaries.
        var closeIndex = -1;
        for (var j = contentStart; j < text.Length; j++)
        {
            if (text[j] == '\n') break;
            if (text[j] == '`') { closeIndex = j; break; }
        }

        if (closeIndex < 0)
        {
            sb.Append("\\`");
            return start + 1;
        }

        var content = text.Substring(contentStart, closeIndex - contentStart);

        sb.Append('`');
        foreach (var c in content)
        {
            if (c == '`' || c == '\\') sb.Append('\\');
            sb.Append(c);
        }
        sb.Append('`');

        return closeIndex + 1;
    }

    // Processes a heading line (# / ## / etc.) starting at `start`.
    // Telegram has no native heading; headings are mapped to bold.
    private static int ProcessHeading(string text, int start, StringBuilder sb)
    {
        var i = start;
        while (i < text.Length && text[i] == '#') i++;

        // Skip the space between # markers and the heading text.
        if (i < text.Length && text[i] == ' ') i++;

        var lineEnd = text.IndexOf('\n', i);
        var headingText = lineEnd >= 0
            ? text.Substring(i, lineEnd - i)
            : text.Substring(i);

        sb.Append('*');
        AppendLiteralEscaped(headingText, sb);
        sb.Append('*');

        return lineEnd >= 0 ? lineEnd : text.Length;
    }

    // Processes a bold+italic span ***...*** → Telegram *_..._*
    private static int ProcessBoldItalic(string text, int start, StringBuilder sb)
    {
        var contentStart = start + 3;
        var closeIndex = text.IndexOf("***", contentStart, StringComparison.Ordinal);
        if (closeIndex < 0)
        {
            sb.Append("\\*\\*\\*");
            return start + 3;
        }

        var content = text.Substring(contentStart, closeIndex - contentStart);
        sb.Append("*_");
        AppendLiteralEscaped(content, sb);
        sb.Append("_*");

        return closeIndex + 3;
    }

    // Processes a bold span **...** → Telegram *...*
    private static int ProcessBold(string text, int start, StringBuilder sb)
    {
        var contentStart = start + 2;
        var closeIndex = text.IndexOf("**", contentStart, StringComparison.Ordinal);
        if (closeIndex < 0)
        {
            sb.Append("\\*\\*");
            return start + 2;
        }

        var content = text.Substring(contentStart, closeIndex - contentStart);
        sb.Append('*');
        AppendLiteralEscaped(content, sb);
        sb.Append('*');

        return closeIndex + 2;
    }

    // Processes a strikethrough span ~~...~~ → Telegram ~...~
    private static int ProcessStrikethrough(string text, int start, StringBuilder sb)
    {
        var contentStart = start + 2;
        var closeIndex = text.IndexOf("~~", contentStart, StringComparison.Ordinal);
        if (closeIndex < 0)
        {
            sb.Append("\\~\\~");
            return start + 2;
        }

        var content = text.Substring(contentStart, closeIndex - contentStart);
        sb.Append('~');
        AppendLiteralEscaped(content, sb);
        sb.Append('~');

        return closeIndex + 2;
    }

    // Processes a markdown link [text](url) → Telegram [text](url)
    private static int ProcessLink(string text, int start, StringBuilder sb)
    {
        var closeTextIndex = text.IndexOf(']', start + 1);
        if (closeTextIndex < 0 || closeTextIndex + 1 >= text.Length || text[closeTextIndex + 1] != '(')
        {
            // Not a valid link — escape the [ and move on.
            sb.Append("\\[");
            return start + 1;
        }

        var closeUrlIndex = text.IndexOf(')', closeTextIndex + 2);
        if (closeUrlIndex < 0)
        {
            sb.Append("\\[");
            return start + 1;
        }

        var linkText = text.Substring(start + 1, closeTextIndex - start - 1);
        var url = text.Substring(closeTextIndex + 2, closeUrlIndex - closeTextIndex - 2);

        sb.Append('[');
        AppendLiteralEscaped(linkText, sb);
        sb.Append("](");

        // Inside the URL, only ) and \ must be escaped.
        foreach (var c in url)
        {
            if (c == ')' || c == '\\') sb.Append('\\');
            sb.Append(c);
        }

        sb.Append(')');

        return closeUrlIndex + 1;
    }

    // Processes italic *...* → Telegram _..._
    // Only matches a single * (not **); does not cross line boundaries.
    private static int ProcessItalicStar(string text, int start, StringBuilder sb)
    {
        var contentStart = start + 1;

        var closeIndex = -1;
        for (var j = contentStart; j < text.Length; j++)
        {
            if (text[j] == '\n') break;
            // Closing * must not be immediately followed by another * (that would be **).
            if (text[j] == '*' && Peek(text, j + 1) != '*')
            {
                closeIndex = j;
                break;
            }
        }

        if (closeIndex < 0)
        {
            sb.Append("\\*");
            return start + 1;
        }

        var content = text.Substring(contentStart, closeIndex - contentStart);
        sb.Append('_');
        AppendLiteralEscaped(content, sb);
        sb.Append('_');

        return closeIndex + 1;
    }

    // Processes italic _..._ → Telegram _..._
    // Does not cross line boundaries.
    private static int ProcessItalicUnderscore(string text, int start, StringBuilder sb)
    {
        var contentStart = start + 1;

        var closeIndex = -1;
        for (var j = contentStart; j < text.Length; j++)
        {
            if (text[j] == '\n') break;
            // Closing _ must not be immediately followed by another _ (that would be __).
            if (text[j] == '_' && Peek(text, j + 1) != '_')
            {
                closeIndex = j;
                break;
            }
        }

        if (closeIndex < 0)
        {
            sb.Append("\\_");
            return start + 1;
        }

        var content = text.Substring(contentStart, closeIndex - contentStart);
        sb.Append('_');
        AppendLiteralEscaped(content, sb);
        sb.Append('_');

        return closeIndex + 1;
    }

    // Appends each character of `text` to `sb`, escaping MarkdownV2 special chars.
    // Used for the content inside formatting spans where no further markdown is parsed.
    private static void AppendLiteralEscaped(string text, StringBuilder sb)
    {
        foreach (var c in text)
        {
            if (EscapeSet.Contains(c)) sb.Append('\\');
            sb.Append(c);
        }
    }

    private static char Peek(string text, int index)
        => index < text.Length ? text[index] : '\0';
}
