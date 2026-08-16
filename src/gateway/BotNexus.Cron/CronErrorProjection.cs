namespace BotNexus.Cron;

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using BotNexus.Domain.Text;

/// <summary>
/// #3209: projects a thrown exception into the text that is safe to <b>persist</b> in cron run
/// history and to <b>deliver</b> in a failure alert.
/// </summary>
/// <remarks>
/// <para>
/// The error path used to hand <c>ex.ToString()</c> to <c>FinalizeRunAsync</c> and to
/// <c>RecordAlertDeliveryFailureAsync</c>. On .NET that string is the exception's <b>type full
/// name</b>, its message, and the <b>complete stack trace</b> - namespaces, class names, method
/// signatures, and on a debug-symbol build the absolute source paths of the machine that built the
/// binary. Cron run history is durable (<c>cron.sqlite</c>), is rendered by <c>cron history</c>,
/// and since #2557/#3161 has a delivery route into a conversation an agent reads. None of those
/// consumers need internal type identity or build layout, so retaining it is information
/// disclosure with no diagnostic payoff.
/// </para>
/// <para>
/// What operators actually use is the message, so the projection keeps the message <b>chain</b>
/// (outer plus each inner cause, which is where the real root cause usually lives) and drops
/// everything else. Nothing is lost for a developer: <c>_logger.LogError(ex, ...)</c> still writes
/// the whole exception, stack trace included, to the structured log.
/// </para>
/// <para>
/// The stack-frame scrub is deliberately applied to the message text as well. A message is not
/// guaranteed trace-free - a wrapper exception that formatted an inner <c>ToString()</c> into its
/// own message would smuggle the trace straight back through a projection that only skipped
/// <c>StackTrace</c>. Scrubbing the OUTPUT rather than trusting the INPUT makes the guarantee
/// structural rather than conventional.
/// </para>
/// </remarks>
internal static partial class CronErrorProjection
{
    /// <summary>
    /// Upper bound on the projected text. A pathological message chain must not become an
    /// unbounded write into a durable store; the cap keeps run history rows readable.
    /// </summary>
    internal const int MaxProjectedLength = 2000;

    /// <summary>Separator between the outer message and each successive inner cause.</summary>
    internal const string CauseSeparator = " -> ";

    /// <summary>Maximum number of inner causes walked, so a cyclic-ish chain cannot spin.</summary>
    private const int MaxCauseDepth = 5;

    /// <summary>
    /// A managed stack frame line: leading whitespace, <c>at </c>, then the frame. Matches the
    /// canonical <c>"   at Ns.Type.Method()"</c> shape that <see cref="Exception.ToString"/> emits.
    /// </summary>
    [GeneratedRegex(@"(?m)^[ \t]*at [^\r\n]*(\r?\n|$)", RegexOptions.CultureInvariant)]
    private static partial Regex StackFrameLine();

    /// <summary>
    /// Projects <paramref name="ex"/> into persistable text: the message chain, with no exception
    /// type name and no stack frames.
    /// </summary>
    /// <param name="ex">The thrown exception, or <c>null</c>.</param>
    /// <returns>The projected text, or <c>null</c> when there is nothing to record.</returns>
    internal static string? Project(Exception? ex)
    {
        if (ex is null)
            return null;

        var parts = new List<string>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);

        for (var current = ex; current is not null && parts.Count <= MaxCauseDepth; current = current.InnerException)
        {
            if (!seen.Add(current))
                break;

            var message = Sanitize(current.Message);
            // An empty projected message would otherwise contribute a bare separator. Skipping it
            // keeps the chain readable when a frame carries no message of its own.
            if (!string.IsNullOrWhiteSpace(message) && !parts.Contains(message, StringComparer.Ordinal))
                parts.Add(message);
        }

        if (parts.Count == 0)
            return null;

        // Surrogate-safe truncation via the shared helper (#3171/#3187): a raw range slice can cut
        // a surrogate pair in half and deposit a lone surrogate into a durable sqlite column.
        return TextTruncation.SafeTruncate(string.Join(CauseSeparator, parts), MaxProjectedLength);
    }

    /// <summary>
    /// Removes stack-frame lines from <paramref name="text"/> and collapses the remaining
    /// whitespace onto a single line, so a multi-line message cannot masquerade as trace output.
    /// </summary>
    private static string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var withoutFrames = StackFrameLine().Replace(text, string.Empty);
        return string.Join(' ', withoutFrames.Split(
            ['\r', '\n', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim();
    }
}
