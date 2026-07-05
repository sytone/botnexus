using System.Text;

namespace BotNexus.Extensions.Channels.Telegram;

/// <summary>
/// Surrogate-safe message-splitting helpers for the Telegram channel. Extracted from
/// <see cref="TelegramChannelAdapter"/> so the boundary-aware chunking logic (which must never sever
/// a UTF-16 surrogate pair across a chunk boundary, or Telegram rejects the message with
/// <c>400 can't parse entities</c>) lives in one focused, independently-testable place. Pure and
/// stateless; every method is a deterministic function of its inputs.
/// </summary>
internal static class TelegramMessageSplitter
{
    /// <summary>
    /// Splits plain (non-rich / MarkdownV2 / fallback) outbound text into chunks no longer than
    /// <paramref name="maxLength"/> UTF-16 code units, never severing a surrogate pair across a chunk
    /// boundary (see <see cref="SliceSurrogateSafe"/>). Used by the legacy and fallback send paths.
    /// </summary>
    public static IEnumerable<string> SplitMessage(string content, int maxLength)
    {
        if (string.IsNullOrEmpty(content))
        {
            yield return string.Empty;
            yield break;
        }

        var offset = 0;
        while (offset < content.Length)
        {
            var chunk = SliceSurrogateSafe(content, offset, maxLength);
            yield return chunk;
            offset += chunk.Length;
        }
    }

    /// <summary>
    /// Returns a slice of <paramref name="content"/> starting at <paramref name="offset"/> that is at
    /// most <paramref name="maxLength"/> UTF-16 code units long and never ends in the middle of a
    /// surrogate pair. If taking <paramref name="maxLength"/> code units would split an astral
    /// character (emoji / non-BMP glyph) - i.e. the last code unit is a high surrogate - the slice is
    /// shortened by one so the whole pair moves into the next chunk instead of leaving a lone
    /// surrogate that serializes to invalid UTF-16 (which Telegram rejects with
    /// <c>400 can't parse entities</c>). Callers must advance by the returned slice's length, not by
    /// a fixed <paramref name="maxLength"/>, so the deferred code unit is not skipped.
    /// </summary>
    /// <remarks>
    /// At least one code unit is always returned (assuming <paramref name="maxLength"/> &gt;= 1 and
    /// characters remain) to guarantee forward progress. The only case that can return a slice ending
    /// on a high surrogate is the degenerate <paramref name="maxLength"/> == 1 with a surrogate pair
    /// at <paramref name="offset"/>; a single-code-unit limit cannot physically hold a two-unit pair,
    /// and real Telegram limits (4096 / 32768) never hit this.
    /// </remarks>
    public static string SliceSurrogateSafe(string content, int offset, int maxLength)
    {
        var remaining = content.Length - offset;
        var length = Math.Min(maxLength, remaining);

        // If the slice would end on a high surrogate whose low half lies in the next chunk, back off
        // by one so the pair is not severed. Keep at least one code unit for forward progress.
        if (length > 1 && offset + length < content.Length && char.IsHighSurrogate(content[offset + length - 1]))
        {
            length--;
        }

        return content.Substring(offset, length);
    }

    /// <summary>
    /// Removes and returns a leading chunk of at most <paramref name="maxLength"/> UTF-16 code units
    /// from the front of the streaming <paramref name="buffer"/>, never severing a surrogate pair at
    /// the chunk boundary. Used by the mid-stream MarkdownV2 flush (<see cref="FlushLegacyMarkdownV2Async"/>)
    /// to drain an over-length buffer one message at a time.
    /// </summary>
    /// <remarks>
    /// The previous implementation sliced and removed a fixed <paramref name="maxLength"/> code units
    /// (<c>Buffer.ToString(0, maxLength)</c> + <c>Buffer.Remove(0, maxLength)</c>), which severed an
    /// emoji / astral glyph straddling the boundary into a lone high surrogate (this chunk) and an
    /// orphaned low surrogate (left at the head of the buffer) - both invalid UTF-16 that Telegram
    /// rejects. This shares the same boundary-safe back-off as <see cref="SliceSurrogateSafe"/>: if the
    /// cut would land between a surrogate pair, the cut is shortened by one so the whole pair moves
    /// into the next chunk. The buffer is advanced by the actual chunk length, so the deferred code
    /// unit is never skipped, and at least one code unit is always drained (forward progress).
    /// </remarks>
    public static string DrainStreamingBuffer(StringBuilder buffer, int maxLength)
    {
        var available = Math.Min(maxLength, buffer.Length);
        var length = available;

        // Back off by one if the cut would split a surrogate pair (last code unit is a high surrogate
        // whose low half is still in the buffer). Keep at least one unit so the drain always advances.
        if (length > 1 && length < buffer.Length && char.IsHighSurrogate(buffer[length - 1]))
        {
            length--;
        }

        var chunk = buffer.ToString(0, length);
        buffer.Remove(0, length);
        return chunk;
    }

    /// <summary>
    /// Splits Rich Markdown into chunks at line boundaries so that tables, code blocks, and other
    /// multi-line constructs are not severed mid-line. Lines are accumulated until adding the next
    /// would exceed <paramref name="maxLength"/>; a single line longer than the limit is split as a
    /// last resort. Most replies fit in one chunk (the rich limit is 32768 characters).
    /// </summary>
    public static IEnumerable<string> SplitMarkdown(string content, int maxLength)
    {
        if (string.IsNullOrEmpty(content))
        {
            yield return string.Empty;
            yield break;
        }

        if (content.Length <= maxLength)
        {
            yield return content;
            yield break;
        }

        var builder = new StringBuilder();
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var isLast = i == lines.Length - 1;

            // A single line longer than the limit must be hard-split as a last resort. Slice on
            // surrogate-pair boundaries so an emoji straddling the limit is not severed into a lone
            // surrogate (the same hazard SliceSurrogateSafe guards in SplitMessage).
            if (line.Length > maxLength)
            {
                if (builder.Length > 0)
                {
                    yield return builder.ToString();
                    builder.Clear();
                }

                var offset = 0;
                while (offset < line.Length)
                {
                    var chunk = SliceSurrogateSafe(line, offset, maxLength);
                    yield return chunk;
                    offset += chunk.Length;
                }
                continue;
            }

            // +1 accounts for the '\n' that re-joins this line to the buffer.
            var projected = builder.Length == 0 ? line.Length : builder.Length + 1 + line.Length;
            if (projected > maxLength)
            {
                yield return builder.ToString();
                builder.Clear();
            }

            if (builder.Length > 0)
                builder.Append('\n');
            builder.Append(line);

            if (isLast && builder.Length > 0)
                yield return builder.ToString();
        }
    }
}
