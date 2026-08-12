using System.Text.RegularExpressions;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// The single definition of what makes an operator-supplied redaction pattern acceptable (#2727).
/// </summary>
/// <remarks>
/// Shared deliberately by <see cref="SecretRedactionOptions.Compile"/> (which throws, at startup)
/// and <c>PlatformConfigValidator</c> (which collects a named error per offending pattern). Keeping
/// one implementation is what guarantees the validator never green-lights a pattern the redactor
/// would then refuse to compile - a divergence that would turn a clear config error into a startup
/// crash with no actionable message.
/// </remarks>
public static class SecretRedactionPatternRules
{
    /// <summary>Bound applied while probing a candidate pattern, so validation itself cannot hang.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Returns true and sets <paramref name="error"/> when the pattern is unacceptable.
    /// </summary>
    /// <param name="pattern">The raw operator pattern.</param>
    /// <param name="index">Zero-based position in the configured list, used to make the message actionable.</param>
    /// <param name="error">The operator-facing reason, naming the offending pattern.</param>
    public static bool TryGetError(string? pattern, int index, out string error)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = $"pattern at index {index} is empty or whitespace. Remove it or supply a regular expression.";
            return true;
        }

        Regex candidate;
        try
        {
            candidate = new Regex(pattern, RegexOptions.None, ProbeTimeout);
        }
        catch (ArgumentException ex)
        {
            error = $"pattern at index {index} is not a valid regular expression: '{pattern}' ({ex.Message}).";
            return true;
        }

        // A pattern that matches the empty string matches at every position, so it would replace the
        // entire text with [REDACTED] markers. That is indistinguishable from data loss, and it is a
        // realistic operator typo (".*", "a?"), so it is rejected rather than merely warned about.
        try
        {
            if (candidate.IsMatch(string.Empty))
            {
                error =
                    $"pattern at index {index} matches the empty string and would redact all text: '{pattern}'. " +
                    "Anchor it or require at least one character.";
                return true;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            error =
                $"pattern at index {index} timed out during validation and is too expensive to evaluate: '{pattern}'.";
            return true;
        }

        error = string.Empty;
        return false;
    }
}
