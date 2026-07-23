using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using Microsoft.Extensions.Logging;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

namespace BotNexus.Gateway.Api.Triggers;

/// <summary>
/// Internal trigger for heartbeat-scheduled sessions (<see cref="TriggerType.Heartbeat"/>).
/// Routes into the agent's active soul session when soul is enabled, keeping heartbeat turns
/// in today's soul context.  Falls back to a stable per-agent heartbeat conversation otherwise.
///
/// Phase 2: after each heartbeat turn, if the assistant response is a heartbeat acknowledgement
/// (contains "HEARTBEAT_OK" within <see cref="AckMaxCharsDefault"/> chars), the user+assistant
/// entries are pruned from the session history and <c>UpdatedAt</c> is restored to its
/// pre-turn value so the session does not appear active.
/// </summary>
public sealed class HeartbeatTrigger(
    IAgentSupervisor supervisor,
    IAgentRegistry registry,
    IConversationStore conversations,
    ISessionStore sessions,
    ILogger<HeartbeatTrigger> logger) : IInternalTrigger
{
    /// <summary>Default max-chars threshold for heartbeat ack classification.</summary>
    public const int AckMaxCharsDefault = 300;

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
            return await RunInSoulSessionAsync(agentId, prompt, descriptor, request, ct).ConfigureAwait(false);

        return await RunInHeartbeatSessionAsync(agentId, prompt, descriptor, request, ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------
    // Soul path - reuse today's active soul session
    // -----------------------------------------------------------------
    private async Task<SessionId> RunInSoulSessionAsync(
        AgentId agentId,
        string prompt,
        AgentDescriptor? descriptor,
        InternalTriggerRequest? request,
        CancellationToken ct)
    {
        // P9-E (#645): soul sessions no longer carry SessionType.Soul. Discovery uses
        // the canonical Metadata["soulDate"] tag that SoulTrigger.InitializeSoulSession
        // stamps on every soul session, gated on Active status.
        var allSessions = await sessions.ListAsync(agentId, ct).ConfigureAwait(false);
        var soulSession = allSessions
            .Where(s => s.Status == GatewaySessionStatus.Active && s.Metadata.ContainsKey("soulDate"))
            .OrderByDescending(s => s.UpdatedAt)
            .FirstOrDefault();

        if (soulSession is null)
        {
            logger.LogDebug(
                "HeartbeatTrigger: no active soul session for '{AgentId}', falling back to heartbeat session.",
                agentId);
            return await RunInHeartbeatSessionAsync(agentId, prompt, descriptor, request, ct).ConfigureAwait(false);
        }

        return await ExecuteInSessionAsync(agentId, soulSession.SessionId, prompt, descriptor, request, ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------
    // Heartbeat path - stable per-agent heartbeat conversation
    // -----------------------------------------------------------------
    private async Task<SessionId> RunInHeartbeatSessionAsync(
        AgentId agentId,
        string prompt,
        AgentDescriptor? descriptor,
        InternalTriggerRequest? request,
        CancellationToken ct)
    {
        var conversation = await GetOrCreateHeartbeatConversationAsync(agentId, ct).ConfigureAwait(false);
        var sessionId = BuildHeartbeatSessionId(agentId);
        var session = await sessions.GetOrCreateAsync(sessionId, agentId, ct).ConfigureAwait(false);

        session.ChannelType = null;
        session.CallerId ??= $"heartbeat:{agentId.Value}";
        // P9-E (#645): heartbeat is agent-self (the agent pings itself); the Heartbeat
        // proxy-trigger kind lives on SessionEntry.Trigger below.
        session.SessionType = SessionType.AgentSelf;
        session.ConversationId = conversation.ConversationId;
        session.Metadata["triggerType"] = Type.Value;

        if (string.IsNullOrWhiteSpace(request?.ModelOverride))
            session.Metadata.Remove("modelOverride");
        else
            session.Metadata["modelOverride"] = request!.ModelOverride;

        if (request?.CronJobId is null)
            session.Metadata.Remove("cronJobId");
        else
            session.Metadata["cronJobId"] = request.CronJobId.Value.Value;

        if (conversation.ActiveSessionId != sessionId)
        {
            conversation.ActiveSessionId = sessionId;
            conversation.UpdatedAt = DateTimeOffset.UtcNow;
            await conversations.SaveAsync(conversation, ct).ConfigureAwait(false);
        }

        return await ExecuteInSessionAsync(agentId, sessionId, prompt, descriptor, request, ct, session).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------
    // Shared execution — with Phase 2 race-safe transcript pruning (#573)
    // -----------------------------------------------------------------
    private async Task<SessionId> ExecuteInSessionAsync(
        AgentId agentId,
        SessionId sessionId,
        string prompt,
        AgentDescriptor? descriptor,
        InternalTriggerRequest? request,
        CancellationToken ct,
        GatewaySession? preloadedSession = null)
    {
        var session = preloadedSession
            ?? await sessions.GetOrCreateAsync(sessionId, agentId, ct).ConfigureAwait(false);

        // --- Phase 2 (race-safe ack-prune, #573) ---------------------------
        // The soul path shares its session with concurrent flows (interactive
        // turns, compaction). Three race windows must be closed:
        //  (1) AddEntry + SnapshotHistoryForCompaction as separate locked
        //      calls leaves a window where a concurrent destructive mutation
        //      shifts the heartbeat user away from snapshot.Count - 1.
        //      => use atomic AddEntryAndSnapshot.
        //  (2) A post-apply assignment to session.UpdatedAt would race with
        //      concurrent AddEntry and clobber legitimate fresh activity
        //      timestamps. => pass restoreUpdatedAtOnApplied so the
        //      restoration happens INSIDE the runtime lock on the Applied
        //      path only.
        //  (3) An UNLOCKED pre-append read of session.UpdatedAt would race
        //      a concurrent ReplaceHistory landing between the read and the
        //      AddEntryAndSnapshot lock — Applied would then restore to a
        //      stale anchor predating that ReplaceHistory. => the prior
        //      UpdatedAt is captured atomically inside AddEntryAndSnapshot
        //      and returned in SessionAppendResult.PriorUpdatedAt.
        // The heartbeat path (RunInHeartbeatSessionAsync) mints a unique
        // sessionId per run so concurrent activity is impossible; the same
        // shared helper applies harmlessly stricter semantics there.
        // P9-E (#645): stamp Heartbeat on the user entry. The Phase-2 ack prune
        // removes both the user and assistant entries when the response is a
        // pure heartbeat ack, so the Trigger stamp disappears together with the
        // text — no audit residue, no observable change for ack-only ticks.
        var appendResult = session.AddEntryAndSnapshot(
            new SessionEntry
            {
                Role = MessageRole.User,
                Content = prompt,
                Trigger = TriggerType.Heartbeat
            });
        var snapshotWithHeartbeat = appendResult.Snapshot;
        var preHeartbeatUpdatedAt = appendResult.PriorUpdatedAt;
        await sessions.SaveAsync(session, ct).ConfigureAwait(false);

        var handle = await supervisor.GetOrCreateAsync(agentId, sessionId, ct).ConfigureAwait(false);
        var response = await handle.PromptAsync(prompt, ct).ConfigureAwait(false);

        var ackMaxChars = descriptor?.Heartbeat?.AckMaxChars ?? AckMaxCharsDefault;

        // #2127 (addendum finding 1): a heartbeat turn that executed tools is NEVER an ack-only
        // no-op, even when the final text looks like an acknowledgement. Pruning it would erase the
        // only durable record that side-effecting tools ran. Persist the user turn, the tool
        // timeline, and the assistant text so the audit trail survives instead of being pruned.
        if (TriggerToolAuditProjector.HasToolActivity(response))
        {
            foreach (var toolEntry in TriggerToolAuditProjector.ProjectToolEntries(response))
                session.AddEntry(toolEntry);
            session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = response.Content });
            await sessions.SaveAsync(session, ct).ConfigureAwait(false);

            logger.LogInformation(
                "HeartbeatTrigger: tool-active turn from agent '{AgentId}' session '{SessionId}' - ack-prune skipped to preserve tool audit records (jobId: {JobId}).",
                agentId, sessionId, request?.CronJobId);

            return sessionId;
        }

        if (IsHeartbeatAck(response.Content, ackMaxChars))
        {
            // Replacement = snapshot minus its last entry (the heartbeat user
            // we just appended). The atomic AddEntryAndSnapshot guaranteed
            // that the heartbeat user is at index snapshot.Count - 1.
            var replacement = snapshotWithHeartbeat.Entries
                .Take(snapshotWithHeartbeat.Count - 1)
                .ToArray();
            var outcome = session.TryReplaceHistoryFromSnapshot(
                replacement,
                snapshotWithHeartbeat.DestructiveVersion,
                snapshotWithHeartbeat.Count,
                restoreUpdatedAtOnApplied: preHeartbeatUpdatedAt);

            switch (outcome)
            {
                case HistoryReplaceOutcome.Applied:
                    logger.LogDebug(
                        "HeartbeatTrigger: ack from agent '{AgentId}' session '{SessionId}' — turn pruned, UpdatedAt restored.",
                        agentId, sessionId);
                    break;

                case HistoryReplaceOutcome.Rebased:
                    logger.LogDebug(
                        "HeartbeatTrigger: ack from agent '{AgentId}' session '{SessionId}' — turn pruned; concurrent activity preserved, UpdatedAt left at concurrent time.",
                        agentId, sessionId);
                    break;

                case HistoryReplaceOutcome.Aborted:
                    logger.LogWarning(
                        "HeartbeatTrigger: ack from agent '{AgentId}' session '{SessionId}' — concurrent destructive mutation detected; ack-prune aborted to preserve compactor work. The heartbeat user turn was already replaced by the concurrent mutation.",
                        agentId, sessionId);
                    break;
            }

            await sessions.SaveAsync(session, ct).ConfigureAwait(false);
        }
        else
        {
            session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = response.Content });
            await sessions.SaveAsync(session, ct).ConfigureAwait(false);

            logger.LogInformation(
                "HeartbeatTrigger: substantive response from agent '{AgentId}' session '{SessionId}' (jobId: {JobId}).",
                agentId, sessionId, request?.CronJobId);
        }

        return sessionId;
    }

    // -----------------------------------------------------------------
    // Ack classification (Phase 2)
    // -----------------------------------------------------------------

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="response"/> is a heartbeat
    /// acknowledgement: it contains "HEARTBEAT_OK" and is no longer than
    /// <paramref name="maxChars"/> characters.
    /// </summary>
    public static bool IsHeartbeatAck(string? response, int maxChars = AckMaxCharsDefault)
    {
        if (string.IsNullOrWhiteSpace(response))
            return false;

        var trimmed = response.Trim();

        if (trimmed.Length > maxChars)
            return false;

        return trimmed.Equals("HEARTBEAT_OK", StringComparison.Ordinal)
               || trimmed.StartsWith("HEARTBEAT_OK", StringComparison.Ordinal)
               || trimmed.Contains("HEARTBEAT_OK", StringComparison.Ordinal);
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
            IsDefault = false,
            // Heartbeat is a self-initiated system flow; the agent itself is the initiator.
            Initiator = CitizenId.Of(agentId)
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
            // Race: another run created it first - re-resolve
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
