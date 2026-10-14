using System.Globalization;
using System.Text.Json;
using BotNexus.Cron;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Commands;

internal sealed partial class BuiltInCommandContributor
{
    // Persisted metadata distinguishes this surface from arbitrary conversation-bound cron jobs.
    private const string LoopMarkerKey = "chatCommand";
    private const string LoopMarkerValue = "/loop";
    private const string LoopIntervalForms = "Accepted intervals: 1m, 2m, 3m, 4m, 5m, 6m, 10m, 12m, 15m, 20m, 30m, 60m; 1h, 2h, 3h, 4h, 6h, 8h, 12h, 24h.";

    private async Task<CommandResult> ExecuteLoopAsync(CommandExecutionContext context, CancellationToken ct)
    {
        var (conversation, error) = await ResolveLoopConversationAsync(context, ct).ConfigureAwait(false);
        if (conversation is null)
            return LoopResult(error ?? "No durable current conversation resolves.", true);

        var store = serviceProvider.GetService<ICronStore>();
        if (store is null)
            return LoopResult("Cron storage is not available in this gateway.", true);
        await store.InitializeAsync(ct).ConfigureAwait(false);

        // Raw input preserves the prompt's whitespace instead of rebuilding it from registry tokens.
        var input = LoopCommandRemainder(context.RawInput);
        var tokenEnd = 0;
        while (tokenEnd < input.Length && !char.IsWhiteSpace(input[tokenEnd]))
            tokenEnd++;
        if (tokenEnd == input.Length)
            tokenEnd = -1;
        var token = tokenEnd < 0 ? input : input[..tokenEnd];
        var remainder = tokenEnd < 0 ? string.Empty : input[(tokenEnd + 1)..].Trim();

        if (token.Equals("status", StringComparison.OrdinalIgnoreCase) || token.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            var candidates = (await store.ListAsync(conversation.AgentId, ct).ConfigureAwait(false))
                .Where(job => IsCurrentLoop(job, conversation)).OrderBy(job => job.Name, StringComparer.Ordinal).ToList();
            if (token.Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                if (remainder.Length > 0)
                    return LoopResult("Usage: /loop status", true);
                return LoopResult(candidates.Count == 0 ? "No loops in this conversation." : string.Join("\n", candidates.Select(job =>
                    $"- {job.Name}: {job.Schedule} (UTC), {(job.Enabled ? "enabled" : "disabled")}; {job.Message}")));
            }

            if (remainder.Any(char.IsWhiteSpace))
                return LoopResult("Usage: /loop stop [name]", true);
            var selected = remainder.Length == 0 ? candidates : candidates.Where(job => job.Name.Equals(remainder, StringComparison.Ordinal)).ToList();
            if (selected.Count == 0)
                return LoopResult("No matching loop in this conversation owned by this agent. Use /loop status.", true);
            if (selected.Count != 1)
                return LoopResult("More than one loop matches. Use /loop status, then /loop stop <name>.", true);
            var scheduler = serviceProvider.GetService<CronScheduler>();
            if (scheduler is null)
                return LoopResult("Cron scheduler is not available; the loop was not deleted.", true);
            // The scheduler owns cancellation, archive provenance and run-session reclamation.
            await scheduler.DeleteJobAsync(selected[0].Id, ct).ConfigureAwait(false);
            return LoopResult($"Stopped loop {selected[0].Name}.");
        }

        if (input.Length == 0)
            return LoopResult($"Usage: /loop [interval] <prompt> | /loop status | /loop stop [name]\n{LoopIntervalForms}", true);

        TimeSpan interval;
        string prompt;
        CronSelfPacingDecision? pacing = null;
        if (TryParseLoopInterval(token, out interval))
        {
            prompt = remainder;
        }
        else if (LooksLikeLoopInterval(token))
        {
            return LoopResult($"Invalid interval '{token}'. {LoopIntervalForms}", true);
        }
        else
        {
            var options = serviceProvider.GetService<IOptionsMonitor<CronOptions>>()?.CurrentValue;
            // A five-minute proposal is clamped by the existing decision, never a second bound.
            pacing = CronSelfPacingBound.Clamp(TimeSpan.FromMinutes(5),
                options is null ? null : TimeSpan.FromSeconds(options.SelfPacingFloorSeconds),
                options is null ? null : TimeSpan.FromSeconds(options.SelfPacingCeilingSeconds));
            interval = pacing.Effective;
            prompt = input;
        }

        if (prompt.Length == 0)
            return LoopResult($"A loop needs a prompt. Usage: /loop [interval] <prompt>\n{LoopIntervalForms}", true);
        if (!TryLoopSchedule(interval, out var schedule))
            return LoopResult($"The configured default interval ({interval.TotalSeconds.ToString(CultureInfo.InvariantCulture)}s) cannot be represented exactly by a five-field cron schedule. Supply an explicit interval. {LoopIntervalForms}", true);

        var id = JobId.From($"loop-{Guid.NewGuid():N}");
        var binding = CronConversationDefault.Resolve("agent-prompt", false,
            explicitConversationId: conversation.ConversationId,
            creatingConversationId: conversation.ConversationId,
            creatingConversationKind: conversation.Kind,
            creatingConversationIsDefault: conversation.IsDefault);
        var job = await store.CreateAsync(new CronJob
        {
            Id = id, Name = id.Value, Schedule = schedule, TimeZone = "UTC", ActionType = "agent-prompt",
            AgentId = conversation.AgentId, CreatedBy = conversation.AgentId.Value, Message = prompt,
            Enabled = true, ConversationId = binding, CreatedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object?> { [LoopMarkerKey] = LoopMarkerValue }
        }, ct).ConfigureAwait(false);
        var pacingSummary = pacing is null ? string.Empty :
            $" Default pacing: requested {pacing.Requested.TotalSeconds.ToString(CultureInfo.InvariantCulture)}s, effective {pacing.Effective.TotalSeconds.ToString(CultureInfo.InvariantCulture)}s, bounds {pacing.Floor.TotalSeconds.ToString(CultureInfo.InvariantCulture)}–{pacing.Ceiling.TotalSeconds.ToString(CultureInfo.InvariantCulture)}s, clamp {pacing.Reason}.";
        return LoopResult($"Created loop {job.Name} in this conversation: {schedule} (UTC), enabled.{pacingSummary} Use /loop stop {job.Name} to stop it.");
    }

