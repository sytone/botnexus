using System.Globalization;

namespace BotNexus.Domain.Text;

/// <summary>
/// Length-limited truncation of arbitrary user-, model- and command-supplied text.
/// </summary>
/// <remarks>
/// <para>
/// This exists because .NET strings are UTF-16 and the codebase treated "characters" and code
/// units as interchangeable. Slicing with <c>value[..max]</c> can cut between a high and a low
/// surrogate, splitting an astral-plane character (emoji, CJK extension, mathematical
/// alphanumerics) into a lone surrogate. That renders as U+FFFD at best; at worst it is
/// <em>persisted</em> - a conversation title stored with a lone surrogate cannot be repaired
/// afterwards, because the other half of the pair is gone. See #2883.
/// </para>
/// <para>
/// The cut is made on a text-element (grapheme cluster) boundary rather than merely a surrogate
/// boundary. Not splitting a surrogate is the correctness bar; respecting grapheme clusters is
/// strictly stronger and additionally keeps combining marks, regional-indicator flag pairs and
/// ZWJ emoji sequences intact, so a truncated string never renders as a mangled half-glyph.
/// </para>
/// <para>
/// This sits beside <see cref="ExternalText"/> because both are length-bounding policies for
/// externally supplied text, and keeping them adjacent is what stops a third variant appearing.
/// The two are complementary, not alternatives: <see cref="ExternalText.Sanitize"/> flattens text
/// to a single control-character-free line for display, whereas this preserves the content
/// verbatim and only bounds its length.
/// </para>
/// </remarks>
public static class TextTruncation
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
    public static string? SafeTruncate(string? value, int maxLength, string suffix = "")
    {
        if (value is null)
        {
            return null;
        }

        // Returning the original reference (not a copy) keeps the ASCII path allocation-free and
        // byte-identical to the raw slicing it replaces - see acceptance criterion 4 on #2883.
        // A negative maxLength needs no clamp here: FindBoundaryAtOrBefore treats any limit <= 0
        // as an empty retained portion, and the length comparison below is false for a negative
        // limit on any string. Clamping as well would be unreachable code that no test can pin.
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
    /// Returns the largest index at or before <paramref name="limit"/> that does not fall inside a
    /// surrogate pair or a grapheme cluster.
    /// </summary>
    /// <remarks>
    /// Enumerating text elements from the start is O(limit) rather than O(value.Length), and is the
    /// only way to be correct for clusters whose extent is not decidable by looking at the cut
    /// point alone - a ZWJ emoji sequence is many code units wide and joins are only visible by
    /// walking forward from a known boundary.
    /// </remarks>
    private static int FindBoundaryAtOrBefore(string value, int limit)
    {
        if (limit <= 0)
        {
            return 0;
        }

        var lastBoundary = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            var start = enumerator.ElementIndex;
            if (start >= limit)
            {
                // This cluster begins at or after the limit, so the previous boundary is the answer.
                return start == limit ? limit : lastBoundary;
            }

            var end = start + ((string)enumerator.Current).Length;
            if (end > limit)
            {
                // The limit lands inside this cluster; stop before the cluster starts.
                return start;
            }

            lastBoundary = end;
        }

        return lastBoundary;
    }
}
