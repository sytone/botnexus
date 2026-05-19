using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Logging;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

namespace BotNexus.Gateway.Api.Triggers;

/// <summary>
/// Internal trigger for heartbeat-scheduled sessions (<see cref="TriggerType.Heartbeat"/>).
/// Routes into the agent's active soul session when soul is enabled, keeping heartbeat turns
/// in today's soul context.  Falls back to a stable per-agent heartbeat conversation otherwise.
/// </summary>
public sealed class HeartbeatTrigger(
    IAgentSupervisor supervisor,
    IAgentRegistry registry,
    IConversationStore conversations,
    ISessionStore sessions,
    ILogger<HeartbeatTrigger> logger) : IInternalTrigger
{
    /// <inheritdoc/>
    public TriggerType Type => TriggerType.Heartbeat;

    /// <inheritdoc/>
    public string DisplayName => "Heartbeat";

    /// <inheritdoc/>
    public async Task<SessionId> CreateSessionAsync(
        AgentId agentId,
        string prompt,
        CancellationToken ct = default,
        InternalTriggerRequest? request = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var descriptor = registry.Get(agentId);

        if (descriptor?.Soul?.Enabled == true)
            return await RunInSoulSessionAsync(agentId, prompt, request, ct).ConfigureAwait(false);

        return await RunInHeartbeatSessionAsync(agentId, prompt, request, ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------
    // Soul path — reuse today's active soul session
    // -----------------------------------------------------------------

    private async Task<SessionId> RunInSoulSessionAsync(
        AgentId agentId,
        string prompt,
        InternalTriggerRequest? request,
        CancellationToken ct)
    {
        var allSessions = await sessions.ListAsync(agentId, ct).ConfigureAwait(false);
        var soulSession = allSessions
            .Where(s => s.SessionType == SessionType.Soul && s.Status == GatewaySessionStatus.Active)
            .OrderByDescending(s => s.UpdatedAt)
            .FirstOrDefault();

        if (soulSession is null)
        {
            logger.LogDebug(
                "HeartbeatTrigger: no active soul session for '{AgentId}', falling back to heartbeat session.",
                agentId);
            return await RunInHeartbeatSessionAsync(agentId, prompt, request, ct).ConfigureAwait(false);
        }

        return await ExecuteInSessionAsync(agentId, soulSession.SessionId, prompt, request, ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------
    // Heartbeat path — stable per-agent heartbeat conversation
    // -----------------------------------------------------------------

    private async Task<SessionId> RunInHeartbeatSessionAsync(
        AgentId agentId,
        string prompt,
        InternalTriggerRequest? request,
        CancellationToken ct)
    {
        var conversation = await GetOrCreateHeartbeatConversationAsync(agentId, ct).ConfigureAwait(false);

        var sessionId = BuildHeartbeatSessionId(agentId);
        var session = await sessions.GetOrCreateAsync(sessionId, agentId, ct).ConfigureAwait(false);
        session.ChannelType = null;
        session.CallerId ??= $"heartbeat:{agentId.Value}";
        session.SessionType = SessionType.Heartbeat;
        session.Session.ConversationId = conversation.ConversationId;
        session.Metadata["triggerType"] = Type.Value;

        if (string.IsNullOrWhiteSpace(request?.ModelOverride))
            session.Metadata.Remove("modelOverride");
        else
            session.Metadata["modelOverride"] = request!.ModelOverride;

        if (string.IsNullOrWhiteSpace(request?.CronJobId))
            session.Metadata.Remove("cronJobId");
        else
            session.Metadata["cronJobId"] = request!.CronJobId;

        if (conversation.ActiveSessionId != sessionId)
        {
            conversation.ActiveSessionId = sessionId;
            conversation.UpdatedAt = DateTimeOffset.UtcNow;
            await conversations.SaveAsync(conversation, ct).ConfigureAwait(false);
        }

        return await ExecuteInSessionAsync(agentId, sessionId, prompt, request, ct, session).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------
    // Shared execution
    // -----------------------------------------------------------------

    private async Task<SessionId> ExecuteInSessionAsync(
        AgentId agentId,
        SessionId sessionId,
        string prompt,
        InternalTriggerRequest? request,
        CancellationToken ct,
        GatewaySession? preloadedSession = null)
    {
        var session = preloadedSession
            ?? await sessions.GetOrCreateAsync(sessionId, agentId, ct).ConfigureAwait(false);

        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = prompt });
        await sessions.SaveAsync(session, ct).ConfigureAwait(false);

        var handle = await supervisor.GetOrCreateAsync(agentId, sessionId, ct).ConfigureAwait(false);
        var response = await handle.PromptAsync(prompt, ct).ConfigureAwait(false);

        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = response.Content });
        await sessions.SaveAsync(session, ct).ConfigureAwait(false);

        logger.LogInformation(
            "HeartbeatTrigger: session '{SessionId}' for agent '{AgentId}' (jobId: {JobId}).",
            sessionId, agentId, request?.CronJobId);

        return sessionId;
    }

    // -----------------------------------------------------------------
    // Conversation management
    // -----------------------------------------------------------------

    private async Task<Conversation> GetOrCreateHeartbeatConversationAsync(AgentId agentId, CancellationToken ct)
    {
        var stableId = BuildHeartbeatConversationId(agentId);
        var existing = await conversations.GetAsync(stableId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Status == BotNexus.Gateway.Abstractions.Models.ConversationStatus.Archived)
            {
                existing.Status = BotNexus.Gateway.Abstractions.Models.ConversationStatus.Active;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                await conversations.SaveAsync(existing, ct).ConfigureAwait(false);
            }
            return existing;
        }

        var conversation = new Conversation
        {
            ConversationId = stableId,
            AgentId = agentId,
            Title = $"heartbeat:{agentId.Value}",
            IsDefault = false
        };

        try
        {
            await conversations.CreateAsync(conversation, ct).ConfigureAwait(false);
            logger.LogInformation(
                "HeartbeatTrigger: created heartbeat conversation '{ConversationId}' for agent '{AgentId}'.",
                stableId, agentId);
            return conversation;
        }
        catch (Exception ex)
        {
            // Race: another run created it first — re-resolve
            logger.LogDebug(ex, "HeartbeatTrigger: create race for conversation '{ConversationId}', retrying.", stableId);
            var resolved = await conversations.GetAsync(stableId, ct).ConfigureAwait(false);
            if (resolved is not null) return resolved; throw;
        }
    }

    // -----------------------------------------------------------------
    // ID helpers
    // -----------------------------------------------------------------

    private static SessionId BuildHeartbeatSessionId(AgentId agentId)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return SessionId.From($"heartbeat:{Sanitize(agentId.Value)}:{timestamp}:{suffix}");
    }

    private static ConversationId BuildHeartbeatConversationId(AgentId agentId)
        => ConversationId.From($"heartbeatconv:{Sanitize(agentId.Value)}");

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "agent";

        Span<char> buf = stackalloc char[Math.Min(40, value.Length)];
        var len = 0;
        foreach (var ch in value)
        {
            if (len >= buf.Length) break;
            buf[len++] = char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-';
        }

        return new string(buf[..len]).Trim('-');
    }
}