    private async Task<(Conversation? Conversation, string? Error)> ResolveLoopConversationAsync(CommandExecutionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.AgentId) || string.IsNullOrWhiteSpace(context.SessionId))
            return (null, "A loop requires a durable current conversation. Start a conversation first; headless commands cannot create loops.");
        var conversations = serviceProvider.GetService<IConversationStore>();
        if (conversations is null)
            return (null, "The conversation store is not available.");
        var agent = AgentId.From(context.AgentId);
        var sessionId = SessionId.From(context.SessionId);
        var session = await sessionStore.GetAsync(sessionId, ct).ConfigureAwait(false);
        // Ownership applies to an existing session even when its durable parent is unset.
        if (session is not null && (session.SessionId != sessionId || (session.AgentId.IsInitialized() && session.AgentId != agent)))
            return (null, "The session does not belong to the current agent/conversation.");
        Conversation? conversation;
        if (session is not null && session.ConversationId.IsInitialized())
        {
            // An existing durable parent that is missing must not fall through to a different row.
            conversation = await conversations.GetAsync(session.ConversationId, ct).ConfigureAwait(false);
            if (conversation is not null && conversation.ConversationId != session.ConversationId)
                return (null, "The durable conversation does not match this session.");
        }
        else
        {
            var matches = (await conversations.ListAsync(agent, ct).ConfigureAwait(false))
                .Where(value => value.ActiveSessionId == sessionId).ToList();
            if (matches.Count != 1)
                return (null, "No unique durable current conversation resolves for this session.");
            conversation = matches[0];
        }
        if (conversation is null || !conversation.ConversationId.IsInitialized())
            return (null, "No durable current conversation resolves for this session.");
        if (conversation.AgentId != agent || conversation.ActiveSessionId != sessionId)
            return (null, "The conversation does not match the current agent and active session.");
        if (conversation.Status == ConversationStatus.Archived)
            return (null, "This conversation is archived; start an active conversation first.");
        return (conversation, null);
    }

    private static string LoopCommandRemainder(string raw)
    {
        // Match the registry's first slash-prefixed whitespace token, preserving prompt bytes.
        var index = 0;
        while (index < raw.Length)
        {
            while (index < raw.Length && char.IsWhiteSpace(raw[index]))
                index++;
            var start = index;
            while (index < raw.Length && !char.IsWhiteSpace(raw[index]))
                index++;
            if (start < raw.Length && raw[start] == '/')
                return raw[index..].Trim();
        }
        return string.Empty;
    }

    private static bool IsCurrentLoop(CronJob job, Conversation conversation)
    {
        if (job.System || job.ActionType != "agent-prompt" || job.AgentId != conversation.AgentId
            || !string.Equals(job.CreatedBy, conversation.AgentId.Value, StringComparison.Ordinal)
            || job.ConversationId != conversation.ConversationId
            || job.Metadata is null || !job.Metadata.TryGetValue(LoopMarkerKey, out var marker))
            return false;
        return marker is string text ? text == LoopMarkerValue
            : marker is JsonElement { ValueKind: JsonValueKind.String } element && element.GetString() == LoopMarkerValue;
    }

    private static bool LooksLikeLoopInterval(string token) => token.Length > 0 &&
        (char.IsDigit(token[0]) || token[0] is '+' or '-' or '.' || token.Equals("every", StringComparison.OrdinalIgnoreCase)
         || token.StartsWith("PT", StringComparison.OrdinalIgnoreCase));

    private static bool TryParseLoopInterval(string token, out TimeSpan interval)
    {
        interval = default;
        if (token.Length < 2 || !int.TryParse(token.AsSpan(0, token.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            return false;
        var unit = char.ToLowerInvariant(token[^1]);
        if (unit is not ('m' or 'h') || number < 1 || (unit == 'm' ? number > 60 : number > 24)) return false;
        interval = unit == 'm' ? TimeSpan.FromMinutes(number) : TimeSpan.FromHours(number);
        return TryLoopSchedule(interval, out _);
    }

    private static bool TryLoopSchedule(TimeSpan interval, out string schedule)
    {
        schedule = string.Empty;
        var minutes = interval.TotalMinutes;
        // Divisors avoid */7's short boundary gap and */5 hours' short midnight gap.
        if (minutes >= 1 && minutes < 60 && minutes == Math.Truncate(minutes) && 60 % minutes == 0)
        {
            schedule = $"*/{minutes.ToString(CultureInfo.InvariantCulture)} * * * *";
            return true;
        }
        var hours = interval.TotalHours;
        if (hours >= 1 && hours <= 24 && hours == Math.Truncate(hours) && 24 % hours == 0)
        {
            schedule = hours == 24 ? "0 0 * * *" : $"0 */{hours.ToString(CultureInfo.InvariantCulture)} * * *";
            return true;
        }
        return false;
    }

    private static CommandResult LoopResult(string body, bool isError = false) => new() { Title = "Conversation Loops", Body = body, IsError = isError };
}
