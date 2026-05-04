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
            if (threadId is not null)
            {
                // A non-null thread id means this is a distinct sub-channel (e.g. Telegram topic).
                // Create a new conversation so the thread gets its own history.
                conversation = new Conversation
                {
                    ConversationId = ConversationId.Create(),
                    AgentId = agentId,
                    Title = $"{channelType}:{channelAddress}/{threadId}",
                    IsDefault = false
                };
                _logger.LogDebug(
                    "Creating new conversation for thread agent={AgentId} channel={ChannelType} address={ChannelAddress} thread={ThreadId}",
                    agentId, channelType, channelAddress, threadId);
            }
            else
            {
                // 2. Fall back to the agent's default conversation and add a binding
                conversation = await _conversationStore.GetOrCreateDefaultAsync(agentId, ct);
                _logger.LogDebug(
                    "No conversation found for agent={AgentId} channel={ChannelType} address={ChannelAddress}. Using default conversation {ConversationId}",
                    agentId, channelType, channelAddress, conversation.ConversationId);
            }

            var binding = new ChannelBinding
            {
                ChannelType = channelType,
                ChannelAddress = channelAddress,
                ThreadId = threadId,
                Mode = BindingMode.Interactive
            };
            conversation.ChannelBindings.Add(binding);
            addedBinding = true;
        }

        // 3. Resolve or create the active session
        var isNewSession = false;
        var conversationChanged = addedBinding;
        SessionId sessionId;

        if (conversation.ActiveSessionId.HasValue)
        {
            // Verify the active session is still usable (not sealed/archived)
            var existingSession = await _sessionStore.GetAsync(conversation.ActiveSessionId.Value, ct);
            if (existingSession is { Status: not SessionStatus.Sealed and not SessionStatus.Expired })
            {
                sessionId = conversation.ActiveSessionId.Value;
                _logger.LogDebug("Reusing active session {SessionId} for conversation {ConversationId}", sessionId, conversation.ConversationId);
            }
            else
            {
                // Active session is sealed/expired — create a new one
                sessionId = SessionId.Create();
                isNewSession = true;
                conversation.ActiveSessionId = null; // will be stamped below
                conversationChanged = true;
                _logger.LogDebug(
                    "Active session {OldSessionId} is no longer usable; creating new session {NewSessionId} for conversation {ConversationId}",
                    conversation.ActiveSessionId, sessionId, conversation.ConversationId);
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
            // GetOrCreateAsync returned a different session — treat as new
            sessionId = session.SessionId;
            isNewSession = true;
        }


        // 4. Stamp ConversationId and ActiveSessionId if not already set
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
        var isNewSession = false;
        SessionId sessionId;

        if (conversation.ActiveSessionId.HasValue)
        {
            var existingSession = await _sessionStore.GetAsync(conversation.ActiveSessionId.Value, ct);
            if (existingSession is { Status: not SessionStatus.Sealed and not SessionStatus.Expired })
            {
                sessionId = conversation.ActiveSessionId.Value;
            }
            else
            {
                sessionId = SessionId.Create();
                isNewSession = true;
                conversation.ActiveSessionId = null;
            }
        }
        else
        {
            sessionId = SessionId.Create();
            isNewSession = true;
        }

        var session = await _sessionStore.GetOrCreateAsync(sessionId, agentId, ct);
        sessionId = session.SessionId;

        var changed = false;
        if (session.Session.ConversationId is null || session.Session.ConversationId != conversation.ConversationId)
        {
            session.Session.ConversationId = conversation.ConversationId;
            await _sessionStore.SaveAsync(session, ct);
        }

        if (conversation.ActiveSessionId is null || conversation.ActiveSessionId != sessionId)
        {
            conversation.ActiveSessionId = sessionId;
            changed = true;
        }

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
}
