using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Sessions;

namespace BotNexus.Gateway.Streaming;

/// <summary>
/// #2522 producer seam: persists the provider's reported prompt-token count for the most recent
/// completed provider request onto the session metadata bag, under the key the compactor already
/// reads (<c>LlmSessionCompactor.ProviderPromptTokensMetadataKey</c>).
/// </summary>
/// <remarks>
/// <para>
/// Before this type the read side shipped by PR #2531 was a dead seam: nothing in the repository
/// wrote <c>lastProviderPromptTokens</c>, so every compaction abort rendered
/// <c>providerPromptTokens=unavailable</c> and the measure-first diagnostic could never fire.
/// Provider usage exists only on the agent-loop side; the narrowest place where a completed turn's
/// usage AND a writable, about-to-be-persisted session are both in hand is the streaming session
/// helper's <c>MessageEnd</c> event, which is the single choke point through which every streamed
/// gateway turn flows.
/// </para>
/// <para>
/// The value recorded is the provider's full prompt cost for that request: input tokens plus any
/// cache-read and cache-write tokens, because for cache-aware providers (Anthropic) the cached
/// portion of the prompt is reported separately from <c>input_tokens</c> yet is still part of the
/// prompt the model saw. This is deliberately the number the estimator is being compared against.
/// </para>
/// <para>
/// The write is last-writer-wins and is a plain metadata assignment, so it rides the session's
/// normal persistence path - no schema column, no separate store call, nothing that can be
/// forgotten by a store implementation. It can still legitimately be absent in two cases, both
/// benign because the read side treats absence as <c>unavailable</c> rather than as zero:
/// (1) a provider that reports no usage at all, and (2) the blocking <c>PromptAsync</c> path
/// (cron / soul / heartbeat), which does not flow through the streaming helper. Extending the
/// blocking path is a separate, larger change and is intentionally not made here.
/// </para>
/// </remarks>
public static class ProviderTokenUsageRecorder
{
    /// <summary>
    /// Records the prompt-token count from a completed provider request onto the session, when the
    /// provider reported a usable positive count. A null usage, or a non-positive total, leaves any
    /// previously recorded value untouched rather than overwriting it with a meaningless zero.
    /// </summary>
    /// <param name="session">The session to stamp; must not be null.</param>
    /// <param name="usage">The provider-reported usage for the completed request, if any.</param>
    /// <returns><c>true</c> when a value was recorded; otherwise <c>false</c>.</returns>
    public static bool Record(GatewaySession session, AgentResponseUsage? usage)
    {
        ArgumentNullException.ThrowIfNull(session);

        var promptTokens = ResolvePromptTokens(usage);
        if (promptTokens is not > 0)
        {
            return false;
        }

        session.Metadata[LlmSessionCompactor.ProviderPromptTokensMetadataKey] = promptTokens.Value;
        return true;
    }

    /// <summary>
    /// Resolves the provider's total prompt-token count for a request: input tokens plus any
    /// cache-read and cache-write tokens. Returns <c>null</c> when the provider reported nothing.
    /// </summary>
    /// <param name="usage">The provider-reported usage.</param>
    /// <returns>The total prompt tokens, or <c>null</c>.</returns>
    internal static int? ResolvePromptTokens(AgentResponseUsage? usage)
    {
        if (usage is null)
        {
            return null;
        }

        var input = usage.InputTokens ?? 0;
        var cacheRead = usage.CacheRead ?? 0;
        var cacheWrite = usage.CacheWrite ?? 0;
        var total = (long)input + cacheRead + cacheWrite;

        return total is > 0 and <= int.MaxValue ? (int)total : null;
    }
}
