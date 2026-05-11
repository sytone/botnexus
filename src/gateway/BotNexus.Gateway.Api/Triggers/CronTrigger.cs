using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Domain.Primitives;
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
                ?? await GetOrCreateCronConversationAsync(agentId, request.CronJobId, ct).ConfigureAwait(false);
        }
        else
        {
            conversation = await GetOrCreateCronConversationAsync(agentId, request?.CronJobId, ct).ConfigureAwait(false);
        }

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
    private async Task<Conversation> GetOrCreateCronConversationAsync(AgentId agentId, string? jobId, CancellationToken ct)
    {
        var title = string.IsNullOrWhiteSpace(jobId)
            ? $"cron:{agentId.Value}"
            : $"cron:{SanitizeSessionIdPart(jobId) ?? agentId.Value}";

        // Try to find an existing conversation with this title.
        var existing = await conversations.ListAsync(agentId, ct).ConfigureAwait(false);
        var match = existing?.FirstOrDefault(c =>
            string.Equals(c.Title, title, StringComparison.Ordinal) &&
            c.Status == BotNexus.Gateway.Abstractions.Models.ConversationStatus.Active);

        if (match is not null)
            return match;

        // Create a new stable conversation for this job.
        var conversation = new Conversation
        {
            ConversationId = ConversationId.Create(),
            AgentId = agentId,
            Title = title,
            IsDefault = false
        };
        await conversations.CreateAsync(conversation, ct).ConfigureAwait(false);
        logger.LogInformation("CronTrigger: created conversation '{Title}' ({ConversationId}) for job '{JobId}'.",
            title, conversation.ConversationId, jobId);
        return conversation;
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
}
