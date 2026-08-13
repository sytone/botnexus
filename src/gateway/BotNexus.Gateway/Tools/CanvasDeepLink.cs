using System.Diagnostics.CodeAnalysis;

namespace BotNexus.Gateway.Tools;

/// <summary>
/// Builds the portal deep link an agent can hand to the user after rendering a canvas (#2975).
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> The route <c>/agent/{agentId}/conversation/{conversationId}?tab=canvas</c>
/// already selects the Canvas pane, but nothing told the agent the portal's external base URL or the
/// <c>?tab=</c> convention, so it could not assemble one. The link is returned from the <c>canvas</c>
/// tool result rather than injected into every system prompt: it is paid only when a canvas is
/// actually rendered, it cannot go stale, and the tool already knows both ids.</para>
/// <para><b>Where the base URL comes from (the single documented source).</b>
/// <c>gateway.publicBaseUrl</c>. When that is unset the resolver falls back to <c>gateway.listenUrl</c>
/// ONLY when that value names a concrete, dialable host. A wildcard bind (<c>http://+:5005</c>,
/// <c>http://*:5005</c>, <c>http://0.0.0.0:5005</c>, <c>http://[::]:5005</c>) names no host at all, so
/// no URL can be built from it without inventing one.</para>
/// <para><b>Failure direction.</b> When nothing resolves, NO link is emitted and the reason is stated.
/// A link built from the wrong host is worse than no link: it sends the user somewhere that either
/// does not answer or, on a shared host, answers as something else. Every guard here fails towards
/// omission, never towards a half-formed or guessed URL.</para>
/// </remarks>
public static class CanvasDeepLink
{
    /// <summary>Query parameter that selects the Canvas pane, per <c>AgentPanel.ApplyTabFromUri</c>.</summary>
    public const string CanvasTabQuery = "tab=canvas";

    /// <summary>
    /// Human-readable explanation returned in place of a link when no external base URL is resolvable.
    /// Names the configuration key so the operator can act on it without reading the source.
    /// </summary>
    /// <remarks>
    /// Deliberately carries NO example URL. This string is read by a model, and an example origin in
    /// the same sentence as "no link is available" is exactly the shape that gets copied into a reply
    /// as though it were the real one.
    /// </remarks>
    public const string UnresolvableBaseUrlReason =
        "No canvas link is available because the portal's external base URL is not configured: "
        + "set gateway.publicBaseUrl to have canvas links emitted.";

    /// <summary>
    /// Explanation returned when the tool has no bound conversation. The canvas route is
    /// conversation-scoped, so there is no correct link to emit rather than a shortened one.
    /// </summary>
    public const string NoConversationReason =
        "No canvas link is available because this canvas is not bound to a conversation.";

    private static readonly string[] WildcardHosts = ["+", "*", "0.0.0.0", "[::]", "::", "[::0]"];

    /// <summary>
    /// Resolves the portal's external base URL from the two configured candidates, or returns
    /// <see langword="null"/> when neither names a dialable origin.
    /// </summary>
    /// <param name="publicBaseUrl">The operator's <c>gateway.publicBaseUrl</c>, if any.</param>
    /// <param name="listenUrl">The operator's <c>gateway.listenUrl</c>, used only if concrete.</param>
    public static string? ResolveBaseUrl(string? publicBaseUrl, string? listenUrl)
        => Normalize(publicBaseUrl) ?? Normalize(listenUrl);

    /// <summary>
    /// Builds the canvas deep link for an agent and conversation.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> and a fully-formed absolute URL, or <see langword="false"/> with
    /// <paramref name="link"/> set to <see langword="null"/> when any input is missing or unusable.
    /// </returns>
    public static bool TryBuild(
        string? baseUrl,
        string? agentId,
        string? conversationId,
        [NotNullWhen(true)] out string? link)
    {
        link = null;

        var origin = Normalize(baseUrl);
        if (origin is null || string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(conversationId))
            return false;

        // Both ids are operator/agent-supplied strings that land in path segments. Escaping them is
        // what stops an id containing '/', '?' or '#' from silently retargeting the link.
        link = $"{origin}/agent/{Uri.EscapeDataString(agentId)}/conversation/{Uri.EscapeDataString(conversationId)}?{CanvasTabQuery}";
        return true;
    }

    /// <summary>
    /// Reduces a configured URL to a trailing-slash-free absolute origin, or <see langword="null"/>
    /// when it is absent, relative, non-http(s), or bound to a wildcard host.
    /// </summary>
    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim().TrimEnd('/');
        if (trimmed.Length == 0)
            return null;

        // A wildcard bind is rejected BEFORE Uri parsing: Uri accepts "http://+:5005" and reports the
        // host as "+", which would produce a syntactically valid but undialable link.
        var schemeSplit = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (schemeSplit < 0)
            return null;

        var authority = trimmed[(schemeSplit + 3)..];
        var hostOnly = authority.Split('/')[0];
        var portSplit = hostOnly.LastIndexOf(':');
        var host = portSplit > 0 && !hostOnly.EndsWith(']') ? hostOnly[..portSplit] : hostOnly;
        if (WildcardHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            return null;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return null;

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return null;

        return trimmed;
    }
}
