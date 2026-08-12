using System.Text.RegularExpressions;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Compiled operator-supplied secret redaction patterns (#2727).
/// </summary>
/// <remarks>
/// <b>Why this type exists rather than passing a raw string list to the redactor.</b> The pattern set
/// is operator input, and operator input that reaches a regex engine has two failure modes that both
/// end badly on the logging path: a malformed pattern (which would throw the first time a transcript
/// is written) and a pathological pattern (which would hang the writer). Both must be resolved before
/// the redactor is usable, so compilation and validation are a single explicit step here, invoked at
/// construction. A redactor that throws mid-transcript is strictly worse than one with a closed
/// pattern set - so the failure is moved to startup, where an operator can see and fix it.
///
/// The same rules are surfaced non-fatally by <c>PlatformConfigValidator</c> so the operator gets a
/// named error for every offending pattern rather than a stack trace for the first one. The two
/// entry points deliberately share <see cref="SecretRedactionPatternRules"/>; two independent copies
/// of the rules would drift, and the drift would be silent.
/// </remarks>
public sealed class SecretRedactionOptions
{
    /// <summary>
    /// Default per-pattern match timeout. Operator patterns are untrusted with respect to
    /// complexity, so every one is bounded; 100ms is far above any legitimate credential-shaped
    /// match and far below a catastrophic backtrack.
    /// </summary>
    public static readonly TimeSpan DefaultMatchTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>Creates options from operator-supplied pattern strings.</summary>
    /// <param name="patterns">Raw operator regex patterns; may be empty.</param>
    /// <param name="matchTimeout">Per-pattern match timeout. Defaults to <see cref="DefaultMatchTimeout"/>.</param>
    public SecretRedactionOptions(IReadOnlyList<string>? patterns, TimeSpan? matchTimeout = null)
    {
        Patterns = patterns ?? [];
        MatchTimeout = matchTimeout ?? DefaultMatchTimeout;
    }

    /// <summary>Raw operator-supplied patterns, in configuration order.</summary>
    public IReadOnlyList<string> Patterns { get; }

    /// <summary>Per-pattern match timeout applied to every compiled operator pattern.</summary>
    public TimeSpan MatchTimeout { get; }

    /// <summary>
    /// Validates and compiles every operator pattern, de-duplicating exact repeats so a repeated
    /// entry costs nothing at redaction time.
    /// </summary>
    /// <returns>The compiled patterns, empty when none are configured.</returns>
    /// <exception cref="ArgumentException">
    /// A pattern is empty/whitespace, is not a valid regex, or matches the empty string (and would
    /// therefore redact everything). The message names the offending pattern.
    /// </exception>
    public IReadOnlyList<Regex> Compile()
    {
        if (Patterns.Count == 0)
            return [];

        if (MatchTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"Secret redaction match timeout must be greater than zero (was {MatchTimeout}).",
                nameof(MatchTimeout));
        }

        List<Regex> compiled = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        for (var i = 0; i < Patterns.Count; i++)
        {
            var pattern = Patterns[i];

            if (SecretRedactionPatternRules.TryGetError(pattern, i, out var error))
                throw new ArgumentException(error, nameof(Patterns));

            if (!seen.Add(pattern))
                continue;

            compiled.Add(new Regex(pattern, RegexOptions.None, MatchTimeout));
        }

        return compiled;
    }
}
