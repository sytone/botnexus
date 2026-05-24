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
/// </summary>
public sealed class CronTrigger(
    IAgentSupervisor supervisor,
    IConversationStore conversations,
    ISessionStore sessions,
    ILogger<CronTrigger> logger) : IInternalTrigger
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
    /// Executes create session async.
    /// </summary>
    /// <param name="agentId">The agent id.</param>
    /// <param name="prompt">The prompt.</param>
    /// <param name="ct">The ct.</param>
    /// <param name="request">Optional trigger metadata such as cron job, conversation, and model override.</param>
    /// <returns>The create session async result.</returns>
    public async Task<SessionId> CreateSessionAsync(
        AgentId agentId,
        string prompt,
        CancellationToken ct = default,
        InternalTriggerRequest? request = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        // Each cron run gets a fresh session ID so history entries are cleanly separated by run.
        var sessionId = BuildCronSessionId(request?.CronJobId);
        var session = await sessions.GetOrCreateAsync(sessionId, agentId, ct).ConfigureAwait(false);
        session.ChannelType ??= ChannelKey.From(Type.Value);
        session.CallerId ??= $"{Type.Value}:{agentId.Value}";
        session.SessionType = SessionType.Cron;
        session.Metadata["triggerType"] = Type.Value;

        if (string.IsNullOrWhiteSpace(request?.ModelOverride))
            session.Metadata.Remove("modelOverride");
        else
            session.Metadata["modelOverride"] = request!.ModelOverride;

        if (string.IsNullOrWhiteSpace(request?.CronJobId))
            session.Metadata.Remove("cronJobId");
        else
            session.Metadata["cronJobId"] = request!.CronJobId;

        // Resolve the conversation for this run:
        // 1. Explicit ConversationId on the job — always use that conversation
        // 2. Otherwise find/create a stable per-job conversation keyed by job ID
        //    so every run of the same job lands in the same conversation.
        Conversation conversation;
        if (!string.IsNullOrWhiteSpace(request?.ConversationId))
        {
            conversation = await conversations.GetAsync(ConversationId.From(request.ConversationId), ct).ConfigureAwait(false)
                ?? await GetOrCreateCronConversationAsync(agentId, request.CronJobId, request.JobName, ct).ConfigureAwait(false);
        }
        else
        {
            conversation = await GetOrCreateCronConversationAsync(agentId, request?.CronJobId, request?.JobName, ct).ConfigureAwait(false);
        }

        // Write back the resolved conversation ID so the caller (e.g. scheduler) can persist it to the job
        // record, enabling subsequent runs to skip the lookup entirely.
        if (request is not null && string.IsNullOrWhiteSpace(request.ResolvedConversationId))
            request.ResolvedConversationId = conversation.ConversationId.Value;

        if (session.Session.ConversationId is null || session.Session.ConversationId != conversation.ConversationId)
            session.Session.ConversationId = conversation.ConversationId;

        // Update the conversation's active session to this run so history loads the latest.
        if (conversation.ActiveSessionId is null || conversation.ActiveSessionId != sessionId)
        {
            conversation.ActiveSessionId = sessionId;
            conversation.UpdatedAt = DateTimeOffset.UtcNow;
            await conversations.SaveAsync(conversation, ct).ConfigureAwait(false);
        }

        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = prompt });
        await sessions.SaveAsync(session, ct).ConfigureAwait(false);

        var handle = await supervisor.GetOrCreateAsync(agentId, sessionId, ct).ConfigureAwait(false);
        var response = await handle.PromptAsync(prompt, ct).ConfigureAwait(false);

        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = response.Content });
        await sessions.SaveAsync(session, ct).ConfigureAwait(false);

        logger.LogInformation(
            "Cron trigger created session '{SessionId}' for agent '{AgentId}' (jobId: {JobId}, model: {ModelOverride}).",
            sessionId,
            agentId,
            request?.CronJobId,
            request?.ModelOverride);

        return sessionId;
    }

    /// <summary>
    /// Finds or creates a stable conversation for a cron job.
    /// All runs of the same job land in the same conversation.
    /// The conversation is identified by its title "cron:{jobId}" or "cron:unnamed" for jobs without an ID.
    /// </summary>
    private async Task<Conversation> GetOrCreateCronConversationAsync(AgentId agentId, string? jobId, string? jobName, CancellationToken ct)
    {
        // Use human-readable job name as title when available; fall back to job-id slug.
        var title = !string.IsNullOrWhiteSpace(jobName)
            ? jobName
            : string.IsNullOrWhiteSpace(jobId)
                ? $"cron:{agentId.Value}"
                : $"cron:{SanitizeSessionIdPart(jobId) ?? agentId.Value}";
        var stableConversationId = BuildCronConversationId(agentId, jobId);

        // Prefer deterministic lookup by stable conversation id.
        var byStableId = await conversations.GetAsync(stableConversationId, ct).ConfigureAwait(false);
        if (byStableId is not null)
        {
            if (byStableId.Status == BotNexus.Gateway.Abstractions.Models.ConversationStatus.Archived)
            {
                byStableId.Status = BotNexus.Gateway.Abstractions.Models.ConversationStatus.Active;
                byStableId.UpdatedAt = DateTimeOffset.UtcNow;
                await conversations.SaveAsync(byStableId, ct).ConfigureAwait(false);
            }

            var conversationsForAgent = await conversations.ListAsync(agentId, ct).ConfigureAwait(false);
            await NormalizeDuplicateCronConversationsAsync(agentId, title, byStableId, conversationsForAgent, ct).ConfigureAwait(false);
            return byStableId;
        }

        // Backward-compatible lookup by title for legacy records created before stable IDs.
        var existing = await conversations.ListAsync(agentId, ct).ConfigureAwait(false);
        var titleMatches = existing
            .Where(c => string.Equals(c.Title, title, StringComparison.Ordinal))
            .ToList();

        var canonical = titleMatches
            .Where(c => c.Status == BotNexus.Gateway.Abstractions.Models.ConversationStatus.Active)
            .OrderByDescending(c => c.UpdatedAt)
            .FirstOrDefault()
            ?? titleMatches.OrderByDescending(c => c.UpdatedAt).FirstOrDefault();

        if (canonical is not null)
        {
            if (canonical.Status == BotNexus.Gateway.Abstractions.Models.ConversationStatus.Archived)
            {
                canonical.Status = BotNexus.Gateway.Abstractions.Models.ConversationStatus.Active;
                canonical.UpdatedAt = DateTimeOffset.UtcNow;
                await conversations.SaveAsync(canonical, ct).ConfigureAwait(false);
            }

            await NormalizeDuplicateCronConversationsAsync(agentId, title, canonical, existing, ct).ConfigureAwait(false);
            return canonical;
        }

        // Create a new stable conversation for this job.
        var conversation = new Conversation
        {
            ConversationId = stableConversationId,
            AgentId = agentId,
            Title = title,
            IsDefault = false,
            // Cron triggers schedule work for the target agent on its own behalf, so the
            // initiating citizen is that agent.
            Initiator = CitizenId.Of(agentId)
        };
        try
        {
            await conversations.CreateAsync(conversation, ct).ConfigureAwait(false);
            logger.LogInformation("CronTrigger: created conversation '{Title}' ({ConversationId}) for job '{JobId}'.",
                title, conversation.ConversationId, jobId);
            return conversation;
        }
        catch (Exception ex)
        {
            // If another concurrent run created it first, re-resolve and continue.
            logger.LogDebug(ex, "CronTrigger: create race for conversation '{ConversationId}', retrying lookup.", stableConversationId);
            var resolved = await conversations.GetAsync(stableConversationId, ct).ConfigureAwait(false);
            if (resolved is not null)
                return resolved;

            var fallback = (await conversations.ListAsync(agentId, ct).ConfigureAwait(false))
                .FirstOrDefault(c =>
                    string.Equals(c.Title, title, StringComparison.Ordinal) &&
                    c.Status == BotNexus.Gateway.Abstractions.Models.ConversationStatus.Active);
            if (fallback is not null)
                return fallback;

            throw;
        }
    }

    private async Task NormalizeDuplicateCronConversationsAsync(
        AgentId agentId,
        string title,
        Conversation canonical,
        IReadOnlyList<Conversation> conversationsForAgent,
        CancellationToken ct)
    {
        var duplicateActive = conversationsForAgent
            .Where(c => c.Status == BotNexus.Gateway.Abstractions.Models.ConversationStatus.Active)
            .Where(c => c.ConversationId != canonical.ConversationId)
            .Where(c => string.Equals(c.Title, title, StringComparison.Ordinal))
            .ToList();

        if (duplicateActive.Count == 0)
            return;

        var agentSessions = await sessions.ListAsync(agentId, ct).ConfigureAwait(false);
        foreach (var duplicate in duplicateActive)
        {
            foreach (var session in agentSessions.Where(s => s.Session.ConversationId == duplicate.ConversationId))
            {
                session.Session.ConversationId = canonical.ConversationId;
                await sessions.SaveAsync(session, ct).ConfigureAwait(false);
            }

            await conversations.ArchiveAsync(duplicate.ConversationId, ct).ConfigureAwait(false);
        }

        var latestLinked = agentSessions
            .Where(s => s.Session.ConversationId == canonical.ConversationId)
            .OrderByDescending(s => s.UpdatedAt)
            .FirstOrDefault();

        if (latestLinked is not null && canonical.ActiveSessionId != latestLinked.SessionId)
        {
            canonical.ActiveSessionId = latestLinked.SessionId;
            canonical.UpdatedAt = DateTimeOffset.UtcNow;
            await conversations.SaveAsync(canonical, ct).ConfigureAwait(false);
        }

        logger.LogInformation(
            "CronTrigger: normalized {DuplicateCount} duplicate conversation(s) for '{Title}' under canonical {ConversationId}.",
            duplicateActive.Count,
            title,
            canonical.ConversationId);
    }

    private static SessionId BuildCronSessionId(string? jobId)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        var suffix = Guid.NewGuid().ToString("N");
        var safeJobId = SanitizeSessionIdPart(jobId);
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

    private static ConversationId BuildCronConversationId(AgentId agentId, string? jobId)
    {
        var safeAgentId = SanitizeSessionIdPart(agentId.Value) ?? "agent";
        var safeJobId = SanitizeSessionIdPart(jobId) ?? safeAgentId;
        return ConversationId.From($"cronconv:{safeAgentId}:{safeJobId}");
    }
}
