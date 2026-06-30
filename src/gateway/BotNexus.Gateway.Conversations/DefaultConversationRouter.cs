using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Extensions.Logging;
using SessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

namespace BotNexus.Gateway.Conversations;

/// <summary>
/// Default implementation of <see cref="IConversationRouter"/>.
/// Wires inbound messages to the appropriate conversation/session
/// and provides outbound binding resolution for fan-out.
/// </summary>
public sealed class DefaultConversationRouter : IConversationRouter
{
    private readonly IConversationStore _conversationStore;
    private readonly ISessionStore _sessionStore;
    private readonly IConversationChangeNotifier? _changeNotifier;
    private readonly ILogger<DefaultConversationRouter> _logger;

    public DefaultConversationRouter(
        IConversationStore conversationStore,
        ISessionStore sessionStore,
        ILogger<DefaultConversationRouter> logger,
        IConversationChangeNotifier? changeNotifier = null)
    {
        _conversationStore = conversationStore;
        _sessionStore = sessionStore;
        _logger = logger;
        _changeNotifier = changeNotifier;
    }

    /// <inheritdoc />
    public async Task<ConversationRoutingResult> ResolveInboundAsync(
        AgentId agentId,
        ChannelKey channelType,
        ChannelAddress channelAddress,
        string? conversationId = null,
        CancellationToken ct = default,
        CitizenId? initiator = null)
    {
        // When the caller knows the exact conversation (e.g. portal with active conversation tab),
        // skip binding lookup entirely.
        if (conversationId is not null)
        {
            var convId = ConversationId.From(conversationId);
            var direct = await _conversationStore.GetAsync(convId, ct);
            if (direct is null)
            {
                _logger.LogWarning(
                    "ResolveInbound: explicit conversationId {ConversationId} not found -- falling back to binding lookup",
                    conversationId);
                // Fall through to binding lookup below
            }
            else
            {
                var reactivated = false;
                if (direct.Status == ConversationStatus.Archived)
                {
                    direct.Status = ConversationStatus.Active;
                    direct.ActiveSessionId = null;
                    reactivated = true;
                }

                // Bind-on-first-use for explicit conversationId path:
                // Add a channel address binding if none exists; reactivate a muted binding.
                // This ensures reconnects without explicit conversationId can find this conversation.
                var existingBinding = direct.ChannelBindings.FirstOrDefault(b =>
                    b.ChannelType == channelType && b.ChannelAddress == channelAddress);
                if (existingBinding is null && ShouldPersistBinding(agentId, channelType, channelAddress))
                {
                    direct.ChannelBindings.Add(new ChannelBinding
                    {
                        ChannelType = channelType,
                        ChannelAddress = channelAddress,
                        Mode = BindingMode.Interactive
                    });
                    reactivated = true;
                }
                else if (existingBinding is { Mode: BindingMode.Muted })
                {
                    existingBinding.Mode = BindingMode.Interactive;
                    reactivated = true;
                }
                var (directSessionId, directIsNew, directSessionChanged) = await ResolveOrCreateSessionAsync(direct, agentId, ct, channelType);
                var changed = directSessionChanged;
                if (changed)
                {
                    direct.UpdatedAt = DateTimeOffset.UtcNow;
                    await _conversationStore.SaveAsync(direct, ct);
                    await NotifyChangedAsync("updated", agentId, direct.ConversationId, ct);
                }
                else if (reactivated)
                {
                    direct.UpdatedAt = DateTimeOffset.UtcNow;
                    await _conversationStore.SaveAsync(direct, ct);
                    await NotifyChangedAsync("updated", agentId, direct.ConversationId, ct);
                }

                return new ConversationRoutingResult(direct, directSessionId, directIsNew);
            }
        }

        // 1. Try to find an existing conversation by binding
        var conversation = await _conversationStore.ResolveByBindingAsync(
            agentId, channelType, channelAddress, ct);

        var addedBinding = false;
        if (conversation is null)
        {
            conversation = await TryReopenArchivedConversationAsync(agentId, channelType, channelAddress, ct);
        }
        if (conversation is null)
        {
            // Every unique (channelType, channelAddress) gets its own conversation; adapters encode
            // any sub-address (e.g. forum topic) into the channel address themselves.
            // There is no special "default" conversation for addressless channels -- an empty
            // address is a valid stable identity (e.g. a future channel with no external ID).
            conversation = new Conversation
            {
                ConversationId = ConversationId.Create(),
                AgentId = agentId,
                Title = $"{channelType}:{channelAddress}",
                IsDefault = false,
                Initiator = initiator?.IsValid == true ? initiator : null
            };
            if (ShouldPersistBinding(agentId, channelType, channelAddress) &&
                !conversation.ChannelBindings.Any(b =>
                    b.ChannelType == channelType && b.ChannelAddress == channelAddress))
            {
                var binding = new ChannelBinding
                {
                    ChannelType = channelType,
                    ChannelAddress = channelAddress,
                    Mode = BindingMode.Interactive
                };
                conversation.ChannelBindings.Add(binding);
                addedBinding = true;
            }
            _logger.LogDebug(
                "Creating new conversation for agent={AgentId} channel={ChannelType} address={ChannelAddress}",
                agentId, channelType, channelAddress);
        }

        // 3. Resolve or create the active session
        var conversationChanged = addedBinding;
        var (sessionId, isNewSession, sessionChanged) = await ResolveOrCreateSessionAsync(conversation, agentId, ct, channelType);
        conversationChanged |= sessionChanged;

        if (conversationChanged)
        {
            conversation.UpdatedAt = DateTimeOffset.UtcNow;
            await _conversationStore.SaveAsync(conversation, ct);
            await NotifyChangedAsync("updated", agentId, conversation.ConversationId, ct);
        }

        // Resolve the originating binding so callers don't need a second lookup into the binding list.
        var originatingBinding = conversation.ChannelBindings
            .FirstOrDefault(b =>
                b.ChannelType == channelType &&
                b.ChannelAddress == channelAddress);

        return new ConversationRoutingResult(conversation, sessionId, isNewSession, originatingBinding);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChannelBinding>> GetOutboundBindingsAsync(
        SessionId sessionId,
        BindingId? originatingBindingId,
        CancellationToken ct = default)
    {
        // 1. Resolve the session to get ConversationId
        var session = await _sessionStore.GetAsync(sessionId, ct);
        if (session is null)
        {
            _logger.LogDebug("GetOutboundBindings: session {SessionId} not found -- returning empty", sessionId);
            return [];
        }

        var conversationId = session.ConversationId;
        if (!conversationId.IsInitialized())
        {
            _logger.LogDebug("GetOutboundBindings: session {SessionId} has no ConversationId -- returning empty", sessionId);
            return [];
        }

        var conversation = await _conversationStore.GetAsync(conversationId, ct);
        if (conversation is null)
        {
            _logger.LogDebug("GetOutboundBindings: conversation {ConversationId} not found -- returning empty", conversationId);
            return [];
        }

        // 2. Filter bindings: not muted, not the originating binding
        return conversation.ChannelBindings
            .Where(b => b.Mode != BindingMode.Muted)
            .Where(b => originatingBindingId is null || b.BindingId != originatingBindingId.Value)
            .ToList();
    }

    /// <inheritdoc />
    public async Task MuteBindingAsync(ConversationId conversationId, BindingId bindingId, CancellationToken ct = default)
    {
        var conversation = await _conversationStore.GetAsync(conversationId, ct);
        if (conversation is null)
        {
            _logger.LogDebug("MuteBinding: conversation {ConversationId} not found", conversationId);
            return;
        }

        var binding = conversation.ChannelBindings.FirstOrDefault(b =>
            string.Equals(b.BindingId.Value, bindingId.Value, StringComparison.Ordinal));

        if (binding is null)
        {
            _logger.LogDebug("MuteBinding: binding {BindingId} not found in conversation {ConversationId}", bindingId, conversationId);
            return;
        }

        if (binding.Mode == BindingMode.Muted)
            return;

        binding.Mode = BindingMode.Muted;
        await _conversationStore.SaveAsync(conversation, ct);
        _logger.LogInformation("MuteBinding: binding {BindingId} ({ChannelType}:{ChannelAddress}) demoted to Muted in conversation {ConversationId}",
            bindingId, binding.ChannelType, binding.ChannelAddress, conversationId);
    }

    /// <inheritdoc />
    public async Task MuteBindingByAddressAsync(AgentId? agentId, ChannelKey channelType, ChannelAddress channelAddress, CancellationToken ct = default)
    {
        var conversations = await _conversationStore.ListAsync(agentId, ct);
        foreach (var conversation in conversations)
        {
            var binding = conversation.ChannelBindings.FirstOrDefault(b =>
                b.ChannelType.Equals(channelType) &&
                b.ChannelAddress == channelAddress &&
                b.Mode != BindingMode.Muted);

            if (binding is null)
                continue;

            binding.Mode = BindingMode.Muted;
            await _conversationStore.SaveAsync(conversation, ct);
            _logger.LogInformation("MuteBindingByAddress: binding {BindingId} ({ChannelType}:{ChannelAddress}) demoted to Muted in conversation {ConversationId}",
                binding.BindingId, channelType, channelAddress, conversation.ConversationId);
        }
    }

    /// <inheritdoc />
    public async Task ReattachBindingAsync(BindingId bindingId, ConversationId targetConversationId, CancellationToken ct = default)
    {
        var target = await _conversationStore.GetAsync(targetConversationId, ct);
        if (target is null)
        {
            _logger.LogWarning("ReattachBinding: target conversation {ConversationId} not found", targetConversationId);
            return;
        }

        // Find the binding across all conversations for the same agent
        var conversations = await _conversationStore.ListAsync(target.AgentId, ct);
        foreach (var source in conversations)
        {
            var binding = source.ChannelBindings.FirstOrDefault(b =>
                string.Equals(b.BindingId.Value, bindingId.Value, StringComparison.Ordinal));

            if (binding is null)
                continue;

            if (source.ConversationId == targetConversationId)
            {
                _logger.LogDebug("ReattachBinding: binding {BindingId} is already in target conversation {ConversationId}", bindingId, targetConversationId);
                return;
            }

            source.ChannelBindings.Remove(binding);
            source.UpdatedAt = DateTimeOffset.UtcNow;
            await _conversationStore.SaveAsync(source, ct);

            target.ChannelBindings.Add(binding);
            target.UpdatedAt = DateTimeOffset.UtcNow;
            await _conversationStore.SaveAsync(target, ct);

            _logger.LogInformation(
                "ReattachBinding: moved binding {BindingId} from conversation {SourceId} to {TargetId}",
                bindingId, source.ConversationId, targetConversationId);
            return;
        }

        _logger.LogWarning("ReattachBinding: binding {BindingId} not found in any conversation for agent {AgentId}", bindingId, target.AgentId);
    }

    // Resolves or creates an active session for the given conversation.
    // Stamps session.ConversationId and conversation.ActiveSessionId when changed.
    // Returns the sessionId, whether it is new, and whether the conversation was mutated.
    private async Task<(SessionId sessionId, bool isNewSession, bool conversationChanged)> ResolveOrCreateSessionAsync(
        Conversation conversation, AgentId agentId, CancellationToken ct, ChannelKey? inboundChannelType = null)
    {
        SessionId sessionId;
        var isNewSession = false;
        var conversationChanged = false;

        if (conversation.ActiveSessionId.HasValue)
        {
            var existingSession = await _sessionStore.GetAsync(conversation.ActiveSessionId.Value, ct);
            if (existingSession is { Status: not SessionStatus.Sealed } &&
                !IsCrossChannelConflict(existingSession, inboundChannelType))
            {
                // Reuse Active AND Expired sessions -- GatewayHost reactivates Expired sessions.
                // Only Sealed sessions (explicit reset/archive) should trigger a new session.
                // Cross-channel conflicts (e.g. cron session reused by SignalR) also trigger
                // a new session to prevent channel context bleeding (#731).
                sessionId = conversation.ActiveSessionId.Value;
                _logger.LogDebug("Reusing {Status} session {SessionId} for conversation {ConversationId}",
                    existingSession.Status, sessionId, conversation.ConversationId);
            }
            else
            {
                var oldId = conversation.ActiveSessionId.Value;
                sessionId = SessionId.Create();
                isNewSession = true;
                conversation.ActiveSessionId = null;
                conversationChanged = true;
                _logger.LogDebug(
                    "Active session {OldSessionId} is no longer usable; creating new session {NewSessionId} for conversation {ConversationId}",
                    oldId, sessionId, conversation.ConversationId);
            }
        }
        else
        {
            sessionId = SessionId.Create();
            isNewSession = true;
            _logger.LogDebug("Creating new session {SessionId} for conversation {ConversationId}", sessionId, conversation.ConversationId);
        }

        var session = await _sessionStore.GetOrCreateAsync(sessionId, agentId, ct);
        if (session.SessionId != sessionId)
        {
            sessionId = session.SessionId;
            isNewSession = true;
        }

        if (session.ConversationId != conversation.ConversationId)
        {
            session.ConversationId = conversation.ConversationId;
            await _sessionStore.SaveAsync(session, ct);
        }

        if (conversation.ActiveSessionId is null || conversation.ActiveSessionId != sessionId)
        {
            conversation.ActiveSessionId = sessionId;
            conversationChanged = true;
        }

        return (sessionId, isNewSession, conversationChanged);
    }

    /// <summary>
    /// Returns true when the existing session was created by a different channel type
    /// (e.g. cron) and the inbound channel is interactive (e.g. signalr). Reusing such
    /// a session would leak the prior channel's context into the new channel (#731).
    /// </summary>
    private static bool IsCrossChannelConflict(GatewaySession existingSession, ChannelKey? inboundChannelType)
    {
        if (inboundChannelType is null)
            return false;

        if (existingSession.ChannelType is null)
            return false;

        // Same channel type — no conflict.
        if (existingSession.ChannelType == inboundChannelType)
            return false;

        // A cron-owned session must not be reused by interactive channels.
        // The session's metadata, SessionType, and ChannelType all reflect the cron context
        // which would confuse diagnostics and potentially leak trigger metadata.
        return string.Equals(existingSession.ChannelType.Value.Value, "cron", StringComparison.OrdinalIgnoreCase);
    }

    // A conversation kickoff (ConversationTool) posts a synthetic inbound on the
    // internal channel whose ChannelAddress equals the conversation AgentId. That
    // artifact must never be persisted as a binding: an internal binding addressed by
    // an agent name accumulates duplicates and poisons multi-bot routing (#1681).
    private const string KickoffChannelType = "internal";

    private static bool ShouldPersistBinding(AgentId agentId, ChannelKey channelType, ChannelAddress channelAddress) =>
        !(string.Equals(channelType.Value, KickoffChannelType, StringComparison.OrdinalIgnoreCase) &&
          string.Equals(channelAddress.Value, agentId.Value, StringComparison.OrdinalIgnoreCase));

    private async Task<Conversation?> TryReopenArchivedConversationAsync(
        AgentId agentId,
        ChannelKey channelType,
        ChannelAddress channelAddress,
        CancellationToken ct)
    {
        var conversations = await _conversationStore.ListAsync(agentId, ct);
        var archived = conversations
            .Where(c => c.Status == ConversationStatus.Archived)
            .Where(c => c.ChannelBindings.Any(b =>
                b.ChannelType == channelType &&
                b.ChannelAddress == channelAddress))
            .OrderByDescending(c => c.UpdatedAt)
            .FirstOrDefault();

        if (archived is null)
            return null;

        archived.Status = ConversationStatus.Active;
        archived.ActiveSessionId = null;
        archived.UpdatedAt = DateTimeOffset.UtcNow;
        await _conversationStore.SaveAsync(archived, ct);

        _logger.LogInformation(
            "Reopened archived conversation {ConversationId} for agent={AgentId} channel={ChannelType} address={ChannelAddress}",
            archived.ConversationId, agentId, channelType, channelAddress);

        return archived;
    }

    private Task NotifyChangedAsync(string changeType, AgentId agentId, ConversationId conversationId, CancellationToken ct)
    {
        if (_changeNotifier is null)
            return Task.CompletedTask;

        return _changeNotifier.NotifyConversationChangedAsync(
            changeType,
            agentId.Value,
            conversationId.Value,
            ct);
    }
}
