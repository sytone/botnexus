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
    /// <remarks>
    /// Documented forwarding shim (#2925). The implementation moved to
    /// <see cref="PortalTextExtensions.ToSingleLine"/> so the operation is discoverable from any
    /// string value; this entry point is retained verbatim so the existing razor call sites and the
    /// public API surface are untouched. Prefer <c>value.ToSingleLine()</c> in new code.
    /// </remarks>
    public static string SingleLine(string? value) => value.ToSingleLine();
}