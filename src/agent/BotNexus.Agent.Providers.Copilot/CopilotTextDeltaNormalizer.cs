namespace BotNexus.Agent.Providers.Copilot;

/// <summary>
/// Removes the confirmed Copilot gpt-5.6 per-delta CRLF transport prefix before provider
/// parsers accumulate text, while leaving every semantic character after that prefix untouched.
/// </summary>
internal static class CopilotTextDeltaNormalizer
{
    /// <summary>
    /// Normalizes one text delta at the Copilot transport boundary so all supported wire
    /// protocols expose the same canonical content to the agent loop.
    /// </summary>
    internal static string Normalize(string modelId, string delta)
        => modelId.StartsWith("gpt-5.6", StringComparison.OrdinalIgnoreCase)
            && delta.StartsWith("\r\n", StringComparison.Ordinal)
                ? delta[2..]
                : delta;
}
