namespace BotNexus.Agent.Providers.Copilot;

/// <summary>
/// The single implementation of Copilot text-delta transport normalization, applied identically by
/// every Copilot transport - Responses, Messages and Completions (#2443). Removes the confirmed
/// Copilot gpt-5.6 per-delta CRLF transport prefix before provider parsers accumulate text, while
/// leaving every semantic character after that prefix untouched.
/// </summary>
/// <remarks>
/// Previously this ran on Responses and Messages only. That asymmetry is precisely how #2170
/// happened: #2049 fixed Responses, model discovery then selected <c>/v1/messages</c>, and the same
/// artifact came straight back on the unnormalized transport. A third unnormalized transport is a
/// third recurrence waiting to happen, so the Completions path is wired to this same method rather
/// than growing its own copy.
/// </remarks>
internal static class CopilotTextDeltaNormalizer
{
    private static long _hitCount;

    /// <summary>
    /// Number of deltas this normalizer has actually modified since process start. This exists to
    /// make the normalizer's premise falsifiable in production: the CRLF framing it strips is not
    /// reproducible in any captured traffic (#2443), so if this counter stays at zero against live
    /// traffic we have earned the right to delete a lossy transform, and if it moves we finally get
    /// the model/transport correlation the original three issues never had.
    /// </summary>
    internal static long HitCount => Interlocked.Read(ref _hitCount);

    /// <summary>
    /// Normalizes one text delta at the Copilot transport boundary so all supported wire
    /// protocols (SSE and the capability-aware WebSocket path) expose the same canonical
    /// content to the agent loop.
    /// </summary>
    /// <remarks>
    /// The Copilot Responses endpoint frames gpt-5.6 text deltas with a CRLF transport
    /// prefix. The original SSE-era fix (#2052) stripped a single leading <c>\r\n</c>, but
    /// the capability-aware WebSocket transport (#2082) surfaced fragments where gpt-5.6-sol
    /// prefixes <em>every</em> token with framing - sometimes more than one pair - which
    /// persisted as one-token-per-line output (#2119). We therefore strip <em>all</em>
    /// leading <c>\r\n</c> pairs. This is safe because genuine Markdown boundaries emitted by
    /// the model arrive as bare <c>\n</c> (LF) characters, never as CRLF, so real newlines,
    /// lists, paragraphs, and code blocks are preserved verbatim.
    /// </remarks>
    internal static string Normalize(string modelId, string delta)
    {
        if (!modelId.StartsWith("gpt-5.6", StringComparison.OrdinalIgnoreCase))
            return delta;

        var offset = 0;
        while (delta.AsSpan(offset).StartsWith("\r\n", StringComparison.Ordinal))
            offset += 2;

        return offset == 0 ? delta : Hit(delta[offset..]);
    }

    private static string Hit(string stripped)
    {
        Interlocked.Increment(ref _hitCount);
        return stripped;
    }
}
