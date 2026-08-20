namespace BotNexus.Domain.Text;

/// <summary>
/// The single normalisation seam for external, operator- or agent-supplied display text
/// (#2553).
/// </summary>
/// <remarks>
/// <para>
/// Cron job names, and text like them, are configuration data that crosses into surfaces a
/// human or an agent reads - conversation titles today, plausibly prompt context tomorrow.
/// Left raw, such a string can carry newlines and control characters that let it forge
/// structure in whatever it is rendered into (the shape OpenClaw fixed in <c>4823d7fe7b20</c>).
/// </para>
/// <para>
/// This is deliberately NOT a prompt-fencing framework. It is one policy - collapse all
/// whitespace (including <c>\r</c> and <c>\n</c>) to single spaces, drop control characters,
/// trim, and bound the length - applied at one place so the four cron producers cannot drift
/// apart.
/// </para>
/// </remarks>
public static class ExternalText
{
    /// <summary>
    /// Default bound for short display strings such as a conversation title.
    /// </summary>
    public const int DefaultDisplayLength = 200;

    /// <summary>
    /// Normalises externally supplied text for safe display: collapses every whitespace run
    /// (including CR/LF and tabs) to a single space, strips non-whitespace control and format
    /// characters, trims, and truncates to <paramref name="maxLength"/>.
    /// </summary>
    /// <param name="value">The untrusted text. May be <see langword="null"/>.</param>
    /// <param name="maxLength">Maximum length of the result. Must be positive.</param>
    /// <returns>
    /// A single-line, control-character-free, length-bounded string. Empty when
    /// <paramref name="value"/> is null, empty, or contains nothing but whitespace and
    /// control characters - callers decide their own fallback.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxLength"/> is zero or negative.
    /// </exception>
    /// <remarks>
    /// Documented forwarding shim (#2925). The implementation, and the Unicode-scalar rationale
    /// that goes with it, moved to <see cref="StringTextExtensions.SanitizeExternalText"/> so the
    /// operation is discoverable from any string value; this entry point is retained verbatim so
    /// existing call sites and the public API surface are untouched. Prefer
    /// <c>value.SanitizeExternalText(max)</c> in new code.
    /// </remarks>
    public static string Sanitize(string? value, int maxLength = DefaultDisplayLength)
        => value.SanitizeExternalText(maxLength);
}