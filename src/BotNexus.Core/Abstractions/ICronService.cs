namespace BotNexus.Core.Abstractions;

/// <summary>Central scheduler for all recurring work in BotNexus.</summary>
public interface ICronService
{
    /// <summary>Register a job from configuration or at runtime.</summary>
    void Register(ICronJob job);

    /// <summary>Remove a registered job by name.</summary>
    void Remove(string jobName);

    /// <summary>Get all registered jobs and their current status.</summary>
    IReadOnlyList<CronJobStatus> GetJobs();

    /// <summary>Get execution history for a specific job.</summary>
    IReadOnlyList<CronJobExecution> GetHistory(string jobName, int limit = 10);

    /// <summary>Manually trigger a job outside its schedule.</summary>
    Task TriggerAsync(string jobName, CancellationToken cancellationToken = default);

    /// <summary>Enable or disable a job at runtime.</summary>
    void SetEnabled(string jobName, bool enabled);
}
