using System.Text.RegularExpressions;

namespace BotNexus.Gateway.Channels;

/// <summary>
/// Utility for sanitising outbound assistant text before delivery to channels.
/// Strips thinking/reasoning blocks that some LLM providers embed as XML-tagged
/// markup in the text stream when extended thinking is enabled.
/// </summary>
/// <remarks>
/// Providers that return structured thinking content via dedicated stream event types
/// (e.g. Anthropic's <c>thinking_delta</c> SSE event) do not need this sanitizer —
/// the gateway routes those via <c>AgentStreamEventType.ThinkingDelta</c> and the
/// channel adapter decides whether to display them based on
/// <see cref="BotNexus.Gateway.Abstractions.Channels.IChannelAdapter.SupportsThinkingDisplay"/>.
/// <para>
/// This sanitizer addresses the defensive case where a provider returns thinking
/// as raw embedded XML tags inside the text content (e.g. some reasoning models):
/// <c>&lt;thinking&gt;...&lt;/thinking&gt;</c> or
/// <c>&lt;antml:thinking&gt;...&lt;/antml:thinking&gt;</c>.
/// </para>
/// </remarks>
public static class AssistantTextSanitizer
{
    // Matches <thinking>...</thinking> and <thinking>...</thinking> (case-insensitive,
    // dotall so newlines inside the block are matched).
    private static readonly Regex ThinkingTagPattern = new(
        @"<(?:antml:)?thinking>.*?</(?:antml:)?thinking>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Strips any embedded thinking/reasoning XML tags from assistant text.
    /// Returns the input unchanged if no such tags are present.
    /// </summary>
    /// <param name="text">The raw assistant text that may contain thinking blocks.</param>
    /// <returns>The text with all embedded thinking blocks removed and excess whitespace trimmed.</returns>
    public static string StripThinkingTags(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        // Fast-path: avoid regex overhead if no opening tag present.
        if (!text.Contains("<thinking>", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("<thinking>", StringComparison.OrdinalIgnoreCase))
            return text;

        var stripped = ThinkingTagPattern.Replace(text, string.Empty);

        // Collapse multiple consecutive blank lines left after tag removal.
        stripped = Regex.Replace(stripped, @"\n{3}", "\n\n");

        return stripped.Trim();
    }
}
