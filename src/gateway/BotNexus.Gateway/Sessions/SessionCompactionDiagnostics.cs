using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Fresh read-only projection of the pre-turn compaction policy and its existing measurements.
/// Session identifiers are opaque: policy identity comes only from the persisted conversation link.
/// </summary>
public sealed class SessionCompactionDiagnostics(
    ISessionStore sessions,
    IConversationStore conversations,
    ISessionContextWindowResolver contextWindow,
    ISessionCompactionCeilingResolver ceilingResolver,
    IOptionsMonitor<CompactionOptions> optionsMonitor,
    IOptionsMonitor<PlatformConfig> configuration) : ISessionCompactionDiagnostics
{
    /// <inheritdoc />
    public async Task<CompactionPolicyDiagnostics?> ResolveAsync(
        AgentId agentId, SessionId sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null || session.AgentId != agentId || !session.ConversationId.IsInitialized())
            return null;
        var conversationId = session.ConversationId;
        var conversation = await conversations.GetAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (conversation is null || conversation.AgentId != agentId
            || !string.Equals(conversation.WorldId, WorldIdentityResolver.Resolve(configuration.CurrentValue).Id, StringComparison.Ordinal))
            return null;

        // Match GatewayHost's policy resolution; never substitute the handle's binding snapshot.
        var options = optionsMonitor.CurrentValue;
        var scopedWindow = await contextWindow.ResolveAsync(agentId, conversationId, cancellationToken).ConfigureAwait(false);
        options = ScopedCompactionWindow.Apply(options, scopedWindow);
        var ceiling = await ceilingResolver.ResolveAsync(agentId, conversationId, cancellationToken).ConfigureAwait(false);
        options = options with { CompactionTokenCeiling = ceiling.Tokens };
        var effectiveThreshold = CompactionThreshold.Resolve(options);
        var ratioThreshold = CompactionThreshold.Resolve(options with { CompactionTokenCeiling = null });
        var measurement = LlmSessionCompactor.MeasureTokens(session.Session,
            LlmSessionCompactor.EstimateVisibleTokenCount(session.Session));
        return new CompactionPolicyDiagnostics
        {
            ScopedContextWindowTokens = scopedWindow,
            ContextWindowTokens = options.ContextWindowTokens,
            TokenThresholdRatio = options.TokenThresholdRatio,
            RatioSafetyThresholdTokens = ratioThreshold,
            CompactionTokenCeiling = ceiling.Tokens,
            CompactionTokenCeilingSource = ceiling.Source,
            EffectiveThresholdTokens = effectiveThreshold,
            LocalEstimatedTokens = measurement.EstimatedTokens,
            ProviderPromptTokens = measurement.ProviderPromptTokens,
            ProviderMeasurementSource = measurement.ProviderPromptTokens.HasValue
                ? LlmSessionCompactor.ProviderPromptTokensMetadataKey : null,
            ProviderToEstimateRatio = measurement.Ratio
        };
    }
}
