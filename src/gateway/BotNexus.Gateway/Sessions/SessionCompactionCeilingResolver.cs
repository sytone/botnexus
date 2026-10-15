using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Sessions;

/// <summary>Fresh, fail-closed ceiling policy: conversation overrides agent overrides the singular local world's configuration.</summary>
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
        var result = conversation.CompactionTokenCeiling is { } conversationCeiling
            ? Checked(conversationCeiling, "conversation")
            : descriptor?.CompactionTokenCeiling is { } agentCeiling
                ? Checked(agentCeiling, "agent")
                : config.World?.CompactionTokenCeiling is { } worldCeiling
                    ? Checked(worldCeiling, "world")
                    : new ScopedCompactionCeiling(null, null);
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
