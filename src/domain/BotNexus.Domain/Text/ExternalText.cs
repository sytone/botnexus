using System.Globalization;
using System.Text;

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
    /// Enumeration is per Unicode scalar value, not per UTF-16 code unit (#2923). A per-<c>char</c>
    /// loop cannot tell a well-formed surrogate pair from a lone surrogate - every half of every
    /// pair reports <see cref="UnicodeCategory.Surrogate"/> - so the guard that was meant to drop
    /// ill-formed text deleted every astral character instead. Decoding scalar by scalar makes the
    /// distinction explicit: pairs survive, lone surrogates are dropped. The length bound remains
    /// in UTF-16 code units, applied by <see cref="TextTruncation.SafeTruncate"/> so it can never
    /// cut a pair back apart (#2883).
    /// </remarks>
    public static string Sanitize(string? value, int maxLength = DefaultDisplayLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);

        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        var pendingSpace = false;
        var remaining = value.AsSpan();

        while (!remaining.IsEmpty)
        {
            // DecodeFromUtf16 rather than EnumerateRunes: enumeration silently substitutes
            // U+FFFD for an ill-formed sequence, which would make a lone surrogate
            // indistinguishable from a replacement character the operator actually typed.
            // The status code keeps that distinction, so only genuinely ill-formed input is
            // dropped.
            var status = Rune.DecodeFromUtf16(remaining, out var rune, out var consumed);
            remaining = remaining[consumed..];

            if (status != System.Buffers.OperationStatus.Done)
            {
                // Lone surrogate (high without low, or a stray low). Drop it - this is the
                // original guard's intent, now applied only where it belongs.
                continue;
            }

            if (Rune.IsWhiteSpace(rune))
            {
                // Collapse CR/LF/tab/space runs into one space. This is the newline-collapse
                // that keeps a job name from forging a second line in a title or prompt.
                pendingSpace = builder.Length > 0;
                continue;
            }

            // UnicodeCategory.Surrogate is deliberately absent: a decoded Rune is a valid scalar
            // value and can never be a surrogate, so listing it here would be an unreachable
            // branch. Lone surrogates are rejected above, by decode status.
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(rune);

            // Bail out once the buffer can no longer contribute to a maxLength-bounded result.
            // The final bound is applied below; this only keeps the buffer from growing without
            // limit on a pathological input.
            if (builder.Length >= maxLength)
            {
                break;
            }
        }

        // SafeTruncate cuts on a grapheme-cluster boundary, so the bound can never split the very
        // surrogate pairs this method now preserves.
        return TextTruncation.SafeTruncate(builder.ToString(), maxLength)!.TrimEnd();
    }
}
