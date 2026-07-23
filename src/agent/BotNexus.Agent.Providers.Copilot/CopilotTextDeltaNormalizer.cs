namespace BotNexus.Agent.Providers.Copilot;

/// <summary>
/// Removes the confirmed Copilot gpt-5.6 per-delta CRLF transport prefix before provider
/// parsers accumulate text, while leaving every semantic character after that prefix untouched.
/// </summary>
internal static class CopilotTextDeltaNormalizer
{
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

        return offset == 0 ? delta : delta[offset..];
    }
}
