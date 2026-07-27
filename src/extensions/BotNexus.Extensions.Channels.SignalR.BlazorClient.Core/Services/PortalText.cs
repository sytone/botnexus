namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Text normalisation for single-line portal chrome (#2441). Agent display names, descriptions and
/// conversation titles are user- and config-supplied, so they can carry embedded newlines, tabs,
/// carriage returns and other control characters. Those must never reach a single-line row: a raw
/// <c>\n</c> renders as extra whitespace, defeats <c>text-overflow: ellipsis</c>, and can grow the
/// row height depending on the white-space rule in force. Normalising at the render boundary makes
/// the guarantee structural rather than dependent on a CSS rule staying correct.
/// </summary>
public static class PortalText
{
    /// <summary>
    /// Collapses a value to a single line: every Unicode control character (including
    /// <c>\n</c>, <c>\r</c>, <c>\t</c>) and every whitespace run becomes exactly one space, and
    /// leading/trailing whitespace is trimmed. Null or blank input returns an empty string.
    /// </summary>
    /// <param name="value">Raw text, possibly containing control characters.</param>
    /// <returns>A trimmed single-line rendering of <paramref name="value"/>.</returns>
    public static string SingleLine(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var buffer = new System.Text.StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value)
        {
            // Control characters are treated as whitespace rather than dropped so that
            // "a\tb" stays two readable words instead of collapsing into "ab".
            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                pendingSpace = buffer.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                buffer.Append(' ');
                pendingSpace = false;
            }

            buffer.Append(ch);
        }

        return buffer.ToString();
    }
}
