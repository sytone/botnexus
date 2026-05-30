using BotNexus.Domain.Primitives;

namespace BotNexus.Cron;

public sealed record CronJob
{
    public required JobId Id { get; init; }
    public required string Name { get; init; }
    public required string Schedule { get; init; }
    public required string ActionType { get; init; }
    public AgentId? AgentId { get; init; }
    public string? Message { get; init; }
    /// <summary>
    /// Optional named prompt template reference for agent-prompt jobs.
    /// When set, the runtime resolves and renders this template at execution time.
    /// </summary>
    public string? TemplateName { get; init; }

    /// <summary>
    /// Optional parameter values applied when rendering <see cref="TemplateName"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? TemplateParameters { get; init; }
    public string? Model { get; init; }
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
    /// <summary>
    /// Canonical link from a cron job to its long-lived Conversation. P9-D inverts the
    /// previous "composite-id key" model: the job owns the link, and every run lands in
    /// that one conversation until the job is deleted.
    ///
    /// Null on creation. Stamped via CAS during the first run that requires a per-job
    /// conversation (currently only the agent-prompt action routed through CronTrigger;
    /// heartbeat and soul triggers manage their own per-agent conversations). Once
    /// stamped, immutable for the life of the job — operators wanting a fresh
    /// conversation thread delete the job and create a new one.
    /// </summary>
    public ConversationId? ConversationId { get; init; }
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}
