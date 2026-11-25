using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Sessions;

/// <summary>Fresh policy selection: configured authority by default, scoped inheritance or model policy only when selected.</summary>
public sealed class SessionCompactionCeilingResolver(
    ILogger<SessionCompactionCeilingResolver> logger,
    IAgentRegistry registry,
    IConversationStore conversations,
    IOptionsMonitor<PlatformConfig> configuration) : ISessionCompactionCeilingResolver
{
    /// <inheritdoc />
    public async Task<ScopedCompactionCeiling> ResolveAsync(AgentId agentId, ConversationId conversationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var conversation = await conversations.GetAsync(conversationId, cancellationToken).ConfigureAwait(false);
        var config = configuration.CurrentValue;
        // Missing conversations and foreign ownership cannot borrow the local agent/world policy.
        // Stores backfill legacy empty WorldId values with their local IWorldContext on read.
        if (conversation is null || conversation.AgentId != agentId
            || !string.Equals(conversation.WorldId, WorldIdentityResolver.Resolve(config).Id, StringComparison.Ordinal))
            return new ScopedCompactionCeiling(null, null);

        var descriptor = registry.Get(agentId);
        var options = config.Gateway?.Compaction ?? new CompactionOptions();
        var result = options.PolicySource switch
        {
            CompactionPolicySource.Model => new ScopedCompactionCeiling(null, null),
            CompactionPolicySource.Configured when options.CompactionTokenCeiling is { } configured
                => Checked(configured, "configured"),
            CompactionPolicySource.Conversation when conversation.CompactionTokenCeiling is { } conversationCeiling
                => Checked(conversationCeiling, "conversation"),
            _ => descriptor?.CompactionTokenCeiling is { } agentCeiling
                ? Checked(agentCeiling, "agent")
                : config.World?.CompactionTokenCeiling is { } worldCeiling
                    ? Checked(worldCeiling, "world")
                    : options.CompactionTokenCeiling is { } fallback
                        ? Checked(fallback, "configured")
                        : new ScopedCompactionCeiling(null, null)
        };
        logger.LogDebug("Compaction ceiling for agent {AgentId}, conversation {ConversationId}: {Tokens} ({Source}).",
            agentId.Value, conversationId.Value, result.Tokens, result.Source);
        return result;
    }

    private static ScopedCompactionCeiling Checked(int tokens, string source)
    {
        if (tokens <= 0)
            throw new InvalidOperationException($"Invalid {source} compaction token ceiling; expected a positive integer.");
        return new ScopedCompactionCeiling(tokens, source);
    }
}
