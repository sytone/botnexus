namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Describes a request to spawn a background sub-agent session.
/// </summary>
public sealed record SubAgentSpawnRequest
{
    /// <summary>
    /// Gets the parent agent identifier initiating the spawn.
    /// </summary>
    public required string ParentAgentId { get; init; }

    /// <summary>
    /// Gets the parent session identifier that owns the sub-agent.
    /// </summary>
    public required string ParentSessionId { get; init; }

    /// <summary>
    /// Gets the delegated task prompt for the sub-agent.
    /// </summary>
    public required string Task { get; init; }

    /// <summary>
    /// Gets an optional friendly name for the sub-agent instance.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets an optional model override for the sub-agent run.
    /// </summary>
    public string? ModelOverride { get; init; }

    /// <summary>
    /// Gets an optional API provider override for the sub-agent run.
    /// </summary>
    public string? ApiProviderOverride { get; init; }

    /// <summary>
    /// Gets an optional tool allowlist for the sub-agent.
    /// </summary>
    public IReadOnlyList<string>? ToolIds { get; init; }

    /// <summary>
    /// Gets an optional system prompt override for the sub-agent.
    /// </summary>
    public string? SystemPromptOverride { get; init; }

    /// <summary>
    /// Gets the maximum number of turns the sub-agent may execute.
    /// </summary>
    public int MaxTurns { get; init; } = 30;

    /// <summary>
    /// Gets the timeout, in seconds, for the sub-agent execution.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 600;
}
