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
/// Internal trigger used for daily soul-session heartbeat execution.
/// </summary>
public sealed class SoulTrigger(
    IAgentSupervisor supervisor,
    IAgentRegistry registry,
    ISessionStore sessions,
    ILogger<SoulTrigger> logger,
    TimeProvider? timeProvider = null,
    IConversationStore? conversationStore = null) : IInternalTrigger
{
    private static readonly TimeSpan DefaultDayBoundary = TimeSpan.Zero;
    private static readonly TimeZoneInfo DefaultTimeZone = TimeZoneInfo.Utc;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IConversationStore? _conversationStore = conversationStore;

    /// <summary>
    /// Gets the trigger type identifier.
    /// </summary>
    public TriggerType Type => TriggerType.Soul;

    /// <summary>
    /// Gets the display name for the trigger.
    /// </summary>
    public string DisplayName => "Soul Session";

    /// <summary>
    /// Executes create session async.
    /// </summary>
    /// <param name="agentId">The agent id.</param>
    /// <param name="prompt">The prompt.</param>
    /// <param name="ct">The ct.</param>
    /// <param name="request">Optional trigger metadata such as cron job and model override.</param>
    /// <returns>The create session async result.</returns>
    public async Task<SessionId> CreateSessionAsync(
        AgentId agentId,
        string prompt,
        CancellationToken ct = default,
        InternalTriggerRequest? request = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var soulConfig = registry.Get(agentId)?.Soul;
        var (timeZone, dayBoundary) = ResolveCalendarSettings(soulConfig);
        var nowUtc = _timeProvider.GetUtcNow();
        var soulDate = ResolveSoulDate(nowUtc, timeZone, dayBoundary);
        var sessionId = SessionId.ForSoul(agentId, soulDate);

        await SealOlderSoulSessionsAsync(agentId, soulDate, soulConfig, ct).ConfigureAwait(false);

        var session = await sessions.GetOrCreateAsync(sessionId, agentId, ct).ConfigureAwait(false);
        InitializeSoulSession(session, agentId, soulDate);

        // P9-F: Participants live on the Conversation, not the Session. Register the agent
        // citizen against the conversation pinned to this soul session. Skipped when no
        // conversation store is wired (legacy unit-test compositions) — the participant
        // would have nowhere to live and the fence pins direct Session.Participants
        // mutations as removed.
        if (_conversationStore is not null && session.ConversationId.IsInitialized())
        {
            await _conversationStore.AddParticipantsAsync(
                session.ConversationId,
                [new SessionParticipant { CitizenId = CitizenId.Of(agentId) }],
                ct).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(request?.ModelOverride))
            session.Metadata.Remove("modelOverride");
        else
            session.Metadata["modelOverride"] = request!.ModelOverride;

        if (request?.CronJobId is null)
            session.Metadata.Remove("cronJobId");
        else
            session.Metadata["cronJobId"] = request.CronJobId.Value.Value;

        // P9-E (#645): stamp the proxy-trigger origin on the user entry instead of
        // discriminating via SessionType — the session itself is AgentSelf because
        // soul is an agent-talking-to-itself flow; the Soul trigger kind is per-turn.
        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.User,
            Content = prompt,
            Trigger = TriggerType.Soul
        });
        await sessions.SaveAsync(session, ct).ConfigureAwait(false);
        var handle = await supervisor.GetOrCreateAsync(agentId, sessionId, ct).ConfigureAwait(false);
        var response = await handle.PromptAsync(prompt, ct).ConfigureAwait(false);
        // #2127: persist the tool timeline this blocking run executed before the assistant text so
        // the soul session records a durable, auditable tool trail rather than final text only.
        foreach (var toolEntry in TriggerToolAuditProjector.ProjectToolEntries(response))
            session.AddEntry(toolEntry);
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
        // P9-E (#645): soul sessions no longer carry SessionType.Soul. Discovery is now
        // driven by the canonical Metadata["soulDate"] tag that InitializeSoulSession
        // stamps on every soul session; status must still be Active.
        var agentSessions = await sessions.ListAsync(agentId, ct).ConfigureAwait(false);
        var oldActiveSoulSessions = agentSessions
            .Where(session => session.Status == GatewaySessionStatus.Active && session.Metadata.ContainsKey("soulDate"))
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
                // #2127: reflection-on-seal is a blocking run too - record its tool timeline durably.
                foreach (var toolEntry in TriggerToolAuditProjector.ProjectToolEntries(reflectionResponse))
                    previousSession.AddEntry(toolEntry);
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
        // P9-E (#645): soul is an agent-self conversation; the "Soul" proxy-trigger
        // kind lives on SessionEntry.Trigger, not on the SessionType.
        session.SessionType = SessionType.AgentSelf;
        session.ChannelType = null;
        session.CallerId ??= $"soul:{agentId.Value}";
        session.Status = GatewaySessionStatus.Active;
        session.Metadata["soulDate"] = soulDate.ToString("yyyy-MM-dd");
        // P9-F: participant registration moved to CreateSessionAsync (after this method) so it
        // can be routed through IConversationStore.AddParticipantsAsync — Participants no
        // longer live on Session.
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
