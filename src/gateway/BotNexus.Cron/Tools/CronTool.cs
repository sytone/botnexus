using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Cron.Actions;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Domain.Primitives;

namespace BotNexus.Cron.Tools;

public sealed class CronTool(
    ICronStore cronStore,
    CronScheduler scheduler,
    AgentId agentId,
    bool allowCrossAgentCron = false,
    ModelRegistry? modelRegistry = null,
    ICommandCronAuthorizer? commandAuthorizer = null) : IAgentTool
{
    private readonly AgentId _agentId = agentId;

    /// <summary>
    /// #2462 (AUTHORING half): every create/update that would persist a <c>shellCommand</c> is
    /// checked here, using the same <see cref="ICommandCronAuthorizer"/> seam - and therefore the
    /// same exec-tool policy vocabulary - that gates FIRING in <c>CommandCronAction</c>. Firing is
    /// still gated independently: policy can tighten after a job is stored, and jobs can be created
    /// through paths other than this tool.
    ///
    /// Fails CLOSED: when no authorizer was supplied the gate cannot be evaluated, so the write is
    /// refused rather than silently allowed.
    /// </summary>
    private void EnsureCommandAuthorized(CronJob job, string command)
    {
        var decision = commandAuthorizer is null
            ? CommandAuthorizationDecision.Deny(
                $"no {nameof(ICommandCronAuthorizer)} is available, so the command cannot be classified; failing closed")
            : commandAuthorizer.AuthorizeAuthoring(job, command);

        if (!decision.Allowed)
        {
            throw new UnauthorizedAccessException(
                $"Cron command job '{job.Name}' was denied by the command authorization policy: {decision.Reason}");
        }
    }

    public string Name => "cron";
    public string Label => "Cron Job Manager";

    public Tool Definition => new(
        Name,
        "Manage scheduled cron jobs. Create, list, update, delete, and run cron jobs. A job is either an 'agent-prompt' job (the default - costs a model turn on every fire, requires 'message' or 'templateName') or a 'command' job (runs 'shellCommand' directly and costs no tokens, requires 'shellCommand').",
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
                "actionType": {
                  "type": "string",
                  "enum": ["agent-prompt", "command"],
                  "description": "What the job does when it fires. 'agent-prompt' (default) sends a prompt to the agent and requires 'message' or 'templateName'. 'command' runs 'shellCommand' as a script and requires 'shellCommand'. On update, omitting it keeps the current action type; supplying a different one switches the job and clears the fields belonging to the other action type."
                },
                "shellCommand": { "type": "string", "description": "Shell command or script to run (required for create/update of actionType 'command'). This is an arbitrary-execution surface - treat creating or editing a command job as a dangerous operation." },
                "message": { "type": "string", "description": "Prompt message (for create/update). Optional when templateName is provided." },
                "templateName": { "type": "string", "description": "Named prompt template reference (for create/update)." },
                "templateParameters": {
                  "type": "object",
                  "description": "Template parameter values for templateName (for create/update).",
                  "additionalProperties": { "type": "string" }
                },
                "model": { "type": "string", "description": "Optional model override for agent-prompt jobs. Supports model-id or provider/model-id. Validated against the model registry at create/update time; an unknown id is rejected with the available models." },
                "enabled": { "type": "boolean", "description": "Whether the job is enabled." },
                "deleteJobAfterRun": { "type": "boolean", "description": "One-shot lifecycle. When true, the SCHEDULER deletes this job itself after its first terminal run (success, timeout, error, or abort alike). Use this instead of writing 'delete this cron job after running' into the prompt - a prompt instruction has no enforcement and no retry if the turn ends early. Distinct from 'deleteAfterRun'; this removes the JOB. Default: false." },
                "expiresAt": { "type": "string", "description": "Optional hard expiry instant (ISO-8601, e.g. '2026-12-31T00:00:00Z'). From that instant on the job stops executing: the scheduler suppresses the fire and never invokes the action. The job is NOT deleted or disabled, so it stays visible for a human to extend. Omit for no expiry (the default, identical to today's behaviour); pass an empty string on update to clear an existing expiry." },
                "deleteAfterRun": { "type": "boolean", "description": "Ephemeral run-SESSION cleanup. When true, the run's cron-scoped session and transcript are deleted after each run. This does NOT delete the job - for that use 'deleteJobAfterRun'. Default: false." },
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
        CopyString(arguments, prepared, "actionType");
        CopyString(arguments, prepared, "shellCommand");
        if (TryReadStringMap(arguments, "templateParameters", out var templateParameters))
            prepared["templateParameters"] = templateParameters;
        CopyString(arguments, prepared, "model");

        if (arguments.TryGetValue("enabled", out var enabled) && enabled is not null)
            prepared["enabled"] = ReadBool(enabled, "enabled");

        // #2634: lifecycle fields. Booleans are normalized here like 'enabled'; expiresAt is copied
        // through ContainsKey (not CopyString) so an explicit empty string -- which means "clear the
        // expiry" on update -- survives instead of being swallowed as blank.
        if (arguments.TryGetValue("deleteJobAfterRun", out var deleteJobAfterRun) && deleteJobAfterRun is not null)
            prepared["deleteJobAfterRun"] = ReadBool(deleteJobAfterRun, "deleteJobAfterRun");

        if (arguments.TryGetValue("deleteAfterRun", out var deleteAfterRun) && deleteAfterRun is not null)
            prepared["deleteAfterRun"] = ReadBool(deleteAfterRun, "deleteAfterRun");

        if (arguments.ContainsKey("expiresAt"))
            prepared["expiresAt"] = ReadString(arguments, "expiresAt") ?? string.Empty;

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
        // #2389: the action type decides which fields are required and which are even meaningful.
        // Defaults to 'agent-prompt' so every pre-existing caller is unaffected.
        var actionType = NormalizeRequestedActionType(ReadString(arguments, "actionType")) ?? "agent-prompt";
        var isCommand = string.Equals(actionType, "command", StringComparison.Ordinal);

        // Each action type carries only its own fields, so a command job can never be persisted
        // holding a stale prompt (or vice versa) that the scheduler would silently ignore.
        var message = isCommand ? null : ReadString(arguments, "message");
        var templateName = isCommand ? null : ReadString(arguments, "templateName");
        var shellCommand = isCommand ? ReadString(arguments, "shellCommand") : null;
        if (isCommand)
            EnsureShellCommand(shellCommand);
        else
            EnsurePromptSource(message, templateName);

        var tz = ResolveTimeZone(timeZone);

        DateTimeOffset? nextRunAt = null;
        try
        {
            var expr = Cronos.CronExpression.Parse(schedule, Cronos.CronFormat.Standard);
            nextRunAt = expr.GetNextOccurrence(now, tz);
        }
        catch { /* invalid schedule — will be caught by scheduler */ }

        var model = ReadString(arguments, "model");
        EnsureModelResolvable(model);

        var targetAgentIdString = ReadString(arguments, "agentId");
        var targetAgentId = ResolveTargetAgentId(targetAgentIdString, _agentId);

        var job = new CronJob
        {
            Id = JobId.From(Guid.NewGuid().ToString("N")),
            Name = ReadRequired(arguments, "name"),
            Schedule = schedule,
            ActionType = actionType,
            AgentId = targetAgentId,
            Message = message,
            TemplateName = templateName,
            TemplateParameters = isCommand ? null : ReadStringMap(arguments, "templateParameters"),
            ShellCommand = shellCommand,
            Model = model,
            Enabled = arguments.TryGetValue("enabled", out var enabled) && enabled is bool boolEnabled ? boolEnabled : true,
            // #2634: both default to the inert state, so a create that omits them is byte-identical
            // to a create today.
            DeleteJobAfterRun = arguments.TryGetValue("deleteJobAfterRun", out var djar) && djar is bool b1 && b1,
            DeleteAfterRun = arguments.TryGetValue("deleteAfterRun", out var dar) && dar is bool b2 && b2,
            ExpiresAt = ParseExpiresAt(ReadString(arguments, "expiresAt")),
            TimeZone = timeZone,
            CreatedBy = _agentId.Value,
            CreatedAt = now,
            NextRunAt = nextRunAt,
            Metadata = new Dictionary<string, object?>()
        };

        // AUTHORING gate (#2462) - before any store write, so a denied command leaves no row behind.
        if (isCommand)
            EnsureCommandAuthorized(job, shellCommand!);

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

        // #2389: an omitted actionType keeps the job's existing one, so a prompt-irrelevant edit
        // (schedule / enabled / name / timeZone) on a command job is no longer asked for a prompt
        // it will never use. On an explicit switch the previous action type's fields are dropped
        // rather than inherited, so the job cannot be left internally inconsistent.
        var newActionType = NormalizeRequestedActionType(ReadString(arguments, "actionType")) ?? existing.ActionType;
        var switchingActionType = !string.Equals(newActionType, existing.ActionType, StringComparison.Ordinal);

        var newMessage = arguments.ContainsKey("message")
            ? ReadString(arguments, "message")
            : switchingActionType ? null : existing.Message;
        var newTemplateName = arguments.ContainsKey("templateName")
            ? ReadString(arguments, "templateName")
            : switchingActionType ? null : existing.TemplateName;
        var newTemplateParameters = arguments.ContainsKey("templateParameters")
            ? ReadStringMap(arguments, "templateParameters")
            : switchingActionType ? null : existing.TemplateParameters;
        var newShellCommand = arguments.ContainsKey("shellCommand")
            ? ReadString(arguments, "shellCommand")
            : switchingActionType ? null : existing.ShellCommand;

        if (string.Equals(newActionType, "command", StringComparison.Ordinal))
        {
            EnsureShellCommand(newShellCommand);
            newMessage = null;
            newTemplateName = null;
            newTemplateParameters = null;
        }
        else
        {
            EnsurePromptSource(newMessage, newTemplateName);
            newShellCommand = null;
        }

        // Only a caller-supplied override is preflighted. An update that leaves Model alone must
        // not be blocked by a pre-existing bad value, or a job whose model was decommissioned
        // after creation could never be edited (not even to fix the model itself).
        var requestedModel = ReadString(arguments, "model");
        EnsureModelResolvable(requestedModel);

        var newAgentIdString = ReadString(arguments, "agentId");
        var newAgentId = string.IsNullOrWhiteSpace(newAgentIdString)
            ? existing.AgentId
            : ResolveTargetAgentId(newAgentIdString, _agentId);

        var updated = existing with
        {
            Name = ReadString(arguments, "name") ?? existing.Name,
            Schedule = newSchedule,
            TimeZone = newTimeZone,
            ActionType = newActionType,
            ShellCommand = newShellCommand,
            Message = newMessage,
            TemplateName = newTemplateName,
            TemplateParameters = newTemplateParameters,
            Model = requestedModel ?? existing.Model,
            AgentId = newAgentId,
            Enabled = arguments.TryGetValue("enabled", out var enabled) && enabled is bool boolEnabled ? boolEnabled : existing.Enabled,
            // #2634: an omitted lifecycle field leaves the stored value alone, so an unrelated edit
            // (schedule / name / enabled) can never accidentally clear a one-shot or an expiry.
            DeleteJobAfterRun = arguments.TryGetValue("deleteJobAfterRun", out var djar) && djar is bool b1
                ? b1
                : existing.DeleteJobAfterRun,
            DeleteAfterRun = arguments.TryGetValue("deleteAfterRun", out var dar) && dar is bool b2
                ? b2
                : existing.DeleteAfterRun,
            ExpiresAt = arguments.ContainsKey("expiresAt")
                ? ParseExpiresAt(ReadString(arguments, "expiresAt"))
                : existing.ExpiresAt
        };

        // #2133: a tool definition update is a narrow write that never touches scheduler-owned
        // runtime bookkeeping (LastRun*/NextRunAt) or the CAS-pinned conversation, so it cannot
        // regress a concurrent run's status, timestamps, next run, or conversation pin.
        // AUTHORING gate (#2462). A retained command is re-checked too, so a policy tightened after
        // creation takes effect on the next edit instead of being grandfathered in.
        if (string.Equals(updated.ActionType, "command", StringComparison.Ordinal))
            EnsureCommandAuthorized(updated, newShellCommand!);

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

    // #2634: parses the caller-supplied expiry. Null/blank means "no expiry" (and on update, an
    // explicit empty string clears an existing one). An unparseable value is REJECTED rather than
    // silently dropped: quietly discarding a bad expiry would leave the agent believing the job
    // will stop firing when it never will - the exact failure this issue is about.
    private static DateTimeOffset? ParseExpiresAt(string? expiresAt)
    {
        if (string.IsNullOrWhiteSpace(expiresAt))
            return null;

        if (DateTimeOffset.TryParse(
                expiresAt,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return parsed;
        }

        throw new ArgumentException(
            $"Argument 'expiresAt' must be an ISO-8601 instant (e.g. '2026-12-31T00:00:00Z'); got '{expiresAt}'.");
    }

    // #2373: reject an unresolvable model override at create/update time rather than letting the
    // job silently fail on every fire. When no populated registry is available the override is
    // accepted unchanged - "cannot verify" must never become "reject".
    private void EnsureModelResolvable(string? model)
    {
        if (CronModelPreflight.ClassifyRejection(modelRegistry, model) is { } reason)
            throw new ArgumentException(reason);
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

    // #2389: the command counterpart of EnsurePromptSource. A command job with nothing to run is
    // just as invalid as an agent-prompt job with nothing to say - relaxing the prompt requirement
    // per action type must not degrade into "anything goes".
    private static void EnsureShellCommand(string? shellCommand)
    {
        if (string.IsNullOrWhiteSpace(shellCommand))
            throw new ArgumentException("'shellCommand' is required when actionType is 'command'.");
    }

    // Returns null when the caller supplied no action type, meaning "default on create / leave
    // alone on update". Only action types the tool can fully validate are accepted; 'agent-chat'
    // is the historical alias for 'agent-prompt' and is normalized the same way the REST API
    // normalizes it. An unknown value is rejected rather than silently persisted as a job the
    // scheduler has no action for.
    private static string? NormalizeRequestedActionType(string? actionType)
    {
        if (string.IsNullOrWhiteSpace(actionType))
            return null;

        var trimmed = actionType.Trim();
        if (trimmed.Equals("agent-prompt", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("agent-chat", StringComparison.OrdinalIgnoreCase))
            return "agent-prompt";
        if (trimmed.Equals("command", StringComparison.OrdinalIgnoreCase))
            return "command";

        throw new ArgumentException(
            $"Unsupported cron actionType '{actionType}'. Supported values are 'agent-prompt' and 'command'.");
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
