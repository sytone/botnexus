using System.Diagnostics.CodeAnalysis;
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

    // Leaked tool-call XML: some models (notably opus via github-copilot) serialise a tool call as
    // Anthropic <invoke>/<tool_use> markup inside the assistant TEXT channel instead of a structured
    // tool_use block (issue #1698). Mirror the proven MemoryContentSanitizer filters so raw XML never
    // reaches a channel. Block form first (removes nested <parameter> JSON), then stray open/close tags
    // including bare <parameter>.
    private static readonly Regex ToolCallBlockPattern = new(
        @"<(?:tool_call|function_calls|invoke|tool_use)\b[^>]*>.*?</(?:tool_call|function_calls|invoke|tool_use)>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ToolCallStrayTagPattern = new(
        @"</?(?:tool_call|function_calls|invoke|tool_use|parameter)\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Junk 'court' token observed immediately before the leaked <invoke> XML; strip only when adjacent
    // to a tool tag so ordinary prose containing "court" survives.
    private static readonly Regex CourtJunkPrefixPattern = new(
        @"\bcourt(?=\s*<(?:invoke|tool_use|tool_call|function_calls)\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="text"/> contains only thinking/reasoning
    /// block(s) with no user-visible content outside them.
    /// A response that strips to empty or whitespace-only is considered thinking-only.
    /// </summary>
    /// <param name="text">The raw assistant text to inspect.</param>
    /// <returns>
    /// <see langword="true"/> when the text is non-empty, contains at least one thinking block,
    /// and produces no user-visible content after stripping those blocks.
    /// </returns>
    public static bool IsThinkingOnlyResponse(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        // Must contain at least one thinking block; otherwise it is simply an empty response.
        if (!text.Contains("<thinking>", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("<thinking>", StringComparison.OrdinalIgnoreCase))
            return false;

        return string.IsNullOrWhiteSpace(StripThinkingTags(text));
    }

    /// <summary>
    /// Strips thinking blocks AND leaked tool-call XML (invoke/tool_use/function_calls/bare parameter)
    /// plus the junk "court" prefix opus-copilot emits ahead of the XML (issue #1698). Input returned
    /// unchanged when clean; allocation-free fast path.
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        if (text.IndexOf('<') < 0)
            return text;
        var stripped = ThinkingTagPattern.Replace(text, string.Empty);
        stripped = CourtJunkPrefixPattern.Replace(stripped, string.Empty);
        stripped = ToolCallBlockPattern.Replace(stripped, string.Empty);
        stripped = ToolCallStrayTagPattern.Replace(stripped, string.Empty);
        stripped = Regex.Replace(stripped, @"\n{3}", "\n\n");
        return stripped.Trim();
    }

    /// <summary>
    /// Strips ONLY leaked tool-call XML (invoke/tool_use/function_calls/bare parameter) and the junk
    /// "court" prefix, preserving thinking blocks. Used on the thinking-display delivery path where
    /// reasoning is intentionally shown but leaked tool markup must never reach the channel (#1698).
    /// Returns input unchanged when clean.
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? StripLeakedToolCalls(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        if (text.IndexOf('<') < 0)
            return text;
        var stripped = CourtJunkPrefixPattern.Replace(text, string.Empty);
        stripped = ToolCallBlockPattern.Replace(stripped, string.Empty);
        stripped = ToolCallStrayTagPattern.Replace(stripped, string.Empty);
        return stripped.Trim();
    }
}
