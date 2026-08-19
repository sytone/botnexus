using System.Net;
using System.Text;

namespace BotNexus.Extensions.Channels.Matrix;

/// <summary>
/// Converts the Markdown that BotNexus agents emit into the <c>org.matrix.custom.html</c> subset
/// Matrix clients render, and builds the paired <c>m.room.message</c> content.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a small, total converter rather than a full CommonMark implementation. Matrix
/// messages always carry a plain-text <c>body</c> alongside the optional <c>formatted_body</c>, so
/// a construct this converter does not recognise degrades to correct plain text rather than to
/// broken markup - the failure mode that matters.
/// </para>
/// <para>
/// Every literal segment is HTML-escaped before any tag is emitted, so agent output containing
/// <c>&lt;script&gt;</c> or a stray angle bracket cannot inject markup into a reader's client.
/// </para>
/// </remarks>
public static class MatrixMessageFormatter
{
    /// <summary>The Matrix format identifier for HTML-formatted message bodies.</summary>
    public const string HtmlFormat = "org.matrix.custom.html";

    /// <summary>
    /// Builds an <c>m.text</c> message content for the supplied Markdown, attaching a
    /// <c>formatted_body</c> only when the conversion actually produced markup.
    /// </summary>
    /// <param name="markdown">Agent-authored Markdown.</param>
    /// <param name="threadRootEventId">
    /// Optional <c>m.thread</c> root event ID. When set the message is sent as a thread reply.
    /// </param>
    /// <param name="replacesEventId">
    /// Optional event ID this message replaces via <c>m.replace</c>. Used by the streaming path to
    /// edit a message in place.
    /// </param>
    /// <returns>The message content ready to send.</returns>
    public static MatrixMessageContent BuildTextMessage(
        string? markdown,
        string? threadRootEventId = null,
        string? replacesEventId = null)
    {
        var plain = markdown ?? string.Empty;
        var html = ToHtml(plain);

        // A formatted_body that renders exactly the same as the plain body carries no information
        // and costs every client an extra parse, so it is omitted when the markdown had no
        // constructs. The comparison is against the HTML rendering of the PLAIN text - escaped, with
        // newlines as <br/> - not against the raw text: ToHtml necessarily emits <br/> for line
        // breaks, so comparing with the raw escaped string would report markup for every message
        // that contains none.
        var hasMarkup = !string.Equals(html, RenderPlainAsHtml(plain), StringComparison.Ordinal);

        var content = new MatrixMessageContent
        {
            MsgType = "m.text",
            Body = plain,
            Format = hasMarkup ? HtmlFormat : null,
            FormattedBody = hasMarkup ? html : null,
        };

        if (!string.IsNullOrWhiteSpace(replacesEventId))
        {
            // An m.replace edit carries the NEW content under m.new_content while the top-level
            // body stays a "* <text>" fallback for clients that do not render edits. Emitting only
            // the top-level body would make the edit invisible on compliant clients.
            content.NewContent = new MatrixMessageContent
            {
                MsgType = "m.text",
                Body = plain,
                Format = hasMarkup ? HtmlFormat : null,
                FormattedBody = hasMarkup ? html : null,
            };
            content.Body = "* " + plain;
            if (hasMarkup)
                content.FormattedBody = "* " + html;

            content.RelatesTo = new MatrixRelatesTo
            {
                RelType = "m.replace",
                EventId = replacesEventId,
            };
        }
        else if (!string.IsNullOrWhiteSpace(threadRootEventId))
        {
            content.RelatesTo = new MatrixRelatesTo
            {
                RelType = "m.thread",
                EventId = threadRootEventId,
            };
        }

        return content;
    }

    /// <summary>
    /// Renders <paramref name="text"/> as the HTML a no-markup message would produce: every line
    /// HTML-escaped and terminated with <c>&lt;br/&gt;</c>, exactly as <see cref="ToHtml"/> emits a
    /// line containing no Markdown constructs. Used to decide whether a conversion actually found
    /// any markup.
    /// </summary>
    private static string RenderPlainAsHtml(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length + 16);
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            builder.Append(string.IsNullOrWhiteSpace(line)
                ? "<br/>"
                : WebUtility.HtmlEncode(line) + "<br/>");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Converts a Markdown string to the Matrix HTML subset. Always HTML-escapes literal text
    /// before emitting any tag.
    /// </summary>
    /// <param name="markdown">Markdown source.</param>
    /// <returns>HTML suitable for a Matrix <c>formatted_body</c>.</returns>
    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return string.Empty;

