namespace BotNexus.Agent.Providers.Core;

/// <summary>
/// Classifies a model's API BaseUrl as a local/self-hosted endpoint versus a remote cloud endpoint.
/// Used to decide whether the cron/compaction stream-setup idle cap (StreamSetupTimeoutMs) should be
/// applied (#1652): cloud calls get a fail-fast cap so a stalled first token never wedges a
/// background turn, while local/self-hosted servers (ollama, vllm, lmstudio, sglang bound to
/// localhost / 127.0.0.1) are left uncapped because they are legitimately slow to warm up.
/// Mirrors the localhost/127.0.0.1 detection pattern in
/// <c>BotNexus.Agent.Providers.OpenAICompat.CompatDetector</c> without taking a dependency on it.
/// </summary>
public static class ProviderEndpointClassifier
{
    /// <summary>
    /// Returns true when <paramref name="baseUrl"/> points at a local/self-hosted provider
    /// (host is <c>localhost</c> or <c>127.0.0.1</c>). A null/empty/whitespace BaseUrl is treated as
    /// NOT local (returns false): an unknown host is conservatively treated as cloud so the safety
    /// cap still applies and a mis-registered model cannot stall a background call forever. Matching
    /// is case-insensitive and works on either a raw host:port string or a full URL.
    /// </summary>
    public static bool IsLocalProviderBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return false;

        var normalized = baseUrl.ToLowerInvariant();

        // Prefer a precise host check when the value parses as an absolute URL, so a cloud host that
        // merely contained the substring "localhost" (e.g. a path or subdomain) is not misclassified.
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            var host = uri.Host;
            return host is "localhost" or "127.0.0.1";
        }

        // Fallback for bare host:port forms that are not absolute URIs.
        return normalized is "localhost" or "127.0.0.1"
            || normalized.StartsWith("localhost:", StringComparison.Ordinal)
            || normalized.StartsWith("127.0.0.1:", StringComparison.Ordinal);
    }
}
