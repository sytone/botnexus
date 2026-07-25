using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace BotNexus.Gateway.Channels;

/// <summary>
/// Removes the delimited internal runtime-context envelope from outbound text before it is
/// projected onto a user-visible channel surface (issue #1430).
/// </summary>
/// <remarks>
/// <para>
/// The gateway injects a runtime-context block into the system prompt bracketed by the
/// <c>INTERNAL_RUNTIME_CONTEXT_BEGIN</c> / <c>INTERNAL_RUNTIME_CONTEXT_END</c> delimiters
/// (emitted by <c>BotNexus.Gateway.Prompts.RuntimeLineFormatter</c>, added in #1369). A model that
/// echoes that block back would otherwise leak host, session and provider details to the end user.
/// </para>
/// <para>
/// <b>Why this lives at the channel projection seam</b> rather than in the agent loop or the
/// transcript writer: the strip is deliberately <i>per-channel</i>. Internal, agent-to-agent and
/// session-transcript surfaces legitimately keep the block for debugging and self-diagnosis, so
/// only adapters that opt in via <see cref="ChannelAdapterBase.StripsRuntimeContext"/> redact.
/// </para>
/// <para>
/// <b>Why the clip is guarded</b>: mutating outbound content is risky. The redactor only removes
/// text when it sees well-formed, balanced, non-nested BEGIN/END pairs. Absent, unbalanced,
/// out-of-order or nested delimiters leave the input byte-identical - a user who legitimately
/// asks the agent to quote a partial marker is never silently clipped.
/// </para>
/// </remarks>
public static class RuntimeContextRedactor
{
    /// <summary>
    /// Delimiter marking the start of the internal runtime-context envelope. Mirrors
    /// <c>RuntimeLineFormatter.RuntimeContextBeginDelimiter</c>; the literals are duplicated (and
    /// pinned by a test) so the channel layer need not depend on the prompt-building project.
    /// </summary>
    public const string BeginDelimiter = "INTERNAL_RUNTIME_CONTEXT_BEGIN";

    /// <summary>
    /// Delimiter marking the end of the internal runtime-context envelope. Mirrors
    /// <c>RuntimeLineFormatter.RuntimeContextEndDelimiter</c>.
    /// </summary>
    public const string EndDelimiter = "INTERNAL_RUNTIME_CONTEXT_END";

    /// <summary>
    /// Removes every balanced <see cref="BeginDelimiter"/> ... <see cref="EndDelimiter"/> block from
    /// <paramref name="text"/>. Returns the input unchanged when there is nothing safe to strip
    /// (no delimiters, unbalanced counts, an END before its BEGIN, or a nested BEGIN).
    /// </summary>
    /// <param name="text">The outbound assistant text about to be delivered to a channel.</param>
    /// <returns>The text with the runtime-context envelope(s) removed, or the original input.</returns>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? Strip(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        // Fast path: overwhelmingly the common case - nothing to do, no allocation.
        var firstBegin = text.IndexOf(BeginDelimiter, StringComparison.Ordinal);
        if (firstBegin < 0)
            return text;

        // Guard: a stray END ahead of the first BEGIN means the markers are not a well-formed
        // envelope (e.g. a user quoting fragments). Leave the content alone.
        var firstEnd = text.IndexOf(EndDelimiter, StringComparison.Ordinal);
        if (firstEnd < 0 || firstEnd < firstBegin)
            return text;

        // Guard: marker counts must balance before any mutation happens.
        if (CountOccurrences(text, BeginDelimiter) != CountOccurrences(text, EndDelimiter))
            return text;

        var builder = new StringBuilder(text.Length);
        var cursor = 0;

        while (true)
        {
            var begin = text.IndexOf(BeginDelimiter, cursor, StringComparison.Ordinal);
            if (begin < 0)
                break;

            var afterBegin = begin + BeginDelimiter.Length;
            var end = text.IndexOf(EndDelimiter, afterBegin, StringComparison.Ordinal);
            if (end < 0)
                return text; // Unbalanced in sequence - abort without mutating.

            // Guard: a nested/repeated BEGIN before the END means the envelope is malformed.
            var nested = text.IndexOf(BeginDelimiter, afterBegin, StringComparison.Ordinal);
            if (nested >= 0 && nested < end)
                return text;

            builder.Append(text, cursor, begin - cursor);
            cursor = end + EndDelimiter.Length;

            // Consume the single line break that terminated the END marker line so removing a
            // whole-line envelope does not leave a dangling blank line behind.
            if (cursor < text.Length && text[cursor] == '\r')
                cursor++;
            if (cursor < text.Length && text[cursor] == '\n')
                cursor++;
        }

        builder.Append(text, cursor, text.Length - cursor);
        return builder.ToString();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
