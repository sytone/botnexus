using System.Text;

namespace BotNexus.Gateway.Api;

/// <summary>
/// The single definition of "safe to put in a gateway log message" for request-derived values.
/// </summary>
/// <remarks>
/// <para>
/// <c>HttpRequest.Path</c> and <c>HttpRequest.Method</c> are entirely caller-controlled. A path
/// containing CR/LF forges an additional record in any sink that renders structured properties
/// to plain text, and the sites that matter most are the unauthenticated and unauthorised
/// branches of <see cref="GatewayAuthMiddleware"/> - the caller there is hostile by
/// construction, needs no credentials to reach the 401 path, and the resulting lines are
/// exactly what a security reviewer reads to establish what was attempted (issue #3260).
/// </para>
/// <para>
/// <b>Why a seam rather than a fix at the flagged lines.</b> CodeQL named two new lines in
/// PR #3259, but the pre-existing line in the same middleware had the identical shape. Fixing
/// only the named lines produces two conventions for one hazard in one file, which is how
/// #3151 arose out of #2668's narrow scoping. Correctness here cannot depend on each future
/// author remembering; it has to be the only spelling available. The architecture fence
/// <c>GatewayRequestLogSanitisationFenceArchitectureTests</c> is what makes that true.
/// </para>
/// <para>
/// <b>Escape, do not strip.</b> This mirrors the ROLE of <c>CliText.SafeDisplay</c> - one
/// helper, swept over every site - but deliberately not its MECHANISM. The CLI renders to a
/// terminal, where a control character is an instruction and deleting it is the right answer.
/// A log is evidence: an operator investigating an attack needs to know a CR was sent, so the
/// control is rendered inert as a printable escape (<c>\r</c>, <c>\u0000</c>) rather than
/// silently discarded. Deleting it would destroy the very fact the audit line exists to
/// record. That difference is also why the CLI helper is mirrored rather than referenced -
/// <c>CliText</c> is <c>internal</c> to a different assembly, and it applies Spectre markup
/// escaping that is meaningless (and mangling) in a log sink.
/// </para>
/// <para>
/// Escaping <c>ESC</c> (0x1B) neutralises every ANSI/OSC/DCS sequence by construction: no
/// sequence survives the loss of its introducer, so there is no sequence grammar to enumerate
/// and no new sequence type can slip past. The C1 range (0x80-0x9F) is escaped for the same
/// reason - <c>0x9B</c> is a single-byte CSI and <c>0x85</c> is a line break some sinks honour.
/// </para>
/// <para>
/// Anything that is not a control character passes through byte-for-byte, so legitimate paths
/// - including percent-encoding, query strings and non-ASCII segments - are unchanged and stay
/// greppable (#3260 clause 4).
/// </para>
/// </remarks>
public static class RequestLogText
{
    /// <summary>
    /// Renders an untrusted request-derived value inert for inclusion in a log message.
    /// Control characters become printable escapes; every other character is preserved exactly.
    /// <see langword="null"/> becomes <see cref="string.Empty"/>.
    /// </summary>
    /// <param name="value">The caller-controlled value, typically a path or an HTTP method.</param>
    /// <returns>A single-line, control-character-free rendering of <paramref name="value"/>.</returns>
    public static string Safe(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Overwhelmingly the common case: a legitimate path with nothing to escape, which skips
        // the builder entirely. It still flows through NeutraliseLineBreaks below, so there is
        // exactly ONE exit from this method and no branch can bypass the barrier.
        if (!ContainsControl(value))
        {
            return NeutraliseLineBreaks(value);
        }

        var builder = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (IsControl(ch))
                    {
                        builder.Append("\\u").Append(((int)ch).ToString("X4"));
                    }
                    else
                    {
                        builder.Append(ch);
                    }

                    break;
            }
        }

        return NeutraliseLineBreaks(builder.ToString());
    }

    /// <summary>
    /// The final barrier every value leaves <see cref="Safe"/> through: the two characters that
    /// actually terminate a log record are replaced with their printable spellings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// By the time the escape loop has run these are already no-ops - a <c>\r</c> became the two
    /// characters <c>\</c> and <c>r</c> - and on the control-free fast path there is nothing to
    /// match either, so <c>string.Replace</c> returns the same instance and the hot path stays
    /// allocation-free. It is retained deliberately rather than folded away.
    /// </para>
    /// <para>
    /// <b>Why it is kept.</b> Two reasons, one for machines and one for humans. CodeQL's
    /// log-forging query recognises <c>string.Replace</c> as a sanitising barrier but cannot see
    /// through a hand-written <c>StringBuilder</c> loop, so without this the seam is invisible to
    /// the very analysis that raised #3260 and every future call site would re-alert (the honest
    /// remedy for a true positive is to make the barrier real and visible, not to dismiss the
    /// alert). And it makes the single load-bearing property - CR and LF cannot survive this
    /// method - checkable in one line, independent of the loop above being correct.
    /// </para>
    /// </remarks>
    private static string NeutraliseLineBreaks(string value)
        => value.Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);

    /// <summary>
    /// Renders an untrusted request path for logging, substituting <c>/</c> when the path is
    /// absent.
    /// </summary>
    /// <remarks>
    /// The call sites used to each spell their own <c>?? "/"</c> or <c>?? string.Empty</c>.
    /// Folding that into the seam is what stops them drifting apart again.
    /// </remarks>
    /// <param name="path">The caller-controlled request path.</param>
    /// <returns>A sanitised path, or <c>/</c> when none was supplied.</returns>
    public static string SafePath(string? path)
        => string.IsNullOrEmpty(path) ? "/" : Safe(path);

    /// <summary>
    /// C0 controls, DEL, and the C1 range - the characters that can terminate a log record or
    /// drive a terminal. <c>char.IsControl</c> covers exactly this set.
    /// </summary>
    private static bool IsControl(char ch) => char.IsControl(ch);

    private static bool ContainsControl(string value)
    {
        foreach (var ch in value)
        {
            if (IsControl(ch))
            {
                return true;
            }
        }

        return false;
    }
}
