using System.Diagnostics;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Cron.Jobs;

public sealed class SystemCronJob(CronJobConfig config, ISystemActionRegistry actionRegistry) : ICronJob
{
    private readonly CronJobConfig _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly ISystemActionRegistry _actionRegistry = actionRegistry ?? throw new ArgumentNullException(nameof(actionRegistry));

    public string Name => $"system:{_config.Action ?? "unknown"}";
    public CronJobType Type => CronJobType.System;
    public string Schedule => _config.Schedule;
    public TimeZoneInfo? TimeZone => ResolveTimeZone(_config.Timezone);
    public bool Enabled { get; set; } = config.Enabled;

    public async Task<CronJobResult> ExecuteAsync(CronJobContext context, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var actionName = _config.Action;
        if (string.IsNullOrWhiteSpace(actionName))
        {
            return new CronJobResult(
                Success: false,
                Error: "System job action is required.",
                Duration: stopwatch.Elapsed);
        }

        var action = _actionRegistry.Get(actionName);
        if (action is null)
        {
            return new CronJobResult(
                Success: false,
                Error: $"Unknown system action '{actionName}'.",
                Duration: stopwatch.Elapsed);
        }

        try
        {
            var output = await action.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            var routedChannels = await RouteOutputAsync(context, output, cancellationToken).ConfigureAwait(false);

            return new CronJobResult(
                Success: true,
                Output: output,
                Duration: stopwatch.Elapsed,
                Metadata: new Dictionary<string, object>
                {
                    ["action"] = action.Name,
                    ["routedChannels"] = routedChannels
                });
        }
        catch (Exception ex)
        {
            return new CronJobResult(
                Success: false,
                Error: ex.Message,
                Duration: stopwatch.Elapsed,
                Metadata: new Dictionary<string, object> { ["action"] = action.Name });
        }
    }

    private async Task<int> RouteOutputAsync(CronJobContext context, string output, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(output) || _config.OutputChannels.Count == 0)
            return 0;

        var channels = context.Services.GetServices<IChannel>();
        var channelsByName = channels.ToDictionary(channel => channel.Name, StringComparer.OrdinalIgnoreCase);
        var routedCount = 0;

        foreach (var channelName in _config.OutputChannels.Where(static channel => !string.IsNullOrWhiteSpace(channel)))
        {
            if (!channelsByName.TryGetValue(channelName, out var channel))
                continue;

            await channel.SendAsync(
                    new OutboundMessage(
                        Channel: channel.Name,
                        ChatId: $"cron:{context.JobName}",
                        Content: output,
                        Metadata: new Dictionary<string, object>
                        {
                            ["source"] = "cron",
                            ["job_name"] = context.JobName,
                            ["job_type"] = Type.ToString()
                        }),
                    cancellationToken)
                .ConfigureAwait(false);
            routedCount++;
        }

        return routedCount;
    }

    private static TimeZoneInfo? ResolveTimeZone(string? timezone)
    {
        if (string.IsNullOrWhiteSpace(timezone))
            return null;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }
}
