namespace BotNexus.Cron;

public interface ICronAction
{
    string ActionType { get; }
    Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default);
}

public sealed record CronExecutionContext
{
    public required CronJob Job { get; init; }
    public required string RunId { get; init; }
    public required DateTimeOffset TriggeredAt { get; init; }
    public required CronTriggerType TriggerType { get; init; }
    public required IServiceProvider Services { get; init; }
    public string? SessionId { get; private set; }

    public void RecordSessionId(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionId = sessionId;
    }
}

public enum CronTriggerType
{
    Scheduled,
    Manual
}
