using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Api.Hubs;

/// <summary>
/// Internal trigger used for cron-triggered sessions.
/// </summary>
public sealed class CronTrigger(
    IAgentSupervisor supervisor,
    ISessionStore sessions,
    ILogger<CronTrigger> logger) : IInternalTrigger
{
    public TriggerType Type => TriggerType.Cron;
    public string DisplayName => "Cron Scheduler";

    public async Task<SessionId> CreateSessionAsync(AgentId agentId, string prompt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var sessionId = SessionId.From($"cron:{DateTimeOffset.UtcNow:yyyyMMddHHmmss}:{Guid.NewGuid():N}");
        var session = await sessions.GetOrCreateAsync(sessionId, agentId, ct).ConfigureAwait(false);
        session.ChannelType ??= ChannelKey.From(Type.Value);
        session.CallerId ??= $"{Type.Value}:{agentId.Value}";
        session.SessionType = SessionType.Cron;
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = prompt });

        var handle = await supervisor.GetOrCreateAsync(agentId, sessionId, ct).ConfigureAwait(false);
        var response = await handle.PromptAsync(prompt, ct).ConfigureAwait(false);

        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = response.Content });
        await sessions.SaveAsync(session, ct).ConfigureAwait(false);

        logger.LogInformation(
            "Cron trigger created session '{SessionId}' for agent '{AgentId}'.",
            sessionId,
            agentId);

        return sessionId;
    }
}
