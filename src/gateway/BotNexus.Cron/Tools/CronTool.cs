using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Domain.Primitives;

namespace BotNexus.Cron.Tools;

public sealed class CronTool(
    ICronStore cronStore,
    CronScheduler scheduler,
    AgentId agentId,
    bool allowCrossAgentCron = false) : IAgentTool
{
    private readonly AgentId _agentId = agentId;

    public string Name => "cron";
    public string Label => "Cron Job Manager";

    public Tool Definition => new(
        Name,
        "Manage scheduled cron jobs. Create, list, update, delete, and run cron jobs.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "action": {
                  "type": "string",
                  "enum": ["list", "create", "update", "delete", "run", "history"]
                },
                "jobId": { "type": "string", "description": "Optional - for update/delete/run." },
                "includeSystem": { "type": "boolean", "description": "When true, include system-provisioned jobs (e.g., heartbeat) in list output. Default: false." },
                "name": { "type": "string", "description": "Job name (for create)." },
                "schedule": { "type": "string", "description": "Standard 5-field cron expression (minute hour day month weekday). The expression is evaluated in the timezone specified by 'timeZone', or UTC if omitted. Example: '30 22 * * *' with timeZone 'America/Los_Angeles' fires at 10:30 PM Pacific daily." },
                "timeZone": { "type": "string", "description": "IANA timezone name for the schedule (e.g. 'America/Los_Angeles', 'Europe/London', 'Asia/Tokyo'). When set, the cron expression is interpreted in this timezone (including DST adjustments). Defaults to UTC if omitted." },
                "agentId": { "type": "string", "description": "Target agent (for create, defaults to calling agent)." },
                "message": { "type": "string", "description": "Prompt message (for create/update). Optional when templateName is provided." },
                "templateName": { "type": "string", "description": "Named prompt template reference (for create/update)." },
                "templateParameters": {
                  "type": "object",
                  "description": "Template parameter values for templateName (for create/update).",
                  "additionalProperties": { "type": "string" }
                },
                "model": { "type": "string", "description": "Optional model override for agent-prompt jobs. Supports model-id or provider/model-id." },
                "enabled": { "type": "boolean", "description": "Whether the job is enabled." },
                "limit": { "type": "integer", "description": "Maximum number of history entries to return (for history action). Default: 20, max: 100." }
              },
              "required": ["action"]
            }
            """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var action = ReadString(arguments, "action", required: true)!;
        if (!IsKnownAction(action))
            throw new ArgumentException($"Unsupported cron action '{action}'.");

        var prepared = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["action"] = action.ToLowerInvariant()
        };

        CopyString(arguments, prepared, "jobId");
        CopyString(arguments, prepared, "name");
        CopyString(arguments, prepared, "schedule");
        CopyString(arguments, prepared, "timeZone");
        CopyString(arguments, prepared, "agentId");
        CopyString(arguments, prepared, "message");
        CopyString(arguments, prepared, "templateName");
        if (TryReadStringMap(arguments, "templateParameters", out var templateParameters))
            prepared["templateParameters"] = templateParameters;
        CopyString(arguments, prepared, "model");

        if (arguments.TryGetValue("enabled", out var enabled) && enabled is not null)
            prepared["enabled"] = ReadBool(enabled, "enabled");

        if (arguments.TryGetValue("limit", out var limitVal) && limitVal is not null)
            prepared["limit"] = limitVal;

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(prepared);
    }

    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var action = arguments["action"]?.ToString() ?? string.Empty;
        return action switch
        {
            "list" => await ListAsync(arguments, cancellationToken).ConfigureAwait(false),
            "create" => await CreateAsync(arguments, cancellationToken).ConfigureAwait(false),
            "update" => await UpdateAsync(arguments, cancellationToken).ConfigureAwait(false),
            "delete" => await DeleteAsync(arguments, cancellationToken).ConfigureAwait(false),
            "run" => await RunAsync(arguments, cancellationToken).ConfigureAwait(false),
            "history" => await HistoryAsync(arguments, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported cron action '{action}'.")
        };
    }

    private async Task<AgentToolResult> ListAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var includeSystem = arguments.TryGetValue("includeSystem", out var val) && val is true or "true" or "True";
        var jobs = await cronStore.ListAsync(ct: cancellationToken).ConfigureAwait(false);
        var filtered = includeSystem ? jobs : jobs.Where(job => !job.System);
        var visible = allowCrossAgentCron
            ? filtered.ToList()
            : filtered.Where(job =>
                string.Equals(job.CreatedBy, _agentId.Value, StringComparison.OrdinalIgnoreCase)
                || (job.AgentId.HasValue && job.AgentId.Value == _agentId)).ToList();

        return TextResult(JsonSerializer.Serialize(visible, JsonOptions));
    }

    private async Task<AgentToolResult> CreateAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var schedule = ReadRequired(arguments, "schedule");
        var timeZone = ReadString(arguments, "timeZone");
        var message = ReadString(arguments, "message");
        var templateName = ReadString(arguments, "templateName");
        EnsurePromptSource(message, templateName);
        var tz = ResolveTimeZone(timeZone);

        DateTimeOffset? nextRunAt = null;
        try
        {
            var expr = Cronos.CronExpression.Parse(schedule, Cronos.CronFormat.Standard);
            nextRunAt = expr.GetNextOccurrence(now, tz);
        }
        catch { /* invalid schedule — will be caught by scheduler */ }

        var targetAgentIdString = ReadString(arguments, "agentId");
        var targetAgentId = ResolveTargetAgentId(targetAgentIdString, _agentId);

        var job = new CronJob
        {
            Id = JobId.From(Guid.NewGuid().ToString("N")),
            Name = ReadRequired(arguments, "name"),
            Schedule = schedule,
            ActionType = "agent-prompt",
            AgentId = targetAgentId,
            Message = message,
            TemplateName = templateName,
            TemplateParameters = ReadStringMap(arguments, "templateParameters"),
            Model = ReadString(arguments, "model"),
            Enabled = arguments.TryGetValue("enabled", out var enabled) && enabled is bool boolEnabled ? boolEnabled : true,
            TimeZone = timeZone,
            CreatedBy = _agentId.Value,
            CreatedAt = now,
            NextRunAt = nextRunAt,
            Metadata = new Dictionary<string, object?>()
        };

        var created = await cronStore.CreateAsync(job, cancellationToken).ConfigureAwait(false);
        return TextResult(JsonSerializer.Serialize(created, JsonOptions));
    }

    private async Task<AgentToolResult> UpdateAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var jobId = JobId.From(ReadRequired(arguments, "jobId"));
        var existing = await cronStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Cron job '{jobId.Value}' was not found.");

        EnsureCanManage(existing);

        var newSchedule = ReadString(arguments, "schedule") ?? existing.Schedule;
        var newTimeZone = arguments.ContainsKey("timeZone") ? ReadString(arguments, "timeZone") : existing.TimeZone;
        var newMessage = arguments.ContainsKey("message") ? ReadString(arguments, "message") : existing.Message;
        var newTemplateName = arguments.ContainsKey("templateName") ? ReadString(arguments, "templateName") : existing.TemplateName;
        var newTemplateParameters = arguments.ContainsKey("templateParameters")
            ? ReadStringMap(arguments, "templateParameters")
            : existing.TemplateParameters;
        EnsurePromptSource(newMessage, newTemplateName);

        var newAgentIdString = ReadString(arguments, "agentId");
        var newAgentId = string.IsNullOrWhiteSpace(newAgentIdString)
            ? existing.AgentId
            : ResolveTargetAgentId(newAgentIdString, _agentId);

        var updated = existing with
        {
            Name = ReadString(arguments, "name") ?? existing.Name,
            Schedule = newSchedule,
            TimeZone = newTimeZone,
            Message = newMessage,
            TemplateName = newTemplateName,
            TemplateParameters = newTemplateParameters,
            Model = ReadString(arguments, "model") ?? existing.Model,
            AgentId = newAgentId,
            Enabled = arguments.TryGetValue("enabled", out var enabled) && enabled is bool boolEnabled ? boolEnabled : existing.Enabled
        };

        // #2133: a tool definition update is a narrow write that never touches scheduler-owned
        // runtime bookkeeping (LastRun*/NextRunAt) or the CAS-pinned conversation, so it cannot
        // regress a concurrent run's status, timestamps, next run, or conversation pin.
        var saved = await cronStore.UpdateDefinitionAsync(updated, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Cron job '{jobId.Value}' was not found.");

        // Reschedule via the separate narrow next_run_at write only when the schedule or timezone
        // actually changed, so the reschedule cannot clobber a concurrent definition edit either.
        var scheduleChanged = !string.Equals(newSchedule, existing.Schedule, StringComparison.Ordinal);
        var tzChanged = !string.Equals(newTimeZone ?? "", existing.TimeZone ?? "", StringComparison.Ordinal);
        if (scheduleChanged || tzChanged)
        {
            var tz = ResolveTimeZone(newTimeZone);
            DateTimeOffset? nextRunAt = null;
            try
            {
                var expr = Cronos.CronExpression.Parse(newSchedule, Cronos.CronFormat.Standard);
                nextRunAt = expr.GetNextOccurrence(DateTimeOffset.UtcNow, tz);
            }
            catch { /* invalid schedule — will be caught by scheduler */ }

            await cronStore.SetNextRunAtAsync(jobId, nextRunAt, cancellationToken).ConfigureAwait(false);
            saved = await cronStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false) ?? saved;
        }

        return TextResult(JsonSerializer.Serialize(saved, JsonOptions));
    }

    private async Task<AgentToolResult> DeleteAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var jobId = JobId.From(ReadRequired(arguments, "jobId"));
        var existing = await cronStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Cron job '{jobId.Value}' was not found.");

        EnsureCanManage(existing);
        await cronStore.DeleteAsync(jobId, cancellationToken).ConfigureAwait(false);
        return TextResult($"Deleted cron job '{jobId.Value}'.");
    }

    private async Task<AgentToolResult> RunAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var jobId = JobId.From(ReadRequired(arguments, "jobId"));
        var existing = await cronStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Cron job '{jobId.Value}' was not found.");

        EnsureCanManage(existing);
        var run = await scheduler.RunNowAsync(jobId, cancellationToken).ConfigureAwait(false);
        return TextResult(JsonSerializer.Serialize(run, JsonOptions));
    }

    private async Task<AgentToolResult> HistoryAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var jobId = JobId.From(ReadRequired(arguments, "jobId"));
        var existing = await cronStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Cron job '{jobId.Value}' was not found.");

        EnsureCanManage(existing);

        var limit = ReadInt(arguments, "limit", defaultValue: 20);
        if (limit < 1) limit = 1;
        if (limit > 100) limit = 100;

        var runs = await cronStore.GetRunHistoryAsync(jobId, limit, cancellationToken).ConfigureAwait(false);
        return TextResult(JsonSerializer.Serialize(runs, JsonOptions));
    }

    // Scopes the target agent for create/update. When cross-agent cron is disabled (the
    // default), an explicit foreign agentId is rejected so an agent cannot create a job that
    // runs AS another agent, nor retarget an owned job onto another agent (issue #1667).
    // A blank/omitted agentId is treated as "the calling agent" and is always allowed.
    private AgentId ResolveTargetAgentId(string? requestedAgentId, AgentId callingAgent)
    {
        if (string.IsNullOrWhiteSpace(requestedAgentId))
            return callingAgent;

        var requested = AgentId.From(requestedAgentId);
        if (!allowCrossAgentCron && requested != callingAgent)
            throw new UnauthorizedAccessException("Cron jobs may only target the calling agent.");

        return requested;
    }

    private void EnsureCanManage(CronJob job)
    {
        if (allowCrossAgentCron)
            return;

        var isCreator = string.Equals(job.CreatedBy, _agentId.Value, StringComparison.OrdinalIgnoreCase);
        var isTarget = job.AgentId.HasValue && job.AgentId.Value == _agentId;
        if (!isCreator && !isTarget)
            throw new UnauthorizedAccessException("You can only manage cron jobs created by or targeting this agent.");
    }

    private static AgentToolResult TextResult(string text)
        => new([new AgentToolContent(AgentToolContentType.Text, text)]);

    private static TimeZoneInfo ResolveTimeZone(string? timeZone)
    {
        if (string.IsNullOrWhiteSpace(timeZone))
            return TimeZoneInfo.Utc;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static bool IsKnownAction(string action)
        => action.Equals("list", StringComparison.OrdinalIgnoreCase)
           || action.Equals("create", StringComparison.OrdinalIgnoreCase)
           || action.Equals("update", StringComparison.OrdinalIgnoreCase)
           || action.Equals("delete", StringComparison.OrdinalIgnoreCase)
           || action.Equals("run", StringComparison.OrdinalIgnoreCase)
           || action.Equals("history", StringComparison.OrdinalIgnoreCase);

    private static void CopyString(IReadOnlyDictionary<string, object?> source, Dictionary<string, object?> destination, string key)
    {
        var value = ReadString(source, key);
        if (!string.IsNullOrWhiteSpace(value))
            destination[key] = value;
    }

    private static string ReadRequired(IReadOnlyDictionary<string, object?> arguments, string key)
        => ReadString(arguments, key, required: true)!;

    private static string? ReadString(IReadOnlyDictionary<string, object?> arguments, string key, bool required = false)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
        {
            if (required)
                throw new ArgumentException($"Missing required argument: {key}.");

            return null;
        }

        var result = value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.ToString(),
            _ => value.ToString()
        };

        if (required && string.IsNullOrWhiteSpace(result))
            throw new ArgumentException($"Argument '{key}' cannot be empty.");

        return result;
    }

    private static IReadOnlyDictionary<string, string?>? ReadStringMap(IReadOnlyDictionary<string, object?> arguments, string key)
    {
        if (!TryReadStringMap(arguments, key, out var map))
            return null;

        return map;
    }

    private static bool TryReadStringMap(
        IReadOnlyDictionary<string, object?> arguments,
        string key,
        out IReadOnlyDictionary<string, string?> map)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
        {
            map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            return false;
        }

        map = value switch
        {
            JsonElement { ValueKind: JsonValueKind.Object } element => element
                .EnumerateObject()
                .ToDictionary(
                    property => property.Name,
                    property => property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.ToString(),
                    StringComparer.OrdinalIgnoreCase),
            IReadOnlyDictionary<string, string?> typed => new Dictionary<string, string?>(typed, StringComparer.OrdinalIgnoreCase),
            IReadOnlyDictionary<string, object?> dictionary => dictionary.ToDictionary(
                pair => pair.Key,
                pair => pair.Value?.ToString(),
                StringComparer.OrdinalIgnoreCase),
            _ => throw new ArgumentException($"Argument '{key}' must be an object with string values.")
        };

        return true;
    }

    private static void EnsurePromptSource(string? message, string? templateName)
    {
        if (string.IsNullOrWhiteSpace(message) && string.IsNullOrWhiteSpace(templateName))
            throw new ArgumentException("Either 'message' or 'templateName' is required.");
    }

    private static bool ReadBool(object value, string argumentName)
        => value switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.String } element when bool.TryParse(element.GetString(), out var parsed) => parsed,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => throw new ArgumentException($"Argument '{argumentName}' must be a boolean.")
        };

    private static int ReadInt(IReadOnlyDictionary<string, object?> arguments, string key, int defaultValue)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
            return defaultValue;

        return value switch
        {
            int i => i,
            long l => SaturateToInt32(l),
            double d => SaturateToInt32(d),
            JsonElement { ValueKind: JsonValueKind.Number } element => ReadNumberElement(element, defaultValue),
            JsonElement { ValueKind: JsonValueKind.String } element when int.TryParse(element.GetString(), out var parsed) => parsed,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => defaultValue
        };
    }

    // Reads a JSON number tolerantly: out-of-Int32-range and fractional values are
    // saturated/truncated instead of throwing out of JsonElement.GetInt32(). The caller
    // still clamps the result to a sane range (history limit is bounded to [1, 100]).
    private static int ReadNumberElement(JsonElement element, int defaultValue)
    {
        if (element.TryGetInt32(out var intValue))
            return intValue;
        if (element.TryGetInt64(out var longValue))
            return SaturateToInt32(longValue);
        if (element.TryGetDouble(out var doubleValue))
            return SaturateToInt32(doubleValue);
        return defaultValue;
    }

    private static int SaturateToInt32(long value)
        => value > int.MaxValue ? int.MaxValue
            : value < int.MinValue ? int.MinValue
            : (int)value;

    private static int SaturateToInt32(double value)
    {
        if (double.IsNaN(value)) return 0;
        if (value >= int.MaxValue) return int.MaxValue;
        if (value <= int.MinValue) return int.MinValue;
        return (int)value;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
