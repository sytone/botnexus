using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Logging;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

namespace BotNexus.Gateway.Api.Hubs;

/// <summary>
/// Internal trigger used for daily soul-session heartbeat execution.
/// </summary>
public sealed class SoulTrigger(
    IAgentSupervisor supervisor,
    IAgentRegistry registry,
    ISessionStore sessions,
    ILogger<SoulTrigger> logger,
    TimeProvider? timeProvider = null) : IInternalTrigger
{
    private static readonly TimeSpan DefaultDayBoundary = TimeSpan.Zero;
    private static readonly TimeZoneInfo DefaultTimeZone = TimeZoneInfo.Utc;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public TriggerType Type => TriggerType.Soul;
    public string DisplayName => "Soul Session";

    public async Task<SessionId> CreateSessionAsync(AgentId agentId, string prompt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var soulConfig = registry.Get(agentId)?.Soul;
        var (timeZone, dayBoundary) = ResolveCalendarSettings(soulConfig);
        var nowUtc = _timeProvider.GetUtcNow();
        var soulDate = ResolveSoulDate(nowUtc, timeZone, dayBoundary);
        var sessionId = SessionId.ForSoul(agentId, soulDate);

        await SealOlderSoulSessionsAsync(agentId, soulDate, soulConfig, ct).ConfigureAwait(false);

        var session = await sessions.GetOrCreateAsync(sessionId, agentId, ct).ConfigureAwait(false);
        InitializeSoulSession(session, agentId, soulDate);

        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = prompt });
        var handle = await supervisor.GetOrCreateAsync(agentId, sessionId, ct).ConfigureAwait(false);
        var response = await handle.PromptAsync(prompt, ct).ConfigureAwait(false);
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = response.Content });

        await sessions.SaveAsync(session, ct).ConfigureAwait(false);

        logger.LogInformation(
            "Soul trigger used session '{SessionId}' for agent '{AgentId}' on date '{SoulDate}'.",
            sessionId,
            agentId,
            soulDate);

        return sessionId;
    }

    private async Task SealOlderSoulSessionsAsync(
        AgentId agentId,
        DateOnly todaySoulDate,
        SoulAgentConfig? soulConfig,
        CancellationToken ct)
    {
        var agentSessions = await sessions.ListAsync(agentId, ct).ConfigureAwait(false);
        var oldActiveSoulSessions = agentSessions
            .Where(session => session.SessionType == SessionType.Soul && session.Status == GatewaySessionStatus.Active)
            .Where(session => TryGetSoulDate(session, out var soulDate) && soulDate < todaySoulDate)
            .ToArray();

        foreach (var previousSession in oldActiveSoulSessions)
        {
            if (soulConfig?.ReflectionOnSeal == true && !string.IsNullOrWhiteSpace(soulConfig.ReflectionPrompt))
            {
                var reflectionPrompt = soulConfig.ReflectionPrompt!;
                previousSession.AddEntry(new SessionEntry { Role = MessageRole.User, Content = reflectionPrompt });
                var reflectionHandle = await supervisor.GetOrCreateAsync(agentId, previousSession.SessionId, ct).ConfigureAwait(false);
                var reflectionResponse = await reflectionHandle.PromptAsync(reflectionPrompt, ct).ConfigureAwait(false);
                previousSession.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = reflectionResponse.Content });
            }

            previousSession.Status = GatewaySessionStatus.Sealed;
            previousSession.UpdatedAt = _timeProvider.GetUtcNow();
            await sessions.SaveAsync(previousSession, ct).ConfigureAwait(false);

            logger.LogInformation(
                "Sealed previous soul session '{SessionId}' for agent '{AgentId}'.",
                previousSession.SessionId,
                agentId);
        }
    }

    private static void InitializeSoulSession(GatewaySession session, AgentId agentId, DateOnly soulDate)
    {
        session.SessionType = SessionType.Soul;
        session.ChannelType = null;
        session.CallerId ??= $"soul:{agentId.Value}";
        session.Status = GatewaySessionStatus.Active;
        session.Metadata["soulDate"] = soulDate.ToString("yyyy-MM-dd");

        if (!session.Participants.Any(participant =>
                participant.Type == ParticipantType.Agent &&
                string.Equals(participant.Id, agentId.Value, StringComparison.OrdinalIgnoreCase)))
        {
            session.Participants.Add(new SessionParticipant
            {
                Type = ParticipantType.Agent,
                Id = agentId.Value
            });
        }
    }

    private static bool TryGetSoulDate(GatewaySession session, out DateOnly soulDate)
    {
        soulDate = default;
        if (!session.Metadata.TryGetValue("soulDate", out var raw) || raw is null)
            return false;

        if (raw is DateOnly direct)
        {
            soulDate = direct;
            return true;
        }

        if (DateOnly.TryParse(raw.ToString(), out var parsed))
        {
            soulDate = parsed;
            return true;
        }

        return false;
    }

    private static DateOnly ResolveSoulDate(DateTimeOffset nowUtc, TimeZoneInfo timeZone, TimeSpan dayBoundary)
    {
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        var localDate = DateOnly.FromDateTime(localNow.Date);
        return localNow.TimeOfDay >= dayBoundary
            ? localDate
            : localDate.AddDays(-1);
    }

    private static (TimeZoneInfo TimeZone, TimeSpan DayBoundary) ResolveCalendarSettings(SoulAgentConfig? soulConfig)
    {
        var timeZone = ResolveTimeZone(soulConfig?.Timezone);
        var dayBoundary = ResolveDayBoundary(soulConfig?.DayBoundary);
        return (timeZone, dayBoundary);
    }

    private static TimeZoneInfo ResolveTimeZone(string? timezone)
    {
        if (string.IsNullOrWhiteSpace(timezone))
            return DefaultTimeZone;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (TimeZoneNotFoundException)
        {
        }
        catch (InvalidTimeZoneException)
        {
        }

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(timezone, out var windowsTimeZone))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(windowsTimeZone);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return DefaultTimeZone;
    }

    private static TimeSpan ResolveDayBoundary(string? dayBoundary)
    {
        if (string.IsNullOrWhiteSpace(dayBoundary))
            return DefaultDayBoundary;

        return TimeSpan.TryParse(dayBoundary, out var parsed)
            ? parsed
            : DefaultDayBoundary;
    }
}
