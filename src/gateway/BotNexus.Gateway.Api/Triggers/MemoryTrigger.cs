using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Gateway.Streaming;
using Microsoft.Extensions.Logging;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

namespace BotNexus.Gateway.Api.Triggers;

/// <summary>
/// Internal trigger for session-end memory flushes (<see cref="TriggerType.Memory"/>).
/// </summary>
/// <remarks>
/// <para>
/// #3543: <c>SessionEndMemoryFlusher</c> has always asked DI for a <see cref="TriggerType.Memory"/>
/// trigger, but no such implementation existed, so every <c>/reset</c> flush fell through to
/// <c>CronTrigger</c>. With no job id in hand that path minted a malformed three-segment
/// <c>cron:&lt;timestamp&gt;:&lt;guid&gt;</c> session id, which the portal then rendered as a session
/// divider in the middle of a human conversation - indistinguishable from cron poisoning, and
/// unclaimable by cron's own job-scoped session scans. This type is the missing implementation: the
/// flush now runs under its own namespace and is honestly attributable to the memory subsystem.
/// </para>
/// <para>
/// The flush is an <b>agent-self</b> turn: the agent is writing its own memory, not talking to a
/// citizen. It is therefore stamped <see cref="SessionType.AgentSelf"/> with a null channel, which
/// makes <see cref="Session.IsInteractive"/> false and guarantees the flush can never itself
/// trigger another flush.
/// </para>
/// <para>
/// The trigger deliberately does <b>not</b> claim <c>Conversation.ActiveSessionId</c>. The flush is
/// a background bookkeeping turn attached to the conversation being reset; stealing the portal's
/// active-session pointer is the user-visible half of the defect this type fixes.
/// </para>
/// </remarks>
public sealed class MemoryTrigger(
    IAgentSupervisor supervisor,
    ISessionStore sessions,
    ILogger<MemoryTrigger> logger) : IInternalTrigger
{
    /// <inheritdoc/>
    public TriggerType Type => TriggerType.Memory;

    /// <inheritdoc/>
    public string DisplayName => "Memory Flush";

    /// <summary>
    /// Runs one memory-flush turn in a fresh, non-interactive session bound to the conversation
    /// supplied by the caller.
    /// </summary>
    /// <param name="agentId">The agent whose memory is being flushed.</param>
    /// <param name="prompt">The session-end flush prompt.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="request">Optional trigger metadata; only <c>ConversationId</c> and <c>ModelOverride</c> are honoured.</param>
    public async Task<SessionId> CreateSessionAsync(
        AgentId agentId,
        string prompt,
        CancellationToken ct = default,
        InternalTriggerRequest? request = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var sessionId = BuildMemorySessionId(agentId);
        var session = await sessions.GetOrCreateAsync(sessionId, agentId, ct).ConfigureAwait(false);

        // Agent-self, no channel: Session.IsInteractive is false, so a flush can never recurse
        // into another flush and never surfaces through interactive-only side-effects.
        session.SessionType = SessionType.AgentSelf;
        session.ChannelType = null;
        session.CallerId ??= $"{Type.Value}:{agentId.Value}";
        session.Metadata["triggerType"] = Type.Value;

        if (request?.ConversationId is { } conversationId)
        {
            request.ResolvedConversationId ??= conversationId;
            session.ConversationId = conversationId;
        }

        if (string.IsNullOrWhiteSpace(request?.ModelOverride))
            session.Metadata.Remove("modelOverride");
        else
            session.Metadata["modelOverride"] = request!.ModelOverride;

        // Write-ahead save so the agent handle binds to the right conversation and model before the
        // turn runs. The user entry is added AFTER PromptAsync so the handle does not load the flush
        // prompt as prior history and then receive it again (the #656 duplicate-prompt shape).
        await sessions.SaveAsync(session, ct).ConfigureAwait(false);

        try
        {
            var handle = await supervisor.GetOrCreateAsync(agentId, sessionId, ct).ConfigureAwait(false);
            var response = await handle.PromptAsync(prompt, ct).ConfigureAwait(false);

            ProviderTokenUsageRecorder.Record(session, response.Usage);

            session.AddEntry(new SessionEntry
            {
                Role = MessageRole.User,
                Content = prompt,
                Trigger = TriggerType.Memory
            });

            // A memory flush is exactly the turn whose tool activity matters most - it is nothing
            // but tool calls that write to disk. Project the same durable tool timeline the other
            // blocking trigger paths persist (#2127).
            foreach (var toolEntry in TriggerToolAuditProjector.ProjectToolEntries(response))
                session.AddEntry(toolEntry);

            session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = response.Content });
        }
        finally
        {
            if (session.Status == GatewaySessionStatus.Active)
                session.Status = GatewaySessionStatus.Sealed;

            // Independent token: the flusher runs on a timeout-linked token during /reset, so
            // cancellation must not skip sealing and leave an orphan Active session behind.
            await sessions.SaveAsync(session, CancellationToken.None).ConfigureAwait(false);
        }

        logger.LogInformation(
            "Memory trigger created session '{SessionId}' for agent '{AgentId}' in conversation '{ConversationId}'.",
            sessionId,
            agentId,
            request?.ConversationId);

        return sessionId;
    }

    /// <summary>
    /// Mints the memory-flush session id <c>memory:&lt;agent&gt;:&lt;timestamp&gt;:&lt;suffix&gt;</c>.
    /// Mirrors the heartbeat shape and, critically, is NOT in the <c>cron:</c> namespace - cron's
    /// job-scoped session scans must never see a row that belongs to no job (#3543).
    /// </summary>
    private static SessionId BuildMemorySessionId(AgentId agentId)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return SessionId.From($"memory:{Sanitize(agentId.Value)}:{timestamp}:{suffix}");
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "agent";

        Span<char> buffer = stackalloc char[Math.Min(40, value.Length)];
        var length = 0;
        foreach (var ch in value)
        {
            if (length >= buffer.Length)
                break;

            buffer[length++] = char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-';
        }

        return new string(buffer[..length]).Trim('-');
    }
}
