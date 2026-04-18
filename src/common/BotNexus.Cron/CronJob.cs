namespace BotNexus.Cron;

public sealed record CronJob
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Schedule { get; init; }
    public required string ActionType { get; init; }
    public string? AgentId { get; init; }
    public string? Message { get; init; }
    public string? WebhookUrl { get; init; }
    public string? ShellCommand { get; init; }
    public bool Enabled { get; init; } = true;
    /// <summary>Whether this is a system-provisioned job (e.g., heartbeat). Hidden from default listings.</summary>
    public bool System { get; init; }
    public string? TimeZone { get; init; }
    public string? CreatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastRunAt { get; init; }
    public DateTimeOffset? NextRunAt { get; init; }
    public string? LastRunStatus { get; init; }
    public string? LastRunError { get; init; }
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}
