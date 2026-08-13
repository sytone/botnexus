using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Resolution;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Default <see cref="ISessionContextWindowResolver"/> (#2896): reads the conversation override from
/// <see cref="IConversationStore"/>, the agent's configured window from <see cref="IAgentRegistry"/>,
/// and the registered model's own window from the model registry, then applies
/// <see cref="ScopedCompactionWindow.Resolve"/>.
/// </summary>
/// <remarks>
/// Every collaborator is optional and every read is best-effort. Compaction is a background health
/// mechanism, so a store failure must degrade to the previous global-window behaviour rather than
/// abort the turn - returning <see langword="null"/> leaves
/// <see cref="CompactionOptions.ContextWindowTokens"/> exactly as configured.
/// </remarks>
public sealed class SessionContextWindowResolver : ISessionContextWindowResolver
{
    private readonly IAgentRegistry? _registry;
    private readonly IConversationStore? _conversations;
    private readonly LlmClient? _llmClient;
    private readonly ILogger<SessionContextWindowResolver> _logger;

    public SessionContextWindowResolver(
        ILogger<SessionContextWindowResolver> logger,
        IAgentRegistry? registry = null,
        IConversationStore? conversations = null,
        LlmClient? llmClient = null)
    {
        _logger = logger;
        _registry = registry;
        _conversations = conversations;
        _llmClient = llmClient;
    }

    /// <inheritdoc />
    public async Task<int?> ResolveAsync(AgentId agentId, ConversationId conversationId, CancellationToken cancellationToken = default)
    {
        var descriptor = TryGetDescriptor(agentId);
        var conversation = await TryGetConversationAsync(conversationId, cancellationToken).ConfigureAwait(false);

        // Route through the one sanctioned resolver so the conversation's model/context overrides are
        // applied with the same precedence the dispatch path uses, rather than reading a raw
        // descriptor.ModelId (which ModelResolutionCentralizationArchitectureTests correctly bans).
        var effective = ModelOverrideResolver.Resolve(
            modelDefaults: default,
            agent: new ModelOverrideLayer(
                Model: descriptor?.ModelId,
                ContextWindow: descriptor?.ContextWindow),
            conversation: new ModelOverrideLayer(
                Model: string.IsNullOrWhiteSpace(conversation?.ModelOverride) ? null : conversation!.ModelOverride,
                ContextWindow: conversation?.ContextWindowOverride));

        int? modelWindow = null;
        if (descriptor is not null && _llmClient is not null && !string.IsNullOrWhiteSpace(effective.Model))
        {
            try
            {
                modelWindow = _llmClient.Models.GetModel(descriptor.ApiProvider, effective.Model!)?.ContextWindow;
            }
            catch (Exception ex)
            {
                // An unregistered / mis-registered model must not break the compaction decision.
                _logger.LogDebug(ex, "Could not resolve model context window for agent {AgentId}.", agentId.Value);
            }
        }

        // effective.ContextWindow already carries conversation-over-agent precedence; the model's own
        // window is the least specific layer and applies only when neither is set.
        var resolved = ScopedCompactionWindow.Resolve(
            conversationOverride: effective.ContextWindow,
            agentWindow: null,
            modelWindow: modelWindow);

        _logger.LogDebug(
            "Scoped compaction window for agent {AgentId} / conversation {ConversationId}: " +
            "conversationOverride {ConversationOverride}, agentWindow {AgentWindow}, modelWindow {ModelWindow} => {Resolved}.",
            agentId.Value,
            conversationId.Value,
            conversation?.ContextWindowOverride,
            descriptor?.ContextWindow,
            modelWindow,
            resolved);

        return resolved;
    }

    private AgentDescriptor? TryGetDescriptor(AgentId agentId)
    {
        if (_registry is null)
        {
            return null;
        }

        try
        {
            return _registry.Get(agentId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the descriptor for agent {AgentId}.", agentId.Value);
            return null;
        }
    }

    private async Task<Conversation?> TryGetConversationAsync(ConversationId conversationId, CancellationToken cancellationToken)
    {
        if (_conversations is null || string.IsNullOrWhiteSpace(conversationId.Value))
        {
            return null;
        }

        try
        {
            return await _conversations.GetAsync(conversationId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the context-window override for conversation {ConversationId}.", conversationId.Value);
            return null;
        }
    }
}
