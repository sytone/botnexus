using BotNexus.Cron;
using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Api.Models;

/// <summary>
/// Request body for <c>POST /api/cron</c>.
/// </summary>
/// <remarks>
/// #2389: <see cref="CronJob.Id"/> is <c>required</c>, so binding the domain record directly made
/// a client-supplied id mandatory - creating a resource forced the caller to invent the server's
/// identifier, and omitting it failed during deserialization with a 400 before the action ever ran.
/// This request shape makes the id optional and lets the controller generate one, consistent with
/// how it already defaults <c>CreatedAt</c> and normalizes <c>ActionType</c>. An explicitly supplied
/// id is still honoured unchanged.
/// </remarks>
public sealed record CronJobCreateRequest
{
    /// <summary>Optional client-supplied identifier. When omitted the server generates one.</summary>
    public string? Id { get; init; }

    /// <summary>Job display name.</summary>
    public string? Name { get; init; }

    /// <summary>Standard 5-field cron expression.</summary>
    public string? Schedule { get; init; }

    /// <summary>Action performed when the job fires (e.g. <c>agent-prompt</c>, <c>command</c>).</summary>
    public string? ActionType { get; init; }

    /// <summary>Target agent identifier.</summary>
    public string? AgentId { get; init; }

    /// <summary>Prompt message for agent-prompt jobs.</summary>
    public string? Message { get; init; }

    /// <summary>Named prompt template reference for agent-prompt jobs.</summary>
    public string? TemplateName { get; init; }

    /// <summary>Parameter values applied when rendering <see cref="TemplateName"/>.</summary>
    public IReadOnlyDictionary<string, string?>? TemplateParameters { get; init; }

    /// <summary>Optional model override for agent-prompt jobs.</summary>
    public string? Model { get; init; }

    /// <summary>Webhook target for webhook jobs.</summary>
    public string? WebhookUrl { get; init; }

    /// <summary>Shell command executed by <c>command</c> jobs.</summary>
    public string? ShellCommand { get; init; }

    /// <summary>Whether the job is enabled. Defaults to <c>true</c>.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Whether this is a system-provisioned job.</summary>
    public bool System { get; init; }

    /// <summary>Whether the run's cron-scoped session is deleted after each run.</summary>
    public bool DeleteAfterRun { get; init; }

    /// <summary>IANA timezone the schedule is evaluated in.</summary>
    public string? TimeZone { get; init; }

    /// <summary>Creator identifier.</summary>
    public string? CreatedBy { get; init; }

    /// <summary>Creation timestamp. Defaults to now when omitted.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Next scheduled run.</summary>
    public DateTimeOffset? NextRunAt { get; init; }

    /// <summary>Arbitrary job metadata.</summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }

    /// <summary>
    /// Projects this request onto the domain record, generating an id when none was supplied.
    /// </summary>
    /// <returns>The cron job to persist.</returns>
    public CronJob ToCronJob() => new()
    {
        Id = JobId.From(string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id),
        Name = Name ?? string.Empty,
        Schedule = Schedule ?? string.Empty,
        ActionType = ActionType ?? string.Empty,
        AgentId = string.IsNullOrWhiteSpace(AgentId) ? null : Domain.Primitives.AgentId.From(AgentId),
        Message = Message,
        TemplateName = TemplateName,
        TemplateParameters = TemplateParameters,
        Model = Model,
        WebhookUrl = WebhookUrl,
        ShellCommand = ShellCommand,
        Enabled = Enabled,
        System = System,
        DeleteAfterRun = DeleteAfterRun,
        TimeZone = TimeZone,
        CreatedBy = CreatedBy,
        CreatedAt = CreatedAt,
        NextRunAt = NextRunAt,
        Metadata = Metadata
    };
}