        var builder = new StringBuilder(markdown.Length + 32);
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        var inFencedBlock = false;
        var fenceBuffer = new StringBuilder();

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inFencedBlock)
                {
                    builder.Append("<pre><code>")
                           .Append(WebUtility.HtmlEncode(fenceBuffer.ToString()))
                           .Append("</code></pre>");
                    fenceBuffer.Clear();
                    inFencedBlock = false;
                }
                else
                {
                    inFencedBlock = true;
                }

                continue;
            }

            if (inFencedBlock)
            {
                if (fenceBuffer.Length > 0)
                    fenceBuffer.Append('\n');
                fenceBuffer.Append(line);
                continue;
            }

            builder.Append(ConvertLine(line));
        }

        // An unterminated fence is agent output, not a protocol error. Emit what was buffered as a
        // code block rather than silently dropping the tail of the message.
        if (inFencedBlock && fenceBuffer.Length > 0)
        {
            builder.Append("<pre><code>")
                   .Append(WebUtility.HtmlEncode(fenceBuffer.ToString()))
                   .Append("</code></pre>");
        }

        return builder.ToString();
    }

    private static string ConvertLine(string line)
    {
        var trimmed = line.TrimStart();

        // Headings: #..###### followed by a space.
        var hashes = 0;
        while (hashes < trimmed.Length && trimmed[hashes] == '#')
            hashes++;

        if (hashes is >= 1 and <= 6 && hashes < trimmed.Length && trimmed[hashes] == ' ')
        {
            var text = ConvertInline(trimmed[(hashes + 1)..]);
            return $"<h{hashes}>{text}</h{hashes}>";
        }

        // Unordered list item: -, * or + followed by a space. Emitted as a standalone <li> without
        // a wrapping <ul>; Matrix clients render this acceptably and a full block parser is out of
        // scope for the first slice.
        if (trimmed.Length > 1 && trimmed[0] is '-' or '*' or '+' && trimmed[1] == ' ')
            return $"<li>{ConvertInline(trimmed[2..])}</li>";

        if (string.IsNullOrWhiteSpace(line))
            return "<br/>";

        return ConvertInline(line) + "<br/>";
    }

    private static string ConvertInline(string text)
    {
        var builder = new StringBuilder(text.Length + 16);
        var literal = new StringBuilder();

        void FlushLiteral()
        {
            if (literal.Length == 0)
                return;
            builder.Append(WebUtility.HtmlEncode(literal.ToString()));
            literal.Clear();
        }

        var index = 0;
        while (index < text.Length)
        {
            // Inline code first: its contents are literal, so emphasis markers inside a code span
            // must not be interpreted.
            if (text[index] == '`')
            {
                var close = text.IndexOf('`', index + 1);
                if (close > index)
                {
                    FlushLiteral();
                    builder.Append("<code>")
                           .Append(WebUtility.HtmlEncode(text[(index + 1)..close]))
                           .Append("</code>");
                    index = close + 1;
                    continue;
                }
            }

            if (text[index] == '*' && index + 1 < text.Length && text[index + 1] == '*')
            {
                var close = text.IndexOf("**", index + 2, StringComparison.Ordinal);
                if (close > index + 1)
                {
                    FlushLiteral();
                    builder.Append("<strong>")
                           .Append(ConvertInline(text[(index + 2)..close]))
                           .Append("</strong>");
                    index = close + 2;
                    continue;
                }
            }

            if (text[index] is '*' or '_')
            {
                var marker = text[index];
                var close = text.IndexOf(marker, index + 1);
                if (close > index + 1)
                {
                    FlushLiteral();
                    builder.Append("<em>")
                           .Append(ConvertInline(text[(index + 1)..close]))
                           .Append("</em>");
                    index = close + 1;
                    continue;
                }
            }

            // Links: [text](url). The URL is attribute-escaped and restricted to safe schemes so a
            // javascript: or data: target cannot be produced from agent output.
            if (text[index] == '[')
            {
                var closeText = text.IndexOf(']', index + 1);
                if (closeText > index && closeText + 1 < text.Length && text[closeText + 1] == '(')
                {
                    var closeUrl = text.IndexOf(')', closeText + 2);
                    if (closeUrl > closeText + 1)
                    {
                        var label = text[(index + 1)..closeText];
                        var url = text[(closeText + 2)..closeUrl];

                        FlushLiteral();
                        if (IsSafeUrl(url))
                        {
                            builder.Append("<a href=\"")
                                   .Append(WebUtility.HtmlEncode(url))
                                   .Append("\">")
                                   .Append(ConvertInline(label))
                                   .Append("</a>");
                        }
                        else
                        {
                            // Unsafe scheme: keep the visible text, drop the link. Silently
                            // emitting the anchor would hand a reader's client a hostile target.
                            builder.Append(ConvertInline(label));
                        }

                        index = closeUrl + 1;
                        continue;
                    }
                }
            }

            literal.Append(text[index]);
            index++;
        }

        FlushLiteral();
        return builder.ToString();
    }

    /// <summary>
    /// Whether a Markdown link target may be emitted as an <c>href</c>. Only http, https, mailto
    /// and Matrix's own matrix: scheme are permitted; everything else (notably javascript: and
    /// data:) is rejected.
    /// </summary>
    private static bool IsSafeUrl(string url)
    {
        var trimmed = url.Trim();
        return trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("matrix:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith('#')
            || trimmed.StartsWith('/');
    }
}
