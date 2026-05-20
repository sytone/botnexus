using System.Text.RegularExpressions;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Gateway.Security;

/// <summary>
/// Applies compiled regex patterns for common secret formats to redact credentials
/// from text before it is written to the session store. Each matched value is
/// replaced with <c>[REDACTED]</c>.
/// </summary>
public sealed partial class SecretRedactor : ISecretRedactor
{
    // Patterns are applied in order; more-specific patterns appear before generic ones.
    private static readonly Regex[] Patterns =
    [
        // OpenAI project keys (sk-proj-...)
        OpenAiProjectKeyRegex(),

        // OpenAI legacy keys (sk-...) — must come after project key to avoid partial matches
        OpenAiLegacyKeyRegex(),

        // Anthropic API keys (sk-ant-...)
        AnthropicKeyRegex(),

        // GitHub fine-grained personal access token (github_pat_...)
        GitHubFineGrainedPatRegex(),

        // GitHub classic tokens: ghp_, ghs_, gho_
        GitHubClassicTokenRegex(),

        // AWS access key IDs (AKIA...)
        AwsAccessKeyRegex(),

        // Google API keys (AIza...)
        GoogleApiKeyRegex(),

        // Slack tokens (xox...)
        SlackTokenRegex(),

        // Stripe live/test secret keys (sk_live_, sk_test_)
        StripeSecretKeyRegex(),

        // Authorization: Bearer <token> HTTP headers
        AuthorizationBearerRegex(),

        // Generic api_key / api-key = <value> patterns in text
        GenericApiKeyRegex(),
    ];

    /// <inheritdoc />
    public string Redact(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var result = input;
        foreach (var pattern in Patterns)
            result = pattern.Replace(result, "[REDACTED]");

        return result;
    }

    // ──────────────────── Compiled regex factories ────────────────────

    [GeneratedRegex(@"sk-proj-[A-Za-z0-9_\-]{40,}", RegexOptions.Compiled)]
    private static partial Regex OpenAiProjectKeyRegex();

    [GeneratedRegex(@"sk-[A-Za-z0-9]{48,}", RegexOptions.Compiled)]
    private static partial Regex OpenAiLegacyKeyRegex();

    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_\-]{40,}", RegexOptions.Compiled)]
    private static partial Regex AnthropicKeyRegex();

    [GeneratedRegex(@"github_pat_[A-Za-z0-9_]{59,}", RegexOptions.Compiled)]
    private static partial Regex GitHubFineGrainedPatRegex();

    [GeneratedRegex(@"gh[pso]_[A-Za-z0-9]{36}", RegexOptions.Compiled)]
    private static partial Regex GitHubClassicTokenRegex();

    [GeneratedRegex(@"AKIA[0-9A-Z]{16}", RegexOptions.Compiled)]
    private static partial Regex AwsAccessKeyRegex();

    [GeneratedRegex(@"AIza[0-9A-Za-z\-_]{35}", RegexOptions.Compiled)]
    private static partial Regex GoogleApiKeyRegex();

    [GeneratedRegex(@"xox[bprao]-[A-Za-z0-9\-]+", RegexOptions.Compiled)]
    private static partial Regex SlackTokenRegex();

    [GeneratedRegex(@"sk_(live|test)_[A-Za-z0-9]{20,}", RegexOptions.Compiled)]
    private static partial Regex StripeSecretKeyRegex();

    // Capture group 1 = prefix up to and including "Bearer "; group 2 = the token itself.
    // Replace entire match so the header name is preserved: "Authorization: Bearer [REDACTED]"
    [GeneratedRegex(@"(Authorization:\s*Bearer\s+)[A-Za-z0-9+/=._\-]{20,}", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationBearerRegex();

    // Handles: api_key=VALUE, api-key: VALUE, apiKey=VALUE  (case-insensitive key name)
    [GeneratedRegex(@"(?i)api[_\-]?key\s*[=:]\s*[A-Za-z0-9+/=._\-]{20,}", RegexOptions.Compiled)]
    private static partial Regex GenericApiKeyRegex();
}
