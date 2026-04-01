using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Tools;

/// <summary>Tool for managing cron jobs from within an agent.</summary>
public sealed class CronTool : ToolBase
{
    private readonly ICronService? _cronService;

    public CronTool(ICronService? cronService = null, ILogger? logger = null)
        : base(logger)
    {
        _cronService = cronService;
    }

    /// <inheritdoc/>
    public override ToolDefinition Definition => new(
        "cron",
        "Schedule or manage cron jobs. Actions: schedule, remove, list.",
        new Dictionary<string, ToolParameterSchema>
        {
            ["action"] = new("string", "Action: schedule, remove, or list", Required: true,
                EnumValues: ["schedule", "remove", "list"]),
            ["name"] = new("string", "Job name (for schedule/remove)", Required: false),
            ["expression"] = new("string", "Cron expression (for schedule)", Required: false),
            ["message"] = new("string", "Message payload (for schedule)", Required: false)
        });

    /// <inheritdoc/>
    protected override Task<string> ExecuteCoreAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        if (_cronService is null)
            return Task.FromResult("Error: Cron service not available");

        var action = GetOptionalString(arguments, "action", "list");

        return action.ToLowerInvariant() switch
        {
            "list" => Task.FromResult(string.Join("\n", _cronService.GetJobs().Select(j => j.Name))),
            "remove" => RemoveJob(arguments),
            "schedule" => ScheduleJob(arguments),
            _ => throw new ToolArgumentException($"Unknown action '{action}'")
        };
    }

    private Task<string> ScheduleJob(IReadOnlyDictionary<string, object?> args)
    {
        var name = GetRequiredString(args, "name");
        var expression = GetRequiredString(args, "expression");

        var message = GetOptionalString(args, "message", $"Cron job '{name}' executed.");
        _cronService!.Register(new ToolCronJob(name, expression, message));
        return Task.FromResult($"Cron job '{name}' scheduled with expression '{expression}'");
    }

    private Task<string> RemoveJob(IReadOnlyDictionary<string, object?> args)
    {
        var name = GetRequiredString(args, "name");
        _cronService!.Remove(name);
        return Task.FromResult($"Cron job '{name}' removed");
    }

    private sealed class ToolCronJob(string name, string schedule, string message) : ICronJob
    {
        public string Name { get; } = name;
        public CronJobType Type => CronJobType.System;
        public string Schedule { get; } = schedule;
        public TimeZoneInfo? TimeZone => TimeZoneInfo.Utc;
        public bool Enabled { get; set; } = true;

        public Task<CronJobResult> ExecuteAsync(CronJobContext context, CancellationToken cancellationToken)
            => Task.FromResult(new CronJobResult(
                Success: true,
                Output: message,
                Duration: TimeSpan.Zero));
    }
}
