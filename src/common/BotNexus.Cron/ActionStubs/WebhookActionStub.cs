namespace BotNexus.Cron.Actions;

public sealed class WebhookAction : ICronAction
{
    public string ActionType => "webhook";

    public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
