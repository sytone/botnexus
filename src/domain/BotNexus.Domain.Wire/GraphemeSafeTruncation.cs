using System.Globalization;

namespace BotNexus.Gateway.Abstractions.Text;

// Namespace note: this sits under BotNexus.Gateway.Abstractions.* alongside the other wire shapes
// in this assembly rather than in a new top-level BotNexus.Text. A top-level "Text" namespace is
// ambiguous with System.Text and with Spectre.Console's Text type at every call site that has both
// in scope - it produced CS0118 across BotNexus.Cli on the first attempt.

/// <summary>
/// The single canonical grapheme-cluster boundary policy for length-bounding UTF-16 text anywhere
/// in the product: the gateway domain, the Telegram channel and the Blazor WebAssembly portal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type is named for the boundary, not for a consumer (#2924, #2813).</b> The previous
/// arrangement had three implementations: <c>TextTruncation.SafeTruncate</c> (grapheme-correct),
/// <c>SurrogateSafeText.SurrogateSafeTruncate</c> in the portal, and
/// <c>TelegramMessageSplitter.SliceSurrogateSafe</c>. Two of the three were named after the weaker
/// guarantee they happened to implement ("surrogate safe"), and one was reachable only from the
/// assembly of a single consumer. A helper named after one consumer's boundary is invisible to the
/// next consumer, which is precisely how the second and third copies came to be written. This type
/// is named for the invariant it enforces so that any future caller with the same need finds it.
/// </para>
/// <para>
/// <b>Why it lives in <c>BotNexus.Domain.Wire</c>.</b> <c>BlazorClient.Core</c> feeds a Blazor
/// WebAssembly app: every assembly it references is downloaded by the browser, and
/// <c>WasmPayloadDependencyArchitectureTests</c> forbids it from referencing <c>BotNexus.Domain</c>
/// (which drags <c>Vogen.SharedTypes</c> into the payload at runtime - see #2329). That constraint
/// is real and is what produced the weaker duplicate in the first place. The resolution is #2329
/// proposal 3, already used for the ask_user wire shapes: the shared algorithm lives in the
/// zero-dependency wire assembly that is already inside the sanctioned payload closure, so
/// unification costs the browser nothing and adds no assembly to the WASM graph.
/// </para>
/// <para>
/// <b>Why grapheme clusters and not merely surrogate pairs.</b> Not splitting a surrogate pair is
/// the correctness floor (a lone surrogate renders as U+FFFD and, once persisted, is unrepairable -
/// #2883). Cutting on a grapheme-cluster boundary is strictly stronger and additionally keeps
/// combining marks, regional-indicator flag pairs and ZWJ emoji sequences intact, so a truncated
/// string never renders as a mangled half-glyph or leaves a dangling U+200D.
/// </para>
/// </remarks>
public static class GraphemeSafeTruncation
{
    /// <summary>
    /// Returns the largest index at or before <paramref name="limit"/> that does not fall inside a
    /// surrogate pair or a grapheme cluster. Returns 0 when not even the first cluster fits.
    /// </summary>
    /// <param name="value">The text whose boundaries are being located.</param>
    /// <param name="limit">
    /// The desired cut position in UTF-16 code units. Values &lt;= 0 yield 0; values at or beyond
    /// the length of <paramref name="value"/> yield its length.
    /// </param>
    /// <remarks>
    /// Walking text elements forward from index 0 is O(limit) rather than O(value.Length), and is
    /// the only correct approach for clusters whose extent is not decidable by inspecting the cut
    /// point alone - a ZWJ emoji sequence spans many code units and its joins are visible only by
    /// walking forward from a known boundary.
    /// </remarks>
    public static int FindBoundaryAtOrBefore(ReadOnlySpan<char> value, int limit)
    {
        if (limit <= 0)
        {
            return 0;
        }

        if (limit >= value.Length)
        {
            return value.Length;
        }

        var consumed = 0;
        while (consumed < limit)
        {
            var next = StringInfo.GetNextTextElementLength(value[consumed..]);
            if (next <= 0)
            {
                // Defensive: GetNextTextElementLength never returns 0 for a non-empty span, but a
                // zero here would spin this loop forever. Stopping keeps the caller terminating.
                break;
            }

            if (consumed + next > limit)
            {
                // The limit lands inside this cluster; the previous boundary is the answer.
                break;
            }

            consumed += next;
        }

        return consumed;
    }

    /// <summary>
    /// Truncates <paramref name="value"/> to at most <paramref name="maxLength"/> UTF-16 code units
    /// on a grapheme-cluster boundary, appending <paramref name="suffix"/> only when the value was
    /// actually shortened.
    /// </summary>
    /// <param name="value">The text to truncate. <see langword="null"/> is returned unchanged.</param>
    /// <param name="maxLength">
    /// Maximum length of the retained portion in UTF-16 code units, excluding
    /// <paramref name="suffix"/>. Negative values behave as zero.
    /// </param>
    /// <param name="suffix">Appended only when truncation occurred.</param>
    /// <returns>
    /// The original reference when no truncation is needed - so the common short-string path is
    /// allocation-free and byte-identical to the input (#2883 acceptance criterion) - otherwise a
    /// grapheme-safe prefix with <paramref name="suffix"/> appended.
    /// </returns>
    public static string? Truncate(string? value, int maxLength, string suffix = "")
    {
        if (value is null)
        {
            return null;
        }

        // A negative maxLength needs no clamp: FindBoundaryAtOrBefore treats any limit <= 0 as an
        // empty retained portion, and this length comparison is false for a negative limit on any
        // string. Clamping as well would be unreachable code that no test could pin.
        if (value.Length <= maxLength)
        {
            return value;
        }

        var cut = FindBoundaryAtOrBefore(value, maxLength);
        return cut == 0 && suffix.Length == 0
            ? string.Empty
            : string.Concat(value.AsSpan(0, cut), suffix);
    }

    /// <summary>
    /// Returns how many UTF-16 code units of <paramref name="value"/> a chunk of at most
    /// <paramref name="maxLength"/> units may take while cutting on a grapheme-cluster boundary,
    /// <b>guaranteeing at least one code unit</b> whenever <paramref name="value"/> is non-empty and
    /// <paramref name="maxLength"/> is positive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the chunking sibling of <see cref="Truncate"/>. Chunking callers (Telegram message
    /// splitting, streaming-buffer draining) advance by the returned length in a loop, so returning
    /// 0 for a cluster wider than <paramref name="maxLength"/> would hang them. Forward progress
    /// therefore outranks cluster integrity in the degenerate case, and the fallback still refuses
    /// to sever a surrogate pair - the correctness floor is never breached, only the stronger
    /// grapheme guarantee, and only when the limit physically cannot hold one cluster.
    /// </para>
    /// <para>
    /// Real limits make the fallback unreachable in practice: Telegram's are 4096 / 32768 code
    /// units and no grapheme cluster approaches that.
    /// </para>
    /// </remarks>
    public static int FindChunkLength(ReadOnlySpan<char> value, int maxLength)
    {
        if (value.IsEmpty || maxLength <= 0)
        {
            return 0;
        }

        if (maxLength >= value.Length)
        {
            return value.Length;
        }

        var boundary = FindBoundaryAtOrBefore(value, maxLength);
        if (boundary > 0)
        {
            return boundary;
        }

        // The first cluster alone exceeds maxLength. Take maxLength units but never end on a lone
        // high surrogate - this is the ONLY surrogate back-off in the product (#2924 criterion 1).
        var length = maxLength;
        if (length > 1 && char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }

        return length;
    }
}
