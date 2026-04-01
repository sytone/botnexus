namespace BotNexus.Core.Observability;

public interface IBotNexusMetrics
{
    void IncrementMessagesProcessed(string channel);
    void IncrementToolCallsExecuted(string toolName);
    void RecordProviderLatency(string providerName, double elapsedMilliseconds);
    void UpdateExtensionsLoaded(int loadedCount);

    void IncrementCronJobsExecuted(string jobName);
    void IncrementCronJobsFailed(string jobName);
    void RecordCronJobDuration(string jobName, double elapsedMilliseconds);
    void IncrementCronJobsSkipped(string jobName, string reason);
}
