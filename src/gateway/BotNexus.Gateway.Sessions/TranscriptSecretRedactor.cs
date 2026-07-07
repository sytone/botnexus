using System.Text.RegularExpressions;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Conservative, render-time secret scrubber for exported transcripts.
/// </summary>
/// <remarks>
/// This helper exists to reduce accidental credential exposure through the
/// transcript/session export API (see issue #1618). It is applied only when a
/// caller opts in (default off) so that normal rendering stays byte-identical.
/// The pattern set is deliberately narrow - matching well-known token shapes -
/// to minimise false positives on ordinary prose and source code.
/// </remarks>
public static partial class TranscriptSecretRedactor
{
    /// <summary>Placeholder substituted for any matched secret.</summary>
    public const string Placeholder = "[redacted-secret]";

    // Ordered patterns. Each targets a distinct, high-confidence credential shape.
    // Kept conservative on purpose: a missed exotic secret is preferable to
    // mangling legitimate transcript content with false positives.
    private static readonly Regex[] Patterns =
    [
        // GitHub tokens: ghp_/gho_/ghs_/ghu_/ghr_ followed by >=20 base62 chars.
        GitHubTokenRegex(),
        // OpenAI-style keys: sk- followed by >=20 base62 chars.
        OpenAiKeyRegex(),
        // AWS access key IDs: AKIA + 16 uppercase alphanumerics.
        AwsAccessKeyRegex(),
        // JWT-looking blobs: three base64url segments, header starts eyJ.
        JwtRegex(),
        // Bearer <token>.
        BearerRegex(),
        // key=value credentials for common secret keys.
        KeyValueSecretRegex(),
    ];

    /// <summary>
    /// Returns <paramref name="input"/> with any recognised secret shapes replaced
    /// by <see cref="Placeholder"/>. Returns the input unchanged when it is null,
    /// empty, or contains no recognised secrets.
    /// </summary>
    /// <param name="input">The text to scan; may be null.</param>
    /// <returns>The scrubbed text, or the original reference when nothing matched.</returns>
    public static string? Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var result = input;
        foreach (var pattern in Patterns)
            result = pattern.Replace(result, Placeholder);

        return result;
    }

    [GeneratedRegex(@"\bgh[porsu]_[A-Za-z0-9]{20}[A-Za-z0-9]*", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubTokenRegex();

    [GeneratedRegex(@"\bsk-[A-Za-z0-9]{20}[A-Za-z0-9]*", RegexOptions.CultureInvariant)]
    private static partial Regex OpenAiKeyRegex();

    [GeneratedRegex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.CultureInvariant)]
    private static partial Regex AwsAccessKeyRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b", RegexOptions.CultureInvariant)]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._~+/-]{8}=*", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"\b(?:password|api_key|apikey|secret|token)\s*=\s*[^\s""',;&]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex KeyValueSecretRegex();
}

