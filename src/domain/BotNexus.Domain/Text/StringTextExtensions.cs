using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BotNexus.Gateway.Abstractions.Text;

namespace BotNexus.Domain.Text;

/// <summary>
/// The domain-side home for general-purpose text-to-text operations, expressed as
/// <c>this string</c> extensions (#2925).
/// </summary>
/// <remarks>
/// <para>
/// These are the implementations, not wrappers. <see cref="TextTruncation"/>,
/// <see cref="ExternalText"/> and <see cref="EscapedMarkupNormalizer"/> retain their original
/// static entry points as documented forwarding shims so no public API breaks and no call site
/// has to change; every one of them delegates here, so there is exactly one behaviour.
/// </para>
/// <para>
/// The point of the move is discoverability. An engineer holding a string could not previously
/// find <c>SafeTruncate</c> without already knowing the helper class name, which is how the
/// codebase acquired three truncation implementations (#2924) and fourteen copied one-liners
/// (#2883). IntelliSense on the value now surfaces them.
/// </para>
/// <para>
/// Scope is deliberately narrow: text in, text out, no domain semantics. Static factories that
/// return a richer type (<c>ModelFamilyVersion.TryParse</c> and friends) and domain policies
/// keyed on a string (<c>SkillTrustVerifier.Verify</c>) stay static by decision - see #2926 and
/// #2927. Putting those on <see cref="string"/> would pollute every string in the product with
/// unrelated semantics.
/// </para>
/// <para>
/// Portal-side text extensions cannot live here: <c>BlazorClient.Core</c> is barred from
/// referencing <c>BotNexus.Domain</c> by <c>WasmPayloadDependencyArchitectureTests</c>, so the
/// WASM payload does not drag in the domain assembly. Those live beside their own helper in
/// <c>BotNexus.Extensions.Channels.SignalR.BlazorClient.Core</c>.
/// </para>
/// </remarks>
public static class StringTextExtensions
{
    /// <summary>
    /// Truncates <paramref name="value"/> to at most <paramref name="maxLength"/> UTF-16 code
    /// units without ever splitting a surrogate pair or a grapheme cluster, appending
    /// <paramref name="suffix"/> only when the value was actually shortened.
    /// </summary>
    /// <param name="value">The text to truncate. <see langword="null"/> is returned unchanged.</param>
    /// <param name="maxLength">
    /// The maximum length of the retained portion, in UTF-16 code units, excluding
    /// <paramref name="suffix"/>. Negative values are treated as zero.
    /// </param>
    /// <param name="suffix">
    /// Appended only when truncation occurred, so a short input is returned byte-identical.
    /// </param>
    /// <returns>
    /// The original reference when no truncation is needed, otherwise a grapheme-safe prefix with
    /// <paramref name="suffix"/> appended.
    /// </returns>
    public static string? SafeTruncate(this string? value, int maxLength, string suffix = "")
    {
        if (value is null)
        {
            return null;
        }

        // Returning the original reference (not a copy) keeps the ASCII path allocation-free and
        // byte-identical to the raw slicing it replaces - see acceptance criterion 4 on #2883.
        // Delegation to the shared boundary policy (#2924) preserves this exactly: the shared
        // helper applies the identical short-circuit, and this one is kept so the reference-return
        // guarantee is pinned at the domain seam too rather than inherited silently.
        if (value.Length <= maxLength)
        {
            return value;
        }

        return GraphemeSafeTruncation.Truncate(value, maxLength, suffix);
    }

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
    /// in UTF-16 code units, applied by <see cref="SafeTruncate"/> so it can never cut a pair back
    /// apart (#2883).
    /// </remarks>
    public static string SanitizeExternalText(
        this string? value,
        int maxLength = ExternalText.DefaultDisplayLength)
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
        return builder.ToString().SafeTruncate(maxLength)!.TrimEnd();
    }

    /// <summary>
    /// Returns <paramref name="text"/> with every span that matches <paramref name="pattern"/>
    /// removed, matching against a decoded view so escaped spellings of the marker are caught
    /// as well as the literal spelling.
    /// </summary>
    /// <param name="text">
    /// The untrusted text. Returned by reference when nothing matches, so a clean input costs
    /// no allocation beyond the scan buffer.
    /// </param>
    /// <param name="pattern">
    /// The marker pattern, written in LITERAL form only. It must be linear-time bounded - it is
    /// applied to attacker-controlled input.
    /// </param>
    /// <returns>The original text with all matched spans (in original spelling) deleted.</returns>
    public static string RemoveEscapedMarkupMatches(this string text, Regex pattern) =>
        EscapedMarkupNormalizer.ReplaceMatchesCore(text, pattern);
}
