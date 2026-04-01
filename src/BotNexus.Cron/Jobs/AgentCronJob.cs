using System.Diagnostics;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Cron.Jobs;

/// <summary>
/// Cron job that executes a prompt through the agent runner pipeline.
/// </summary>
public sealed class AgentCronJob : ICronJob
{
    private readonly CronJobConfig _config;
    private readonly IAgentRunnerFactory _agentRunnerFactory;
    private readonly ISessionManager _sessionManager;
    private readonly Func<string, IChannel?> _channelResolver;
    private readonly ILogger<AgentCronJob> _logger;

    public AgentCronJob(
        CronJobConfig config,
        IAgentRunnerFactory agentRunnerFactory,
        ISessionManager sessionManager,
        Func<string, IChannel?> channelResolver,
        ILogger<AgentCronJob>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(agentRunnerFactory);
        ArgumentNullException.ThrowIfNull(sessionManager);
        ArgumentNullException.ThrowIfNull(channelResolver);

        if (string.IsNullOrWhiteSpace(config.Agent))
            throw new ArgumentException("Cron job Agent must be provided for agent jobs.", nameof(config));

        if (string.IsNullOrWhiteSpace(config.Prompt))
            throw new ArgumentException("Cron job Prompt must be provided for agent jobs.", nameof(config));

        _config = config;
        _agentRunnerFactory = agentRunnerFactory;
        _sessionManager = sessionManager;
        _channelResolver = channelResolver;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentCronJob>.Instance;

        Name = config.Agent;
        Schedule = config.Schedule;
        Enabled = config.Enabled;
        TimeZone = ResolveTimeZone(config.Timezone);
    }

    public string Name { get; }
    public CronJobType Type => CronJobType.Agent;
    public string Schedule { get; }
    public TimeZoneInfo? TimeZone { get; }
    public bool Enabled { get; set; }

    public async Task<CronJobResult> ExecuteAsync(CronJobContext context, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();

        try
        {
            var jobName = string.IsNullOrWhiteSpace(context.JobName) ? Name : context.JobName;
            var agentName = _config.Agent!;
            var sessionKey = ResolveSessionKey(jobName, context.ActualTime);

            var message = new InboundMessage(
                Channel: "cron",
                SenderId: $"cron:{jobName}",
                ChatId: sessionKey,
                Content: _config.Prompt!,
                Timestamp: context.ActualTime,
                Media: [],
                Metadata: new Dictionary<string, object>
                {
                    ["source"] = "cron",
                    ["cron_job"] = jobName,
                    [InboundMessageCorrelationExtensions.CorrelationIdMetadataKey] = context.CorrelationId,
                    ["agent"] = agentName
                },
                SessionKeyOverride: sessionKey);

            var runner = _agentRunnerFactory.Create(agentName);
            await runner.RunAsync(message, cancellationToken).ConfigureAwait(false);

            var session = await _sessionManager.GetOrCreateAsync(sessionKey, agentName, cancellationToken).ConfigureAwait(false);
            var response = session.History.LastOrDefault(entry => entry.Role == MessageRole.Assistant)?.Content;

            if (_config.OutputChannels.Count == 0)
            {
                _logger.LogInformation("Cron job {JobName} completed. Response: {Response}", jobName, response ?? "<empty>");
            }
            else if (!string.IsNullOrWhiteSpace(response))
            {
                await RouteToChannelsAsync(context, jobName, response, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation("Cron job {JobName} completed with no response to route.", jobName);
            }

            timer.Stop();
            return new CronJobResult(Success: true, Output: response, Duration: timer.Elapsed);
        }
        catch (Exception ex)
        {
            timer.Stop();
            _logger.LogError(ex, "Cron job {JobName} failed", context.JobName);
            return new CronJobResult(Success: false, Error: ex.Message, Duration: timer.Elapsed);
        }
    }

    private async Task RouteToChannelsAsync(
        CronJobContext context,
        string jobName,
        string response,
        CancellationToken cancellationToken)
    {
        var tasks = new List<Task>();
        var channelNames = _config.OutputChannels
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var channelName in channelNames)
        {
            var channel = _channelResolver(channelName);
            if (channel is null)
            {
                _logger.LogWarning("Cron job {JobName}: channel '{ChannelName}' was not found", jobName, channelName);
                continue;
            }

            if (!channel.IsRunning)
            {
                _logger.LogWarning("Cron job {JobName}: channel '{ChannelName}' is not running", jobName, channelName);
                continue;
            }

            tasks.Add(channel.SendAsync(
                new OutboundMessage(
                    Channel: channel.Name,
                    ChatId: $"cron:{jobName}",
                    Content: response,
                    Metadata: new Dictionary<string, object>
                    {
                        ["source"] = "cron",
                        ["job_name"] = jobName,
                        ["job_type"] = Type.ToString(),
                        ["correlation_id"] = context.CorrelationId,
                        ["scheduled_time"] = context.ScheduledTime.ToString("O")
                    }),
                cancellationToken));
        }

        if (tasks.Count > 0)
            await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private string ResolveSessionKey(string jobName, DateTimeOffset timestamp)
    {
        var sessionMode = _config.Session?.Trim();

        if (string.Equals(sessionMode, "persistent", StringComparison.OrdinalIgnoreCase))
            return $"cron:{jobName}";

        if (sessionMode is not null &&
            sessionMode.StartsWith("named:", StringComparison.OrdinalIgnoreCase))
        {
            var explicitKey = sessionMode["named:".Length..].Trim();
            return string.IsNullOrWhiteSpace(explicitKey) ? $"cron:{jobName}" : explicitKey;
        }

        return $"cron:{jobName}:{timestamp:yyyyMMddHHmmss}";
    }

    private TimeZoneInfo? ResolveTimeZone(string? timezone)
    {
        if (string.IsNullOrWhiteSpace(timezone))
            return null;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (TimeZoneNotFoundException)
        {
            _logger.LogWarning("Cron job timezone '{Timezone}' was not found; defaulting to UTC", timezone);
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            _logger.LogWarning("Cron job timezone '{Timezone}' was invalid; defaulting to UTC", timezone);
            return null;
        }
    }
}
