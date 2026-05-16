namespace BotNexus.Cron;

#pragma warning disable CS1591 // Config DTOs are self-descriptive and internal to scheduler plumbing

public sealed class CronOptions
{
    public const string SectionName = "cron";

    public bool Enabled { get; set; } = true;
    public int TickIntervalSeconds { get; set; } = 60;
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
