using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.Text;
using BotNexus.Domain.World;
using BotNexus.Gateway.Streaming;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Api.Triggers;

/// <summary>
/// Internal trigger used for cron-triggered sessions.
///
/// Conversation ownership is inverted under P9-D (directive G-5):
/// • <see cref="BotNexus.Cron.CronJob.ConversationId"/> is the canonical link from a job to its long-lived conversation.
/// • If <see cref="InternalTriggerRequest.ConversationId"/> is set (the scheduler loaded a pinned job), the trigger
///   reuses that conversation verbatim — no per-agent enumeration, no title matching, no composite-id construction.
/// • Otherwise the trigger creates a fresh conversation with a random GUID id, titled after the job, with the
///   initiator derived from <see cref="InternalTriggerRequest.CreatedBy"/> (parsed via
///   <see cref="CitizenId.TryParse(string?, out CitizenId)"/>) or falling back to the agent itself for system-provisioned jobs.
/// • Race safety: when two parallel runs create different conversations, the scheduler's CAS
///   (<see cref="BotNexus.Cron.ICronStore.TrySetConversationIdAsync"/>) picks one winner; the loser archives its
///   conversation and rebinds its session. See <see cref="BotNexus.Cron.CronScheduler"/>.
/// </summary>
public sealed class CronTrigger(
    IAgentSupervisor supervisor,
    IConversationStore conversations,
    ISessionStore sessions,
    ILogger<CronTrigger> logger,
    IConversationChangeNotifier? changeNotifier = null) : IInternalTrigger
{
    /// <summary>
    /// Gets the trigger type identifier.
    /// </summary>
    public TriggerType Type => TriggerType.Cron;

    /// <summary>
    /// Gets the display name for the trigger.
    /// </summary>
    public string DisplayName => "Cron Scheduler";

    /// <summary>
    /// Creates a fresh session for a single cron run and routes it into the job's conversation.
    /// </summary>
    /// <param name="agentId">The agent the cron job belongs to.</param>
    /// <param name="prompt">The cron-supplied prompt text.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="request">Trigger metadata including the job id, the optional pinned conversation, and the citizen who scheduled the job.</param>
    public async Task<SessionId> CreateSessionAsync(
        AgentId agentId,
        string prompt,
        CancellationToken ct = default,
        InternalTriggerRequest? request = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var (conversation, createdFreshConversation) = await ResolveOrCreateConversationAsync(agentId, request, ct).ConfigureAwait(false);

        if (request is not null && request.ResolvedConversationId is null)
            request.ResolvedConversationId = conversation.ConversationId;

        var sessionId = BuildCronSessionId(request?.CronJobId);
        var session = await sessions.GetOrCreateAsync(sessionId, agentId, ct).ConfigureAwait(false);
        session.ChannelType ??= ChannelKey.From(Type.Value);
        session.CallerId ??= $"{Type.Value}:{agentId.Value}";
        // P9-E (#645): cron is a proxy for the citizen who scheduled it (directive W-2),
        // so the session shape is UserAgent — the Cron trigger kind is per-turn and
        // lives on SessionEntry.Trigger below. Session.IsInteractive excludes the
        // "cron" channel so memory flushers / warmup still ignore cron sessions.
        session.SessionType = SessionType.UserAgent;
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

        // Only claim ActiveSessionId if no human (SignalR) session currently holds it.
        // A cron session must never evict a human session from the portal-facing pointer.
        // If a user has intentionally pinned a cron job to their conversation, the cron
        // messages still appear live (SignalR routes by ConversationId group, not session),
        // but the portal stays connected to the human session. (#867)
        var existingIsHumanSession = conversation.ActiveSessionId is { } existingId
            && !existingId.Value.StartsWith("cron:", StringComparison.Ordinal);

        if (!existingIsHumanSession &&
            (conversation.ActiveSessionId is null || conversation.ActiveSessionId != sessionId))
        {
            conversation.ActiveSessionId = sessionId;
            conversation.UpdatedAt = DateTimeOffset.UtcNow;
            await conversations.SaveAsync(conversation, ct).ConfigureAwait(false);
        }

        // Save session metadata before creating the agent handle so the handle picks up
        // the correct conversation binding and model override — but do NOT add the user
        // entry yet. Adding it here would cause the handle to load it as prior history
        // and then receive the same prompt again via PromptAsync, giving the model a
        // duplicate user message and suppressing tool call execution. (#656)
        await sessions.SaveAsync(session, ct).ConfigureAwait(false);

        var retainSession = true;
        try
        {
            var handle = await supervisor.GetOrCreateAsync(agentId, sessionId, ct).ConfigureAwait(false);

            AgentResponse response;
            try
            {
                response = await handle.PromptAsync(prompt, ct).ConfigureAwait(false);

                // #2985: report the turn's tool-invocation count back to the caller (the cron
                // scheduler, via AgentPromptAction) BEFORE any early-return path below, so the
                // no-op-turn branch - the very shape that produced the fabricated maintenance
                // runs - still reports its zero. The count is taken from the same
                // response.ToolCalls collection the transcript projection uses, so the recorded
                // outcome and the persisted tool rows can never disagree.
                // #2641: the same write-back now also carries turn count and aggregated token
                // usage, through one helper shared with SoulTrigger.
                TriggerRunCostRecorder.Record(request, response);

                // #2522 residual: the blocking path must stamp the provider's reported prompt-token
                // count exactly as the streaming path does at MessageEnd. Without it, long-lived
                // cron sessions - the ones that compact most often - present a null provider count
                // to LlmSessionCompactor, so the unit normalisation shipped by #2717 silently falls
                // back to the unscaled keep-recent budget and the fix is inert where it matters most.
                ProviderTokenUsageRecorder.Record(session, response.Usage);
            }
            catch (AgentPromptInterruptedException interrupted)
            {
                // #2118: the run was cancelled or timed out mid-flight, but tools may have executed
                // before the interruption. Persist the user entry plus the captured tool timeline
                // (completed tools + an interrupted-tool row for any in-flight call) so the transcript
                // reflects the work that actually happened, then re-surface the cancellation. The
                // assistant text row is intentionally omitted - the turn never produced a final answer.
                session.AddEntry(new SessionEntry
                {
                    Role = MessageRole.User,
                    Content = prompt,
                    Trigger = TriggerType.Cron
                });
                foreach (var toolEntry in ProjectToolEntries(interrupted.PartialResponse))
                    session.AddEntry(toolEntry);

                // #2641 AC1: an interrupted run still cost what it cost before it was interrupted,
                // and cron's timeout path routes through here. Without this write-back the runs
                // most worth measuring - the ones expensive enough to hit their timeout - would be
                // the only ones recorded as unmeasured. The partial response carries the same
                // aggregate the completed path uses.
                TriggerRunCostRecorder.Record(request, interrupted.PartialResponse);

                // Re-surface as a normal cancellation so upstream cancellation/timeout handling is
                // unchanged; the interrupted exception is chained as the inner exception for diagnostics.
                throw new OperationCanceledException(
                    "Cron run was interrupted (cancelled or timed out) mid-flight.",
                    interrupted,
                    interrupted.CancellationToken);
            }

            // #1722 Part A: a wake whose turn produced nothing - the response is NO_REPLY or
            // whitespace AND no tool calls fired - is a pure no-op. Persisting it would leave a
            // full cron session plus two history rows (user + empty assistant) per silent wake,
            // which dominates the store over time. Treat it as a no-op: skip the user/assistant
            // entries entirely. If we also created the transient conv:<guid> for this run (no
            // human pin), dispose it - delete the cron session and archive the throwaway
            // conversation. A pinned/human/default conversation is never deleted or archived;
            // only the trigger's own per-run conversation is disposable.
            if (IsNoOpTurn(response))
            {
                logger.LogInformation(
                    "Cron trigger no-op for agent '{AgentId}' job '{JobId}': empty turn (no reply, no tools); skipping persistence.",
                    agentId,
                    request?.CronJobId);

                if (createdFreshConversation && IsDisposableTransientConversation(conversation, sessionId))
                {
                    await sessions.DeleteAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                    retainSession = false;
                    await conversations.ArchiveAsync(conversation.ConversationId, "cron-transient-cleanup", sessionId.Value, "system", CancellationToken.None).ConfigureAwait(false);
                }

                return sessionId;
            }

            // P9-E (#645): stamp Cron on the user entry. Cron is a proxy for the citizen
            // who scheduled the job - the persisted trigger marks the originating proxy
            // so audit/UI can render the right badge without sniffing SessionType.
            // Persisted AFTER PromptAsync so session history never contains the user entry
            // as prior context when the agent handle is created for this run.
            session.AddEntry(new SessionEntry
            {
                Role = MessageRole.User,
                Content = prompt,
                Trigger = TriggerType.Cron
            });

            // #2118: project each tool call the turn executed into ordered tool rows (id, name,
            // arguments, result content, error) so a completed cron session shows the same tool
            // timeline the interactive streaming path persists. Placed between the user and
            // assistant entries to preserve execution order.
            foreach (var toolEntry in ProjectToolEntries(response))
                session.AddEntry(toolEntry);

            session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = response.Content });
        }
        finally
        {
            // CronTrigger owns every in-process terminal path after the Active write-ahead save.
            // Use an independent token so cancellation, timeout, and host shutdown cannot skip it.
            // A deleted disposable no-op session is the sole path that has no row to seal.
            if (retainSession && session.Status == SessionStatus.Active)
            {
                session.Status = SessionStatus.Sealed;
                await sessions.SaveAsync(session, CancellationToken.None).ConfigureAwait(false);
            }
        }

        logger.LogInformation(
            "Cron trigger created session '{SessionId}' for agent '{AgentId}' in conversation '{ConversationId}' (jobId: {JobId}, model: {ModelOverride}).",
            sessionId,
            agentId,
            conversation.ConversationId,
            request?.CronJobId,
            request?.ModelOverride);

        return sessionId;
    }

    /// <summary>
    /// A cron turn produced no real work when the assistant content is empty/whitespace or the
    /// silent-reply sentinel <c>NO_REPLY</c>, AND no tool calls executed. Mirrors the GatewayHost
    /// NO_REPLY suppression (#1237) plus a tool-activity guard so a silent-but-acted turn (e.g.
    /// memory write then NO_REPLY) is still treated as work and persisted.
    /// </summary>
    private static bool IsNoOpTurn(AgentResponse response)
    {
        if (response.ToolCalls.Count > 0)
            return false;

        var content = response.Content;
        if (string.IsNullOrWhiteSpace(content))
            return true;

        return content.Trim().Equals("NO_REPLY", StringComparison.Ordinal);
    }

    /// <summary>
    /// Projects the tool calls carried on an <see cref="AgentResponse"/> into ordered
    /// <see cref="MessageRole.Tool"/> history entries, mirroring the tool rows the interactive
    /// streaming path persists (issue #2118). Each call yields a single row carrying the tool call
    /// id, name, serialized arguments, result content, and error state. A call that never completed
    /// (cancelled/timed-out mid-flight, <see cref="AgentToolCallInfo.IsIncomplete"/>) is rendered
    /// with a synthesized "did not complete" body and an error flag so the transcript stays
    /// consistent with the streaming orphan-synthesis behaviour.
    /// </summary>
    private static IEnumerable<SessionEntry> ProjectToolEntries(AgentResponse response)
        => TriggerToolAuditProjector.ProjectToolEntries(response);

    /// <summary>
    /// True only for a throwaway per-run conversation the trigger created this run: a transient
    /// <c>conv:&lt;guid&gt;</c> that is not pinned, not the default, not a human-pairing thread, and
    /// whose ActiveSessionId is either unset or our own cron session. Guarantees a pinned/human
    /// conversation is never archived by a no-op cron wake.
    /// </summary>
    private static bool IsDisposableTransientConversation(Conversation conversation, SessionId cronSessionId)
    {
        if (conversation.IsPinned || conversation.IsDefault)
            return false;

        if (conversation.Kind != ConversationKind.HumanAgent)
            return false;

        if (!conversation.ConversationId.Value.StartsWith("conv:", StringComparison.Ordinal))
            return false;

        return conversation.ActiveSessionId is null || conversation.ActiveSessionId == cronSessionId;
    }

    private async Task<(Conversation Conversation, bool CreatedFresh)> ResolveOrCreateConversationAsync(
        AgentId agentId,
        InternalTriggerRequest? request,
        CancellationToken ct)
    {
        // Fast path: scheduler passed in the conversation already pinned on the job.
        if (request?.ConversationId is { } pinnedId)
        {
            var pinned = await conversations.GetAsync(pinnedId, ct).ConfigureAwait(false);
            if (pinned is not null)
            {
                if (pinned.Status == ConversationStatus.Archived)
                {
                    pinned.Status = ConversationStatus.Active;
                    pinned.UpdatedAt = DateTimeOffset.UtcNow;
                    await conversations.SaveAsync(pinned, ct).ConfigureAwait(false);

                    // Notify the portal so the conversation reappears in the sidebar without
                    // requiring a page reload. Best-effort: failure here must not block the
                    // cron turn. (#864)
                    if (changeNotifier is not null)
                    {
                        try
                        {
                            await changeNotifier.NotifyConversationChangedAsync(
                                "updated", agentId.Value, pinned.ConversationId.Value, ct)
                                .ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex,
                                "CronTrigger: non-fatal failure sending reactivation notification for conversation {ConversationId}",
                                pinned.ConversationId);
                        }
                    }
                }
                return (pinned, false);
            }

            logger.LogWarning(
                "CronTrigger: pinned conversation '{ConversationId}' for job '{JobId}' was missing; creating a fresh one. The scheduler's CAS will reconcile.",
                pinnedId,
                request.CronJobId);

            // #3161: this is the silent-loss path the issue names. The job's destination - the
            // conversation the operator is actually reading - is gone, so this run's output lands
            // in a brand-new throwaway conversation nobody has open. That is a DELIVERY FAILURE:
            // pre-#3161 it produced one warning log line and a run recorded 'ok', so a job whose
            // destination had been deleted looked healthy forever. Reporting it back lets the
            // scheduler record a non-success status and alert on it. The run still proceeds - the
            // work is worth doing and the CAS reconciliation is still correct - it just no longer
            // claims to have succeeded.
            if (request is not null)
            {
                request.DeliveryError =
                    $"The job's destination conversation '{pinnedId.Value}' no longer exists; "
                    + "this run's output was written to a newly created conversation instead.";
            }
        }

        // No pin (first run) or pinned conversation was hard-deleted out from under us.
        // Create a fresh per-run conversation; the scheduler's CAS decides whether ours wins.
        var initiator = ResolveInitiator(request?.CreatedBy, agentId);
        // #2553: the job name is operator/agent-supplied. Normalise it at the render seam too,
        // not just at the four producers, so any future caller of InternalTriggerRequest cannot
        // put a multi-line or control-character-bearing title on a conversation.
        var sanitizedJobName = ExternalText.Sanitize(request?.JobName, ExternalText.DefaultDisplayLength);
        var title = !string.IsNullOrWhiteSpace(sanitizedJobName)
            ? sanitizedJobName
            : "Cron";

        // Schedule-driven origination (#2302). Minted through the single creation seam (#2310)
        // so clients never need to sniff a `cron:` prefix out of a session id to know this run
        // was unattended, and provenance cannot be silently omitted.
        var conversation = ConversationFactory.CreateForCron(
            ConversationId.From($"conv:{Guid.NewGuid():N}"),
            agentId,
            title: title,
            initiator: initiator,
            // #2121: the cron job that owns this run. Null only when the trigger request carried no
            // job id (an internal trigger that is not a scheduled job), which is an honest gap
            // rather than a fabricated identifier.
            sourceId: request?.CronJobId?.Value);

        await conversations.CreateAsync(conversation, ct).ConfigureAwait(false);
        logger.LogInformation(
            "CronTrigger: created conversation '{ConversationId}' titled '{Title}' for job '{JobId}' (initiator={Initiator}).",
            conversation.ConversationId,
            title,
            request?.CronJobId,
            initiator);

        return (conversation, true);
    }

    /// <summary>
    /// Resolves the citizen who scheduled the cron job. Per directive G-5, a cron run is "a
    /// proxy message for the citizen who scheduled it so they did not have to do it manually".
    /// Falls back to the target agent itself for system-provisioned jobs (heartbeat, soul, etc.)
    /// where <c>CreatedBy</c> is null or holds a legacy free-form string.
    /// </summary>
    private static CitizenId ResolveInitiator(string? createdBy, AgentId agentId)
    {
        if (!string.IsNullOrWhiteSpace(createdBy) && CitizenId.TryParse(createdBy, out var parsed))
            return parsed;

        return CitizenId.Of(agentId);
    }

    private static SessionId BuildCronSessionId(JobId? jobId)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        var suffix = Guid.NewGuid().ToString("N");
        var safeJobId = SanitizeSessionIdPart(jobId?.Value);
        return string.IsNullOrWhiteSpace(safeJobId)
            ? SessionId.From($"cron:{timestamp}:{suffix}")
            : SessionId.From($"cron:{safeJobId}:{timestamp}:{suffix}");
    }

    private static string? SanitizeSessionIdPart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        Span<char> buffer = stackalloc char[Math.Min(40, value.Length)];
        var length = 0;
        foreach (var ch in value)
        {
            if (length >= buffer.Length)
                break;

            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
            {
                buffer[length++] = ch;
                continue;
            }

            buffer[length++] = '-';
        }

        return new string(buffer[..length]).Trim('-');
    }
}
