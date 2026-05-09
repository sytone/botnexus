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

        var conversation = await conversations.GetOrCreateDefaultAsync(agentId, ct).ConfigureAwait(false);
        if (session.Session.ConversationId is null || session.Session.ConversationId != conversation.ConversationId)
            session.Session.ConversationId = conversation.ConversationId;

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
