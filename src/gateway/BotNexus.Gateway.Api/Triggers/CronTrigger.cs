using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
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
///   <see cref="CitizenId.TryParse"/>) or falling back to the agent itself for system-provisioned jobs.
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

        var conversation = await ResolveOrCreateConversationAsync(agentId, request, ct).ConfigureAwait(false);

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

        if (conversation.ActiveSessionId is null || conversation.ActiveSessionId != sessionId)
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

        var handle = await supervisor.GetOrCreateAsync(agentId, sessionId, ct).ConfigureAwait(false);
        var response = await handle.PromptAsync(prompt, ct).ConfigureAwait(false);

        // P9-E (#645): stamp Cron on the user entry. Cron is a proxy for the citizen
        // who scheduled the job — the persisted trigger marks the originating proxy
        // so audit/UI can render the right badge without sniffing SessionType.
        // Persisted AFTER PromptAsync so session history never contains the user entry
        // as prior context when the agent handle is created for this run.
        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.User,
            Content = prompt,
            Trigger = TriggerType.Cron
        });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = response.Content });
        await sessions.SaveAsync(session, ct).ConfigureAwait(false);

        logger.LogInformation(
            "Cron trigger created session '{SessionId}' for agent '{AgentId}' in conversation '{ConversationId}' (jobId: {JobId}, model: {ModelOverride}).",
            sessionId,
            agentId,
            conversation.ConversationId,
            request?.CronJobId,
            request?.ModelOverride);

        return sessionId;
    }

    private async Task<Conversation> ResolveOrCreateConversationAsync(
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
                return pinned;
            }

            logger.LogWarning(
                "CronTrigger: pinned conversation '{ConversationId}' for job '{JobId}' was missing; creating a fresh one. The scheduler's CAS will reconcile.",
                pinnedId,
                request.CronJobId);
        }

        // No pin (first run) or pinned conversation was hard-deleted out from under us.
        // Create a fresh per-run conversation; the scheduler's CAS decides whether ours wins.
        var initiator = ResolveInitiator(request?.CreatedBy, agentId);
        var title = !string.IsNullOrWhiteSpace(request?.JobName)
            ? request!.JobName!
            : "Cron";

        var conversation = new Conversation
        {
            ConversationId = ConversationId.From($"conv:{Guid.NewGuid():N}"),
            AgentId = agentId,
            Title = title,
            IsDefault = false,
            Initiator = initiator
        };

        await conversations.CreateAsync(conversation, ct).ConfigureAwait(false);
        logger.LogInformation(
            "CronTrigger: created conversation '{ConversationId}' titled '{Title}' for job '{JobId}' (initiator={Initiator}).",
            conversation.ConversationId,
            title,
            request?.CronJobId,
            initiator);

        return conversation;
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
