using System.Globalization;
using System.Text;
using BotNexus.Agent.Providers.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.Core.Streaming;

/// <summary>
/// Compares text assembled from streamed deltas against the provider's own authoritative final
/// text for the same block, and reports a structured diagnostic when they disagree (#2443).
/// </summary>
/// <remarks>
/// Every streaming protocol we consume hands us a free checksum at block close - the Responses
/// API's <c>response.output_text.done.text</c> and the Messages API's terminal block value - and
/// until now we discarded it. That is why the CRLF corruption family (#2049 -> #2119 -> #2170) took
/// three issues across three transports to diagnose: nothing in the pipeline could tell that the
/// bytes we accumulated were not the bytes the provider sent. This type turns that class of defect
/// into a self-reporting event at the exact seam where it happens.
/// <para>
/// The comparison is deliberately fail-open with respect to content: a mismatch never throws and
/// never truncates a stream. The provider's final text is preferred as canonical because it is the
/// value the provider itself considers complete, so a per-delta transport artifact cannot survive
/// into persisted history.
/// </para>
/// <para>
/// The diagnostic never logs message content. Only lengths, indices, and a bounded, escaped
/// window around the first divergence are emitted, so an assembly bug is diagnosable from logs
/// without turning the log store into a transcript of user conversations.
/// </para>
/// </remarks>
public static class StreamAssemblyConformance
{
    /// <summary>Half-width of the escaped context window emitted around a mismatch.</summary>
    internal const int ContextRadius = 16;

    private static long _mismatchCount;

    /// <summary>
    /// Number of assembly mismatches observed in this process since start. Exposed so a
    /// deployment can answer "does this ever actually fire?" with a number rather than a belief.
    /// </summary>
    public static long MismatchCount => Interlocked.Read(ref _mismatchCount);

    /// <summary>
    /// Compares <paramref name="assembled"/> against the provider's <paramref name="finalText"/>
    /// and returns the value that should be treated as canonical for the block.
    /// </summary>
    /// <param name="assembled">Text accumulated from the streamed deltas.</param>
    /// <param name="finalText">
    /// The provider's own final text for the block, or <see langword="null"/> when the protocol did
    /// not supply one. A null or absent final value is not a mismatch - it means there is nothing to
    /// check against - so the assembled text is returned unchanged.
    /// </param>
    /// <param name="provider">Provider identifier, for the diagnostic.</param>
    /// <param name="modelId">Model identifier, for the diagnostic.</param>
    /// <param name="api">Api identifier, for the diagnostic.</param>
    /// <param name="transport">Transport identifier (e.g. <c>sse</c>), for the diagnostic.</param>
    /// <param name="deltaCount">Number of deltas that contributed to <paramref name="assembled"/>.</param>
    /// <param name="logger">Optional logger; a null logger suppresses the diagnostic only, not the reconciliation.</param>
    /// <param name="stream">
    /// Optional stream to report the mismatch on as a non-terminal <see cref="WarningEvent"/> (#3291).
    /// A log line is not a contract: until this existed, the one detector built for the CRLF
    /// corruption family could not get its finding out of the provider layer, so the consumer that
    /// had just rendered the wrong deltas to a user was never told. Optional so existing callers and
    /// unit tests that only want the reconciliation are unaffected.
    /// </param>
    /// <param name="buildPartial">
    /// Optional factory for the partial message carried on the warning. Only invoked on the mismatch
    /// path, so a caller pays nothing for a clean block. Ignored when <paramref name="stream"/> is null.
    /// </param>
    /// <returns>
    /// <paramref name="finalText"/> when it is present and differs from <paramref name="assembled"/>;
    /// otherwise <paramref name="assembled"/>.
    /// </returns>
    public static string Reconcile(
        string assembled,
        string? finalText,
        string provider,
        string modelId,
        string api,
        string transport,
        int deltaCount,
        ILogger? logger,
        LlmStream? stream = null,
        Func<AssistantMessage>? buildPartial = null)
    {
        ArgumentNullException.ThrowIfNull(assembled);

        if (finalText is null)
            return assembled;

        if (string.Equals(assembled, finalText, StringComparison.Ordinal))
            return assembled;

        Interlocked.Increment(ref _mismatchCount);

        var firstMismatchIndex = FirstMismatchIndex(assembled, finalText);

        logger?.LogWarning(
            "Stream assembly mismatch at provider seam: provider={Provider} model={ModelId} api={Api} " +
            "transport={Transport} deltaCount={DeltaCount} assembledLength={AssembledLength} " +
            "finalLength={FinalLength} firstMismatchIndex={FirstMismatchIndex} " +
            "assembledContext={AssembledContext} finalContext={FinalContext}. " +
            "Preferring the provider's final text as canonical.",
            provider,
            modelId,
            api,
            transport,
            deltaCount,
            assembled.Length,
            finalText.Length,
            firstMismatchIndex,
            Context(assembled, firstMismatchIndex),
            Context(finalText, firstMismatchIndex));

        if (stream is not null && buildPartial is not null)
        {
            // Deliberately narrower than the log line: the escaped context windows above are the one
            // place content-derived characters appear, and this string leaves the provider layer for
            // consumers and persisted transcripts. Lengths, indices and identifiers only (#3291).
            stream.Push(new WarningEvent(
                WarningCodes.StreamAssemblyMismatch,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Stream assembly mismatch at provider seam: provider={provider} model={modelId} " +
                    $"api={api} transport={transport} deltaCount={deltaCount} " +
                    $"assembledLength={assembled.Length} finalLength={finalText.Length} " +
                    $"firstMismatchIndex={firstMismatchIndex}. " +
                    $"Preferring the provider's final text as canonical."),
                buildPartial()));
        }

        return finalText;
    }

    /// <summary>
    /// Index of the first differing UTF-16 code unit, or the length of the shorter string when one
    /// is a strict prefix of the other.
    /// </summary>
    internal static int FirstMismatchIndex(string left, string right)
    {
        var limit = Math.Min(left.Length, right.Length);
        for (var i = 0; i < limit; i++)
        {
            if (left[i] != right[i])
                return i;
        }

        return limit;
    }

    /// <summary>
    /// Builds a bounded, escaped window of <paramref name="value"/> centred on
    /// <paramref name="index"/>. Control characters are rendered as escapes precisely because the
    /// bugs this diagnoses are invisible ones - a raw log line cannot distinguish CR from LF.
    /// </summary>
    internal static string Context(string value, int index)
    {
        if (value.Length == 0)
            return "";

        var start = Math.Max(0, index - ContextRadius);
        var end = Math.Min(value.Length, index + ContextRadius);
        var builder = new StringBuilder((end - start) * 2);

        for (var i = start; i < end; i++)
            Escape(builder, value[i]);

        return builder.ToString();
    }

    private static void Escape(StringBuilder builder, char c)
    {
        switch (c)
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
            case '\\':
                builder.Append("\\\\");
                break;
            default:
                if (char.IsControl(c))
                    builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
                else
                    builder.Append(c);
                break;
        }
    }
}
