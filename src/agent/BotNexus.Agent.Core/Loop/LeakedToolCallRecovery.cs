using System.Text.RegularExpressions;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Loop;

/// <summary>
/// Recovers tool calls that a model leaked as Anthropic <c>invoke</c>/<c>tool_use</c> XML inside
/// the assistant TEXT channel (issue #1709, Tier 3 of #1698). Some models -- notably opus via
/// github-copilot -- serialise a tool call as markup in text and terminate the turn with a finish
/// reason that is not <see cref="StopReason.ToolUse"/>, so the loop's continuation check never
/// dispatches it. The Tier 1 sanitizer (#1699) only strips that markup before delivery; this
/// parser is its executing complement: it extracts each well-formed block into a real
/// <see cref="ToolCallContent"/> and returns the text with the recovered markup removed.
/// </summary>
/// <remarks>
/// Mirrors the proven sanitizer block shapes (invoke/tool_use/tool_call/function_calls) so the two
/// tiers agree on what counts as leaked markup. Only complete, name-bearing blocks are recovered;
/// malformed fragments (no closing tag, no name) and ordinary prose are left untouched so a real
/// conversation that merely mentions a tag is never mutated and never crashes.
/// </remarks>
public static class LeakedToolCallRecovery
{
    private static readonly Regex InvokeBlockPattern = new(
        @"<(?<tag>tool_call|function_calls|invoke|tool_use)\b[^>]*\bname\s*=\s*[""'](?<name>[^""']+)[""'][^>]*>(?<body>.*?)</\k<tag>>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ParameterPattern = new(
        @"<parameter\b[^>]*\bname\s*=\s*[""'](?<key>[^""']+)[""'][^>]*>(?<val>.*?)</parameter>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// The outcome of a recovery pass: any tool calls extracted from leaked XML plus the assistant
    /// text with the recovered blocks removed.
    /// </summary>
    /// <param name="RecoveredCalls">The tool calls parsed from leaked invoke/tool_use blocks (empty when none).</param>
    /// <param name="CleanedText">The input text with recovered blocks stripped; unchanged when nothing recovered.</param>
    public sealed record Result(IReadOnlyList<ToolCallContent> RecoveredCalls, string CleanedText);

    /// <summary>
    /// Scans <paramref name="text"/> for leaked tool-call XML and recovers each complete,
    /// name-bearing block into a <see cref="ToolCallContent"/> with a synthetic id. Returns the
    /// input unchanged with an empty list when no recoverable block is present.
    /// </summary>
    /// <param name="text">The assistant text that may contain leaked invoke/tool_use markup.</param>
    /// <returns>The recovered calls and the text with those blocks removed.</returns>
    public static Result Recover(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('<') < 0)
        {
            return new Result([], text ?? string.Empty);
        }

        var matches = InvokeBlockPattern.Matches(text);
        if (matches.Count == 0)
        {
            return new Result([], text);
        }

        var calls = new List<ToolCallContent>(matches.Count);
        var index = 0;
        foreach (Match block in matches)
        {
            var name = block.Groups["name"].Value;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (Match parameter in ParameterPattern.Matches(block.Groups["body"].Value))
            {
                arguments[parameter.Groups["key"].Value] = Coerce(parameter.Groups["val"].Value);
            }

            calls.Add(new ToolCallContent($"leaked-{index}", name, arguments));
            index++;
        }

        if (calls.Count == 0)
        {
            return new Result([], text);
        }

        var cleaned = InvokeBlockPattern.Replace(text, string.Empty).Trim();
        return new Result(calls, cleaned);
    }

    private static object? Coerce(string raw)
    {
        var value = raw.Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }
        if (bool.TryParse(value, out var boolean))
        {
            return boolean;
        }
        if (long.TryParse(value, out var integer))
        {
            return integer;
        }
        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }
        return value;
    }
}
