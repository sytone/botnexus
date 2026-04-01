using System.Diagnostics.Metrics;
using System.Threading;

namespace BotNexus.Core.Observability;

public sealed class BotNexusMetrics : IBotNexusMetrics, IDisposable
{
    private readonly Meter _meter = new("BotNexus.Platform", "1.0.0");
    private readonly Counter<long> _messagesProcessed;
    private readonly Counter<long> _toolCallsExecuted;
    private readonly Histogram<double> _providerLatency;
    private readonly ObservableGauge<int> _extensionsLoaded;
    private readonly Counter<long> _cronJobsExecuted;
    private readonly Counter<long> _cronJobsFailed;
    private readonly Histogram<double> _cronJobDuration;
    private readonly Counter<long> _cronJobsSkipped;
    private int _loadedExtensions;

    public BotNexusMetrics()
    {
        _messagesProcessed = _meter.CreateCounter<long>("botnexus.messages.processed");
        _toolCallsExecuted = _meter.CreateCounter<long>("botnexus.tool_calls.executed");
        _providerLatency = _meter.CreateHistogram<double>("botnexus.provider.latency", unit: "ms");
        _extensionsLoaded = _meter.CreateObservableGauge<int>("botnexus.extensions.loaded",
            () => Volatile.Read(ref _loadedExtensions));
        _cronJobsExecuted = _meter.CreateCounter<long>("botnexus.cron.jobs.executed");
        _cronJobsFailed = _meter.CreateCounter<long>("botnexus.cron.jobs.failed");
        _cronJobDuration = _meter.CreateHistogram<double>("botnexus.cron.job.duration", unit: "ms");
        _cronJobsSkipped = _meter.CreateCounter<long>("botnexus.cron.jobs.skipped");
    }

    public void IncrementMessagesProcessed(string channel)
        => _messagesProcessed.Add(1, new KeyValuePair<string, object?>("channel", channel));

    public void IncrementToolCallsExecuted(string toolName)
        => _toolCallsExecuted.Add(1, new KeyValuePair<string, object?>("tool", toolName));

    public void RecordProviderLatency(string providerName, double elapsedMilliseconds)
        => _providerLatency.Record(elapsedMilliseconds, new KeyValuePair<string, object?>("provider", providerName));

    public void UpdateExtensionsLoaded(int loadedCount)
        => Interlocked.Exchange(ref _loadedExtensions, loadedCount);

    public void IncrementCronJobsExecuted(string jobName)
        => _cronJobsExecuted.Add(1, new KeyValuePair<string, object?>("job", jobName));

    public void IncrementCronJobsFailed(string jobName)
        => _cronJobsFailed.Add(1, new KeyValuePair<string, object?>("job", jobName));

    public void RecordCronJobDuration(string jobName, double elapsedMilliseconds)
        => _cronJobDuration.Record(elapsedMilliseconds, new KeyValuePair<string, object?>("job", jobName));

    public void IncrementCronJobsSkipped(string jobName, string reason)
        => _cronJobsSkipped.Add(1,
            new KeyValuePair<string, object?>("job", jobName),
            new KeyValuePair<string, object?>("reason", reason));

    public void Dispose()
    {
        _meter.Dispose();
    }
}
