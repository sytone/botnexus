namespace BotNexus.Cron;

#pragma warning disable CS1591 // Config DTOs are self-descriptive and internal to scheduler plumbing

public sealed class CronOptions
{
    public const string SectionName = "cron";

    public bool Enabled { get; set; } = true;
    public int TickIntervalSeconds { get; set; } = 60;
    public int DefaultJobTimeoutSeconds { get; set; } = 3600;

    /// <summary>
    /// How far a run's started_at may deviate from now (in either direction) before the scheduler
    /// treats a still-<c>running</c> row as orphaned and stamps it as an error (#2410). Default: 24h.
    /// </summary>
    public int OrphanedRunThresholdSeconds { get; set; } = 86400;

    /// <summary>
    /// Default aggregate cap applied when <see cref="MaxConcurrentJobs"/> is absent or non-positive.
    /// Mirrors <c>SubAgentOptions.MaxConcurrentPerSession</c> as the naming/defaulting precedent (#2670).
    /// </summary>
    public const int DefaultMaxConcurrentJobs = 5;

    /// <summary>
    /// #2670: maximum number of due jobs the scheduler tick executes concurrently. The remainder queue
    /// and run as slots free; nothing is dropped. A non-positive value degrades to
    /// <see cref="DefaultMaxConcurrentJobs"/> rather than to unbounded fan-out. Independent of the
    /// per-job lock, which serialises repeat runs of a single job.
    /// </summary>
    public int MaxConcurrentJobs { get; set; } = DefaultMaxConcurrentJobs;
    public Dictionary<string, ConfiguredCronJob>? Jobs { get; set; }
    public Dictionary<string, ConfiguredPromptTemplate>? PromptTemplates { get; set; }
}

public sealed record ConfiguredCronJob
{
    public string? Name { get; init; }
    public string? Schedule { get; init; }
    public string? ActionType { get; init; }
    public string? AgentId { get; init; }
    public string? Message { get; init; }
    public string? TemplateName { get; init; }
    public IReadOnlyDictionary<string, string?>? TemplateParameters { get; init; }
    public string? Model { get; init; }
    public string? WebhookUrl { get; init; }
    public string? ShellCommand { get; init; }
    public bool Enabled { get; init; } = true;
    public bool System { get; init; }
    public bool DeleteAfterRun { get; init; }
    /// <summary>#2634: opt-in scheduler-driven one-shot job removal. Off by default.</summary>
    public bool DeleteJobAfterRun { get; init; }
    /// <summary>
    /// #2634: optional hard expiry instant (ISO-8601). Null/absent means no expiry, which is
    /// exactly today's behaviour. An unparseable value degrades to no expiry with a warning.
    /// </summary>
    public string? ExpiresAt { get; init; }
    /// <summary>
    /// #2985: opt-in execution-class marker. Declares that the job's contract is to perform work,
    /// so a run completing with zero tool invocations records <c>no_tool_calls</c> instead of
    /// <c>ok</c>. Off by default - an unmarked job is completely unaffected.
    /// </summary>
    public bool ExecutionClass { get; init; }

    /// <summary>#2557: opt-in failure alerting. Off by default.</summary>
    public bool FailureAlertsEnabled { get; init; }
    /// <summary>#2557: conversation id that failure alerts for this job are delivered to.</summary>
    public string? FailureAlertConversationId { get; init; }
    public string? TimeZone { get; init; }
    public string? CreatedBy { get; init; }
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

public sealed record ConfiguredPromptTemplate
{
    public string? Prompt { get; init; }
    public string? Description { get; init; }
    public IReadOnlyDictionary<string, string?>? Defaults { get; init; }
    public IReadOnlyDictionary<string, ConfiguredPromptTemplateParameter>? Parameters { get; init; }
}

public sealed record ConfiguredPromptTemplateParameter
{
    public string? Description { get; init; }
    public string? Default { get; init; }
    public bool Required { get; init; }
}
