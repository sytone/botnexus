using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace BotNexus.Extensions.Channels.Telegram;

/// <summary>
/// Converts LLM Markdown output to Telegram MarkdownV2 so the Telegram client renders
/// bold, italic, code, links, lists, blockquotes, headings, and tables rather than
/// displaying raw punctuation.
/// </summary>
/// <remarks>
/// <para>
/// Telegram MarkdownV2 reference: https://core.telegram.org/bots/api#markdownv2-style
/// </para>
/// <para>
/// LLM output is parsed with <see href="https://github.com/xoofx/markdig">Markdig</see>
/// (CommonMark + pipe tables) and the resulting AST is walked to emit MarkdownV2. Parsing to
/// an AST — rather than scanning characters — is what makes lists, nested spans, blockquotes,
/// balanced tokens, and tables correct by construction, and it avoids the malformed output that
/// causes Telegram to reject a message with HTTP 400.
/// </para>
/// <para>
/// MarkdownV2 has no native table or heading support. Headings are rendered bold; tables are
/// rendered as an aligned, column-padded monospace block (the only faithful representation
/// Telegram offers for tabular data) instead of leaking raw <c>| pipe | text |</c> markup.
/// </para>
/// </remarks>
internal static class TelegramMarkdownFormatter
{
    // Characters that must be backslash-escaped in MarkdownV2 literal-text regions.
    // Telegram rejects (400) any unescaped occurrence of one of these outside a recognized entity.
    private static readonly char[] EscapeChars =
        ['_', '*', '[', ']', '(', ')', '~', '`', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!', '\\'];

    private static readonly HashSet<char> EscapeSet = [.. EscapeChars];

    // A single Markdig pipeline reused for every conversion. UseAdvancedExtensions enables
    // pipe tables, strikethrough (~~), task lists, autolinks, etc. — the syntax LLMs emit.
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    /// <summary>
    /// Converts a markdown string (as produced by LLMs) into Telegram MarkdownV2.
    /// Recognized formatting is translated; all other special characters are escaped.
    /// Returns an empty string for null/empty input.
    /// </summary>
    public static string Convert(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return string.Empty;

        MarkdownDocument document;
        try
        {
            document = Markdown.Parse(markdown, Pipeline);
        }
        catch
        {
            // Defensive: if the parser ever throws on pathological input, fall back to a
            // fully-escaped plain rendering so the caller still gets a Telegram-safe string.
            return EscapeMarkdownV2(markdown);
        }

        var sb = new StringBuilder(markdown.Length + (markdown.Length / 4));
        WriteBlocks(document, sb, listDepth: 0);

        // Markdig appends a trailing newline per block; trim a single trailing newline run
        // so streamed deltas don't accumulate blank lines at the end.
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// Escapes all MarkdownV2 special characters in a plain-text string. Use this for
    /// structural strings (display prefixes, tool names, labels) that must appear as literal
    /// text with no formatting applied.
    /// </summary>
    public static string EscapeMarkdownV2(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var sb = new StringBuilder(text.Length + (text.Length / 4));
        AppendEscaped(text, sb);
        return sb.ToString();
    }

    // ─── Block rendering ─────────────────────────────────────────────────────

    private static void WriteBlocks(ContainerBlock container, StringBuilder sb, int listDepth)
    {
        for (var i = 0; i < container.Count; i++)
        {
            var block = container[i];
            WriteBlock(block, sb, listDepth);

            // Separate top-level blocks with a blank line (paragraph spacing), matching how
            // the source markdown read. List items are spaced by the list writer itself.
            if (listDepth == 0 && i < container.Count - 1)
                sb.Append('\n');
        }
    }

    private static void WriteBlock(Block block, StringBuilder sb, int listDepth)
    {
        switch (block)
        {
            case HeadingBlock heading:
                // No native heading in MarkdownV2 — render bold.
                sb.Append('*');
                WriteInlines(heading.Inline, sb);
                sb.Append('*');
                sb.Append('\n');
                break;

            case ParagraphBlock paragraph:
                WriteInlines(paragraph.Inline, sb);
                sb.Append('\n');
                break;

            case ListBlock list:
                WriteList(list, sb, listDepth);
                break;

            case QuoteBlock quote:
                WriteQuote(quote, sb, listDepth);
                break;

            case Table table:
                WriteTable(table, sb);
                break;

            case FencedCodeBlock fenced:
                WriteFencedCode(fenced, sb);
                break;

            case CodeBlock code:
                // Indented code block (no fence info) — render as a plain fenced block.
                WriteIndentedCode(code, sb);
                break;

            case ThematicBreakBlock:
                // Horizontal rule — Telegram has no <hr>; emit a short escaped divider.
                sb.Append("\\-\\-\\-\n");
                break;

            case ContainerBlock containerBlock:
                // Any other container (e.g. custom) — recurse.
                WriteBlocks(containerBlock, sb, listDepth);
                break;

            default:
                // Unknown leaf block — best-effort escaped text of its raw lines.
                if (block is LeafBlock leaf && leaf.Lines.Count > 0)
                {
                    AppendEscaped(leaf.Lines.ToString(), sb);
                    sb.Append('\n');
                }
                break;
        }
    }

    private static void WriteList(ListBlock list, StringBuilder sb, int listDepth)
    {
        var indent = new string(' ', listDepth * 2);
        var ordered = list.IsOrdered;
        var number = ParseStartNumber(list.OrderedStart);

        foreach (var child in list)
        {
            if (child is not ListItemBlock item) continue;

            sb.Append(indent);
            if (ordered)
            {
                // Ordered markers keep their number; the dot is a MarkdownV2 special char.
                sb.Append(number.ToString(System.Globalization.CultureInfo.InvariantCulture));
                sb.Append("\\. ");
                number++;
            }
            else
            {
                // Bullet glyph renders cleanly and needs no escaping.
                sb.Append("\u2022 ");
            }

            // A list item is itself a container of blocks (usually one paragraph, possibly a
            // nested list). Render the first paragraph inline on the marker line; deeper blocks
            // (nested lists) recurse with increased depth.
            WriteListItemBody(item, sb, listDepth);
        }
    }

    private static void WriteListItemBody(ListItemBlock item, StringBuilder sb, int listDepth)
    {
        var first = true;
        foreach (var child in item)
        {
            if (first && child is ParagraphBlock paragraph)
            {
                WriteInlines(paragraph.Inline, sb);
                sb.Append('\n');
                first = false;
                continue;
            }

            if (child is ListBlock nested)
            {
                WriteList(nested, sb, listDepth + 1);
                first = false;
                continue;
            }

            // Other block kinds inside a list item (code, quote, extra paragraphs):
            // render them with the item's indentation depth.
            if (first)
            {
                WriteBlock(child, sb, listDepth + 1);
                first = false;
            }
            else
            {
                WriteBlock(child, sb, listDepth + 1);
            }
        }

        if (first)
        {
            // Empty list item — still terminate the line.
            sb.Append('\n');
        }
    }

    private static void WriteQuote(QuoteBlock quote, StringBuilder sb, int listDepth)
    {
        // Render the quote's inner blocks, then prefix every produced line with '>'.
        var inner = new StringBuilder();
        WriteBlocks(quote, inner, listDepth);

        var text = inner.ToString().TrimEnd('\n');
        foreach (var line in text.Split('\n'))
        {
            sb.Append('>');
            sb.Append(line);
            sb.Append('\n');
        }
    }

    private static void WriteFencedCode(FencedCodeBlock fenced, StringBuilder sb)
    {
        var info = fenced.Info?.Trim();
        var content = fenced.Lines.ToString();

        sb.Append("```");
        if (!string.IsNullOrEmpty(info)) sb.Append(info);
        sb.Append('\n');
        AppendCodeEscaped(content, sb);
        sb.Append("\n```\n");
    }

    private static void WriteIndentedCode(CodeBlock code, StringBuilder sb)
    {
        var content = code.Lines.ToString();
        sb.Append("```\n");
        AppendCodeEscaped(content, sb);
        sb.Append("\n```\n");
    }

    // ─── Table rendering (no native Telegram support → aligned monospace block) ───

    private static void WriteTable(Table table, StringBuilder sb)
    {
        // Extract every cell's plain text (formatting is flattened — a monospace table can't
        // carry bold/italic), compute per-column widths, then emit a padded grid inside a
        // code block so columns line up in Telegram's monospace font.
        var rows = new List<List<string>>();
        foreach (var rowObj in table)
        {
            if (rowObj is not TableRow row) continue;
            var cells = new List<string>();
            foreach (var cellObj in row)
            {
                if (cellObj is not TableCell cell) continue;
                cells.Add(FlattenToPlainText(cell).Trim());
            }
            rows.Add(cells);
        }

        if (rows.Count == 0) return;

        var columnCount = rows.Max(r => r.Count);
        var widths = new int[columnCount];
        foreach (var row in rows)
        {
            for (var c = 0; c < row.Count; c++)
                widths[c] = Math.Max(widths[c], row[c].Length);
        }

        var grid = new StringBuilder();
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            for (var c = 0; c < columnCount; c++)
            {
                var value = c < row.Count ? row[c] : string.Empty;
                // Pad every column except the last so columns line up without trailing
                // whitespace at the end of each row.
                if (c < columnCount - 1)
                {
                    grid.Append(value.PadRight(widths[c]));
                    grid.Append(" | ");
                }
                else
                {
                    grid.Append(value);
                }
            }
            grid.Append('\n');

            // Header separator row after the first (header) row.
            if (r == 0 && table.Count > 1)
            {
                for (var c = 0; c < columnCount; c++)
                {
                    grid.Append(new string('-', widths[c]));
                    if (c < columnCount - 1) grid.Append("-+-");
                }
                grid.Append('\n');
            }
        }

        sb.Append("```\n");
        AppendCodeEscaped(grid.ToString().TrimEnd('\n'), sb);
        sb.Append("\n```\n");
    }

    // ─── Inline rendering ────────────────────────────────────────────────────

    private static void WriteInlines(ContainerInline? container, StringBuilder sb)
    {
        if (container is null) return;

        foreach (var inline in container)
            WriteInline(inline, sb);
    }

    private static void WriteInline(Inline inline, StringBuilder sb)
    {
        switch (inline)
        {
            case LiteralInline literal:
                AppendEscaped(literal.Content.ToString(), sb);
                break;

            case EmphasisInline emphasis:
                WriteEmphasis(emphasis, sb);
                break;

            case CodeInline code:
                sb.Append('`');
                AppendCodeEscaped(code.Content, sb);
                sb.Append('`');
                break;

            case LinkInline link:
                WriteLink(link, sb);
                break;

            case LineBreakInline:
                sb.Append('\n');
                break;

            case AutolinkInline auto:
                // Bare URL — render as a link to itself.
                sb.Append('[');
                AppendEscaped(auto.Url, sb);
                sb.Append("](");
                AppendUrlEscaped(auto.Url, sb);
                sb.Append(')');
                break;

            case HtmlInline html:
                // Raw inline HTML is not valid MarkdownV2 — escape it as literal text.
                AppendEscaped(html.Tag, sb);
                break;

            case ContainerInline containerInline:
                WriteInlines(containerInline, sb);
                break;

            default:
                // Unknown inline — emit its escaped textual form if available.
                AppendEscaped(inline.ToString() ?? string.Empty, sb);
                break;
        }
    }

    private static void WriteEmphasis(EmphasisInline emphasis, StringBuilder sb)
    {
        // Markdig encodes emphasis by delimiter char + run length:
        //   * or _ , length 1  → italic        → _…_
        //   * or _ , length 2  → bold           → *…*
        //   ~       , length 2 → strikethrough  → ~…~
        // A length-3 run (***) is represented as nested emphasis (bold wrapping italic),
        // so it is handled naturally by recursion.
        string open, close;
        switch (emphasis.DelimiterChar)
        {
            case '~':
                open = close = "~";
                break;
            case '*':
            case '_':
                if (emphasis.DelimiterCount >= 2) { open = close = "*"; }   // bold
                else { open = close = "_"; }                                 // italic
                break;
            default:
                open = close = string.Empty;
                break;
        }

        sb.Append(open);
        WriteInlines(emphasis, sb);
        sb.Append(close);
    }

    private static void WriteLink(LinkInline link, StringBuilder sb)
    {
        if (link.IsImage)
        {
            // MarkdownV2 has no inline image; render the alt text plus the URL as a link.
            sb.Append('[');
            WriteInlines(link, sb);
            sb.Append("](");
            AppendUrlEscaped(link.Url ?? string.Empty, sb);
            sb.Append(')');
            return;
        }

        sb.Append('[');
        WriteInlines(link, sb);
        sb.Append("](");
        AppendUrlEscaped(link.Url ?? string.Empty, sb);
        sb.Append(')');
    }

    // ─── Escaping helpers ────────────────────────────────────────────────────

    private static void AppendEscaped(string text, StringBuilder sb)
    {
        foreach (var c in text)
        {
            if (EscapeSet.Contains(c)) sb.Append('\\');
            sb.Append(c);
        }
    }

    // Inside code spans / pre blocks MarkdownV2 only requires ` and \ to be escaped.
    private static void AppendCodeEscaped(string text, StringBuilder sb)
    {
        foreach (var c in text)
        {
            if (c == '`' || c == '\\') sb.Append('\\');
            sb.Append(c);
        }
    }

    // Inside a (link) URL MarkdownV2 only requires ) and \ to be escaped.
    private static void AppendUrlEscaped(string url, StringBuilder sb)
    {
        foreach (var c in url)
        {
            if (c == ')' || c == '\\') sb.Append('\\');
            sb.Append(c);
        }
    }

    // Flattens an inline container (e.g. a table cell) to plain unformatted text.
    private static string FlattenToPlainText(Block block)
    {
        var sb = new StringBuilder();
        FlattenBlock(block, sb);
        return sb.ToString();
    }

    private static void FlattenBlock(Block block, StringBuilder sb)
    {
        switch (block)
        {
            case LeafBlock leaf when leaf.Inline is not null:
                FlattenInlines(leaf.Inline, sb);
                break;
            case ContainerBlock container:
                foreach (var child in container) FlattenBlock(child, sb);
                break;
        }
    }

    private static void FlattenInlines(ContainerInline container, StringBuilder sb)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    sb.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
                case LineBreakInline:
                    sb.Append(' ');
                    break;
                case ContainerInline child:
                    FlattenInlines(child, sb);
                    break;
                default:
                    sb.Append(inline.ToString());
                    break;
            }
        }
    }

    private static int ParseStartNumber(string? orderedStart)
        => int.TryParse(orderedStart, out var n) ? n : 1;
}
