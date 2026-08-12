using System.Text;

using BotNexus.Gateway.Abstractions.Text;

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
    /// most <paramref name="maxLength"/> UTF-16 code units long and never ends inside a grapheme
    /// cluster - so it can sever neither a surrogate pair nor a ZWJ emoji sequence, a flag pair or a
    /// combining mark. A severed pair serializes to invalid UTF-16 and Telegram rejects the message
    /// with <c>400 can't parse entities</c>. Callers must advance by the returned slice's length, not
    /// by a fixed <paramref name="maxLength"/>, so the deferred code units are not skipped.
    /// </summary>
    /// <remarks>
    /// #2924: the boundary calculation is <see cref="GraphemeSafeTruncation.FindChunkLength"/>, the
    /// single product-wide policy shared with the gateway domain and the Blazor portal. It always
    /// returns at least one code unit while characters remain, so the chunking loop above cannot
    /// stall; the previous local implementation guaranteed only the weaker surrogate-pair property.
    /// </remarks>
    public static string SliceSurrogateSafe(string content, int offset, int maxLength)
    {
        var length = GraphemeSafeTruncation.FindChunkLength(
            content.AsSpan(offset),
            maxLength);

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
    /// rejects. This shares the exact boundary policy of <see cref="SliceSurrogateSafe"/> (#2924):
    /// the cut moves back to a grapheme-cluster boundary so the whole cluster travels into the next
    /// chunk. The buffer is advanced by the actual chunk length, so the deferred code units are never
    /// skipped, and at least one code unit is always drained (forward progress).
    /// </remarks>
    public static string DrainStreamingBuffer(StringBuilder buffer, int maxLength)
    {
        // The boundary walk must be able to see whether the cluster at the cut extends PAST
        // maxLength, so the window handed to it cannot be capped at maxLength - doing that would
        // make every cut look cluster-aligned and silently restore the severing bug. StringBuilder
        // cannot be spanned, so the pending text is materialised once; it is bounded by the
        // Telegram message limits (4096 / 32768 code units) that drive this drain.
        var pending = buffer.ToString();
        var length = GraphemeSafeTruncation.FindChunkLength(pending, maxLength);

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
