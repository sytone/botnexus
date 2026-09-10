using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Core;

/// <summary>
/// Endpoint hints that raise a provider's prompt-cache hit rate.
/// </summary>
/// <remarks>
/// <para>
/// Prefix caches are not global: OpenAI holds them on individual machines and routes by a hash of
/// the leading tokens, and xAI documents the same routing problem for Grok. A follow-up turn that
/// lands on a different machine re-processes a prefix that was already cached, at full input
/// price, with no error and no signal other than <c>cached_tokens</c> coming back zero. The
/// session-scoped hints here are what steer a conversation back to the machine holding its entry.
/// </para>
/// <para>
/// The Responses paths already sent <c>prompt_cache_key</c>; the Chat Completions paths -- which
/// carry most traffic, including every Copilot and OpenAI-compatible model -- sent nothing. This
/// type exists so the rule is written once rather than three times, and so the endpoint allowlist
/// stays in one place: an unknown field is a 400 on strict OpenAI-compatible servers, so the key
/// goes only to endpoints known to accept it.
/// </para>
/// </remarks>
public static class PromptCacheRouting
{
    /// <summary>
    /// Header xAI documents for keeping a conversation on one cache. Grok caches automatically,
    /// so this header is the only lever available on that path.
    /// </summary>
    public const string GrokConversationHeader = "x-grok-conv-id";

    /// <summary>
    /// Resolves the <c>prompt_cache_key</c> to send with a request, or null when the endpoint is
    /// not known to accept the field, caching is switched off, or there is no session to key on.
    /// </summary>
    public static string? ResolveCacheKey(LlmModel model, StreamOptions? options)
    {
        if (options is null || options.CacheRetention == CacheRetention.None)
            return null;

        if (string.IsNullOrWhiteSpace(options.SessionId))
            return null;

        return SupportsPromptCacheKey(model.BaseUrl) ? options.SessionId : null;
    }

    /// <summary>
    /// Resolves the value for <see cref="GrokConversationHeader"/>, or null when the model is not
    /// an xAI model, caching is switched off, or there is no session to key on.
    /// </summary>
    public static string? ResolveGrokConversationId(LlmModel model, StreamOptions? options)
    {
        if (options is null || options.CacheRetention == CacheRetention.None)
            return null;

        if (string.IsNullOrWhiteSpace(options.SessionId))
            return null;

        return IsXai(model) ? options.SessionId : null;
    }

    /// <summary>
    /// Whether the endpoint behind <paramref name="baseUrl"/> accepts <c>prompt_cache_key</c>.
    /// The Copilot host is included because the Copilot Responses path already sends the field.
    /// </summary>
    private static bool SupportsPromptCacheKey(string baseUrl) =>
        baseUrl.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase) ||
        baseUrl.Contains("githubcopilot.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Matches the xAI detection already used by the compatibility resolvers.
    /// </summary>
    private static bool IsXai(LlmModel model) =>
        string.Equals(model.Provider, "xai", StringComparison.OrdinalIgnoreCase) ||
        model.BaseUrl.Contains("api.x.ai", StringComparison.OrdinalIgnoreCase);
}
