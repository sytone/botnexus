namespace BotNexus.Cron.Actions;

public sealed class WebhookAction : ICronAction
{
    public string ActionType => "webhook";

    public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "Cron webhook actions are not implemented yet. Job execution failed intentionally instead of succeeding silently.");
}
