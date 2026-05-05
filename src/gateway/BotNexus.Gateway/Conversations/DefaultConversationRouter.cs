using BotNexus.Domain.Primitives;
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
    private readonly ILogger<DefaultConversationRouter> _logger;

    public DefaultConversationRouter(
        IConversationStore conversationStore,
        ISessionStore sessionStore,
        ILogger<DefaultConversationRouter> logger)
    {
        _conversationStore = conversationStore;
        _sessionStore = sessionStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ConversationRoutingResult> ResolveInboundAsync(
        AgentId agentId,
        ChannelKey channelType,
        string channelAddress,
        string? threadId,
        CancellationToken ct = default)
    {
        // 1. Try to find an existing conversation by binding
        var conversation = await _conversationStore.ResolveByBindingAsync(
            agentId, channelType, channelAddress, threadId, ct);

        var addedBinding = false;
        if (conversation is null)
        {
            // Every unique (channelType, channelAddress, threadId) gets its own conversation.
            // There is no special "default" conversation for addressless channels — an empty
            // address is a valid stable identity (e.g. a future channel with no external ID).
            var title = threadId is not null
                ? $"{channelType}:{channelAddress}/{threadId}"
                : $"{channelType}:{channelAddress}";
            conversation = new Conversation
            {
                ConversationId = ConversationId.Create(),
                AgentId = agentId,
                Title = title,
                IsDefault = false
            };
            var binding = new ChannelBinding
            {
                ChannelType = channelType,
                ChannelAddress = channelAddress,
                ThreadId = threadId,
                Mode = BindingMode.Interactive
            };
            conversation.ChannelBindings.Add(binding);
            addedBinding = true;
            _logger.LogDebug(
                "Creating new conversation for agent={AgentId} channel={ChannelType} address={ChannelAddress} thread={ThreadId}",
                agentId, channelType, channelAddress, threadId);
        }

        // 3. Resolve or create the active session
        var conversationChanged = addedBinding;
        var (sessionId, isNewSession, sessionChanged) = await ResolveOrCreateSessionAsync(conversation, agentId, ct);
        conversationChanged |= sessionChanged;

        if (conversationChanged)
        {
            conversation.UpdatedAt = DateTimeOffset.UtcNow;
            await _conversationStore.SaveAsync(conversation, ct);
        }

        // Resolve the originating binding so callers don't need a second lookup into the binding list.
        var originatingBinding = conversation.ChannelBindings
            .FirstOrDefault(b =>
                b.ChannelType == channelType &&
                string.Equals(b.ChannelAddress, channelAddress, StringComparison.Ordinal) &&
                string.Equals(b.ThreadId, threadId, StringComparison.Ordinal));

        return new ConversationRoutingResult(conversation, sessionId, isNewSession, originatingBinding);
    }

    /// <inheritdoc />
    public async Task<ConversationRoutingResult> ResolveInboundByConversationAsync(
        ConversationId conversationId,
        AgentId agentId,
        ChannelKey channelType,
        string channelAddress,
        CancellationToken ct = default)
    {
        var conversation = await _conversationStore.GetAsync(conversationId, ct);
        if (conversation is null)
        {
            _logger.LogWarning(
                "ResolveInboundByConversation: conversation {ConversationId} not found — falling back to default routing",
                conversationId);
            return await ResolveInboundAsync(agentId, channelType, channelAddress, null, ct);
        }

        // Resolve or create session for this conversation
        var (sessionId, isNewSession, changed) = await ResolveOrCreateSessionAsync(conversation, agentId, ct);

        if (changed)
        {
            conversation.UpdatedAt = DateTimeOffset.UtcNow;
            await _conversationStore.SaveAsync(conversation, ct);
        }

        return new ConversationRoutingResult(conversation, sessionId, isNewSession);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChannelBinding>> GetOutboundBindingsAsync(
        SessionId sessionId,
        string? originatingBindingId,
        CancellationToken ct = default)
    {
        // 1. Resolve the session to get ConversationId
        var session = await _sessionStore.GetAsync(sessionId, ct);
        if (session is null)
        {
            _logger.LogDebug("GetOutboundBindings: session {SessionId} not found — returning empty", sessionId);
            return [];
        }

        var conversationId = session.Session.ConversationId;
        if (conversationId is null)
        {
            _logger.LogDebug("GetOutboundBindings: session {SessionId} has no ConversationId — returning empty", sessionId);
            return [];
        }

        var conversation = await _conversationStore.GetAsync(conversationId.Value, ct);
        if (conversation is null)
        {
            _logger.LogDebug("GetOutboundBindings: conversation {ConversationId} not found — returning empty", conversationId);
            return [];
        }

        // 2. Filter bindings: not muted, not the originating binding
        return conversation.ChannelBindings
            .Where(b => b.Mode != BindingMode.Muted)
            .Where(b => originatingBindingId is null || !string.Equals(b.BindingId, originatingBindingId, StringComparison.Ordinal))
            .ToList();
    }

    /// <inheritdoc />
    public async Task MuteBindingAsync(ConversationId conversationId, string bindingId, CancellationToken ct = default)
    {
        var conversation = await _conversationStore.GetAsync(conversationId, ct);
        if (conversation is null)
        {
            _logger.LogDebug("MuteBinding: conversation {ConversationId} not found", conversationId);
            return;
        }

        var binding = conversation.ChannelBindings.FirstOrDefault(b =>
            string.Equals(b.BindingId, bindingId, StringComparison.Ordinal));

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
    public async Task MuteBindingByAddressAsync(AgentId? agentId, ChannelKey channelType, string channelAddress, CancellationToken ct = default)
    {
        var conversations = await _conversationStore.ListAsync(agentId, ct);
        foreach (var conversation in conversations)
        {
            var binding = conversation.ChannelBindings.FirstOrDefault(b =>
                b.ChannelType.Equals(channelType) &&
                string.Equals(b.ChannelAddress, channelAddress, StringComparison.Ordinal) &&
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
    public async Task ReattachBindingAsync(string bindingId, ConversationId targetConversationId, CancellationToken ct = default)
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
                string.Equals(b.BindingId, bindingId, StringComparison.Ordinal));

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
        Conversation conversation, AgentId agentId, CancellationToken ct)
    {
        SessionId sessionId;
        var isNewSession = false;
        var conversationChanged = false;

        if (conversation.ActiveSessionId.HasValue)
        {
            var existingSession = await _sessionStore.GetAsync(conversation.ActiveSessionId.Value, ct);
            if (existingSession is { Status: not SessionStatus.Sealed and not SessionStatus.Expired })
            {
                sessionId = conversation.ActiveSessionId.Value;
                _logger.LogDebug("Reusing active session {SessionId} for conversation {ConversationId}", sessionId, conversation.ConversationId);
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

        if (session.Session.ConversationId is null || session.Session.ConversationId != conversation.ConversationId)
        {
            session.Session.ConversationId = conversation.ConversationId;
            await _sessionStore.SaveAsync(session, ct);
        }

        if (conversation.ActiveSessionId is null || conversation.ActiveSessionId != sessionId)
        {
            conversation.ActiveSessionId = sessionId;
            conversationChanged = true;
        }

        return (sessionId, isNewSession, conversationChanged);
    }
}
