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
/// <b>Why the clip fails closed</b> (issue #2520): the redactor previously bailed out and returned
/// the input unchanged whenever the markers looked malformed (unbalanced counts, an END before a
/// BEGIN, a nested BEGIN). That was fail-open: any marker-shaped text reaching assistant output -
/// echoed from a user message, an untrusted issue body or a fetched web page - unbalanced the
/// counts and suppressed the strip, emitting the real envelope verbatim. The scan is now
/// unconditional: every BEGIN consumes up to the next END, and an unterminated BEGIN strips to
/// end-of-text. Text containing no BEGIN at all (including a lone stray END) is byte-identical, so
/// ordinary prose that merely mentions the END marker is never clipped.
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
    /// Removes every <see cref="BeginDelimiter"/> ... <see cref="EndDelimiter"/> region from
    /// <paramref name="text"/>, failing closed. Each BEGIN consumes through the next END (so a
    /// nested or repeated BEGIN is swallowed rather than trusted), and a BEGIN with no following
    /// END strips to end-of-text. Input with no BEGIN is returned byte-identical.
    /// </summary>
    /// <param name="text">The outbound assistant text about to be delivered to a channel.</param>
    /// <returns>The text with the runtime-context envelope(s) removed.</returns>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? Strip(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        // Fast path: overwhelmingly the common case - nothing to do, no allocation.
        var firstBegin = text.IndexOf(BeginDelimiter, StringComparison.Ordinal);
        if (firstBegin < 0)
            return text;

        var builder = new StringBuilder(text.Length);
        var cursor = 0;

        while (true)
        {
            var begin = text.IndexOf(BeginDelimiter, cursor, StringComparison.Ordinal);
            if (begin < 0)
                break;

            builder.Append(text, cursor, begin - cursor);

            var afterBegin = begin + BeginDelimiter.Length;
            var end = text.IndexOf(EndDelimiter, afterBegin, StringComparison.Ordinal);
            if (end < 0)
            {
                // Fail closed: an unterminated BEGIN could still be followed by envelope content,
                // so discard the remainder rather than emit it. A nested/repeated BEGIN before the
                // END needs no special case - the scan simply consumes through the next END.
                cursor = text.Length;
                break;
            }

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
}
