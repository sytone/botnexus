using System.Text.RegularExpressions;

namespace BotNexus.Providers.Core.Utilities;

/// <summary>
/// Detects model context-window overflow errors from provider-specific error text.
/// Port of pi-mono's utils/overflow.ts.
/// </summary>
public static class ContextOverflowDetector
{
    private static readonly Regex[] OverflowPatterns =
    [
        new("prompt is too long", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("request_too_large", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("input is too long for requested model", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("exceeds the context window", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("input token count.*exceeds the maximum", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("maximum prompt length is \\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("reduce the length of the messages", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("maximum context length is \\d+ tokens", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("exceeds the limit of \\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("exceeds the available context size", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("greater than the context length", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("context window exceeds limit", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("exceeded model token limit", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("too large for model with \\d+ maximum context length", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("model_context_window_exceeded", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("prompt too long; exceeded (?:max )?context length", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("context[_ ]length[_ ]exceeded", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("too many tokens", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("token limit exceeded", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("^4(?:00|13)\\s*(?:status code)?\\s*\\(no body\\)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    private static readonly Regex[] NonOverflowPatterns =
    [
        new("^(Throttling error|Service unavailable):", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("rate limit", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("too many requests", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    public static bool IsContextOverflow(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
            return false;

        if (NonOverflowPatterns.Any(pattern => pattern.IsMatch(errorMessage)))
            return false;

        return OverflowPatterns.Any(pattern => pattern.IsMatch(errorMessage));
    }

    public static bool IsContextOverflow(Exception? ex)
    {
        if (ex is null)
            return false;

        if (IsContextOverflow(ex.Message))
            return true;

        if (ex is AggregateException aggregateException)
        {
            foreach (var inner in aggregateException.Flatten().InnerExceptions)
            {
                if (IsContextOverflow(inner))
                    return true;
            }
        }

        return ex.InnerException is not null && IsContextOverflow(ex.InnerException);
    }
}
