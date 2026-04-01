using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Cron.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Cron;

public sealed class CronJobFactory(
    IOptions<CronConfig> options,
    IOptions<BotNexusConfig> botNexusOptions,
    IServiceProvider serviceProvider,
    ILogger<CronJobFactory> logger)
{
    private readonly CronConfig _config = options?.Value ?? new CronConfig();
    private readonly BotNexusConfig _botNexusConfig = botNexusOptions?.Value ?? new BotNexusConfig();
    private readonly IServiceProvider _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    private readonly ILogger<CronJobFactory> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public void CreateAndRegisterAll(ICronService cronService)
    {
        ArgumentNullException.ThrowIfNull(cronService);

        var jobsToRegister = BuildCentralizedJobMapWithLegacyMigration();
        if (jobsToRegister.Count == 0)
        {
            _logger.LogInformation("No cron jobs configured under BotNexus:Cron:Jobs.");
            return;
        }

        foreach (var (jobKey, jobConfig) in jobsToRegister)
        {
            if (jobConfig is null)
            {
                _logger.LogWarning("Skipping cron job '{JobKey}' because configuration is null.", jobKey);
                continue;
            }

            try
            {
                var job = CreateJob(jobKey, jobConfig);
                if (job is null)
                    continue;

                cronService.Register(job);
                _logger.LogInformation(
                    "Registered cron job '{JobKey}' (name='{JobName}', type='{Type}', schedule='{Schedule}', enabled={Enabled})",
                    jobKey,
                    job.Name,
                    jobConfig.Type,
                    jobConfig.Schedule,
                    jobConfig.Enabled);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping invalid cron job '{JobKey}' (type='{Type}')", jobKey, jobConfig.Type);
            }
        }
    }

    private Dictionary<string, CronJobConfig> BuildCentralizedJobMapWithLegacyMigration()
    {
        var jobs = new Dictionary<string, CronJobConfig>(_config.Jobs, StringComparer.OrdinalIgnoreCase);
        var migratedJobs = MigrateLegacyAgentCronJobs(jobs);
        if (migratedJobs == 0)
            return jobs;

        _logger.LogWarning("AgentConfig.CronJobs is deprecated. Migrate to Cron.Jobs in config.json.");
        return jobs;
    }

    private int MigrateLegacyAgentCronJobs(Dictionary<string, CronJobConfig> targetJobs)
    {
        var migrated = 0;
        foreach (var (agentName, agentConfig) in _botNexusConfig.Agents.Named)
        {
#pragma warning disable CS0618
            if (agentConfig.CronJobs.Count == 0)
                continue;

            for (var index = 0; index < agentConfig.CronJobs.Count; index++)
            {
                var legacyJob = agentConfig.CronJobs[index];
#pragma warning restore CS0618
                var migratedJob = CloneLegacyJob(agentName, legacyJob);
                var jobKey = GetUniqueMigratedKey(targetJobs, agentName, index);
                targetJobs[jobKey] = migratedJob;
                migrated++;

                _logger.LogInformation(
                    "Migrated legacy cron job for agent '{AgentName}' to Cron.Jobs key '{JobKey}' (type='{Type}', schedule='{Schedule}')",
                    agentName,
                    jobKey,
                    migratedJob.Type,
                    migratedJob.Schedule);
            }
        }

        return migrated;
    }

    private static CronJobConfig CloneLegacyJob(string agentName, CronJobConfig legacyJob)
    {
        var migrated = new CronJobConfig
        {
            Schedule = legacyJob.Schedule,
            Type = legacyJob.Type,
            Enabled = legacyJob.Enabled,
            Timezone = legacyJob.Timezone,
            Agent = legacyJob.Agent,
            Prompt = legacyJob.Prompt,
            Session = legacyJob.Session,
            Action = legacyJob.Action,
            Agents = legacyJob.Agents.ToList(),
            SessionCleanupDays = legacyJob.SessionCleanupDays,
            LogRetentionDays = legacyJob.LogRetentionDays,
            LogsPath = legacyJob.LogsPath,
            OutputChannels = legacyJob.OutputChannels.ToList()
        };

        if (string.Equals(migrated.Type, "agent", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(migrated.Agent))
        {
            migrated.Agent = agentName;
        }

        return migrated;
    }

    private static string GetUniqueMigratedKey(Dictionary<string, CronJobConfig> jobs, string agentName, int index)
    {
        var baseKey = $"{agentName}-legacy-{index + 1}";
        if (!jobs.ContainsKey(baseKey))
            return baseKey;

        var suffix = 2;
        while (jobs.ContainsKey($"{baseKey}-{suffix}"))
            suffix++;

        return $"{baseKey}-{suffix}";
    }

    private ICronJob? CreateJob(string jobKey, CronJobConfig jobConfig)
    {
        var type = jobConfig.Type?.Trim().ToLowerInvariant();
        return type switch
        {
            "agent" => CreateAgentJob(jobConfig),
            "system" => CreateSystemJob(jobConfig),
            "maintenance" => CreateMaintenanceJob(jobConfig),
            _ => HandleUnknownType(jobKey, jobConfig.Type)
        };
    }

    private ICronJob CreateAgentJob(CronJobConfig jobConfig)
    {
        var agentRunnerFactory = _serviceProvider.GetRequiredService<IAgentRunnerFactory>();
        var sessionManager = _serviceProvider.GetRequiredService<ISessionManager>();
        var jobLogger = _serviceProvider.GetService<ILogger<AgentCronJob>>();

        IChannel? ResolveChannel(string channelName)
            => _serviceProvider.GetServices<IChannel>()
                .FirstOrDefault(channel => string.Equals(channel.Name, channelName, StringComparison.OrdinalIgnoreCase));

        return new AgentCronJob(jobConfig, agentRunnerFactory, sessionManager, ResolveChannel, jobLogger);
    }

    private ICronJob CreateSystemJob(CronJobConfig jobConfig)
    {
        var actionRegistry = _serviceProvider.GetRequiredService<ISystemActionRegistry>();
        return new SystemCronJob(jobConfig, actionRegistry);
    }

    private ICronJob CreateMaintenanceJob(CronJobConfig jobConfig)
    {
        var memoryConsolidator = _serviceProvider.GetRequiredService<IMemoryConsolidator>();
        var sessionManager = _serviceProvider.GetRequiredService<ISessionManager>();
        return new MaintenanceCronJob(jobConfig, memoryConsolidator, sessionManager);
    }

    private ICronJob? HandleUnknownType(string jobKey, string? configuredType)
    {
        _logger.LogWarning(
            "Skipping cron job '{JobKey}' because type '{Type}' is not supported. Expected: agent, system, maintenance.",
            jobKey,
            configuredType ?? "<null>");
        return null;
    }
}

public sealed class CronJobRegistrationHostedService(
    CronJobFactory cronJobFactory,
    ICronService cronService,
    ILogger<CronJobRegistrationHostedService> logger) : IHostedService
{
    private readonly CronJobFactory _cronJobFactory = cronJobFactory ?? throw new ArgumentNullException(nameof(cronJobFactory));
    private readonly ICronService _cronService = cronService ?? throw new ArgumentNullException(nameof(cronService));
    private readonly ILogger<CronJobRegistrationHostedService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _cronJobFactory.CreateAndRegisterAll(_cronService);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cron job registration failed during startup.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
