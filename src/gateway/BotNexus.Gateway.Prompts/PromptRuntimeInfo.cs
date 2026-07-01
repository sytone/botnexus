namespace BotNexus.Gateway.Prompts;

/// <summary>
/// Represents prompt runtime info.
/// </summary>
public sealed record PromptRuntimeInfo
{
    /// <summary>
    /// Gets or sets the agent id.
    /// </summary>
    public string? AgentId { get; init; }
    /// <summary>
    /// Gets or sets the host.
    /// </summary>
    public string? Host { get; init; }
    /// <summary>
    /// Gets or sets the os.
    /// </summary>
    public string? Os { get; init; }
    /// <summary>
    /// Gets or sets the arch.
    /// </summary>
    public string? Arch { get; init; }
    /// <summary>
    /// Gets or sets the provider.
    /// </summary>
    public string? Provider { get; init; }
    /// <summary>
    /// Gets or sets the model.
    /// </summary>
    public string? Model { get; init; }
    /// <summary>
    /// Gets or sets the default model.
    /// </summary>
    public string? DefaultModel { get; init; }
    /// <summary>
    /// Gets or sets the shell.
    /// </summary>
    public string? Shell { get; init; }
    /// <summary>
    /// Gets or sets the channel.
    /// </summary>
    public string? Channel { get; init; }
    /// <summary>
    /// Gets or sets the connecting client kind for transports that can distinguish device
    /// classes (e.g. SignalR "mobile" vs "desktop"). Surfaced on the runtime line only when
    /// a non-default kind is present so the agent can adapt its behaviour (#1209). Null or a
    /// default kind ("desktop"/"unknown") is omitted to keep the no-hint case unchanged.
    /// </summary>
    public string? ClientKind { get; init; }
    /// <summary>
    /// Gets or sets the capabilities.
    /// </summary>
    public IReadOnlyList<string>? Capabilities { get; init; }
    /// <summary>
    /// Gets the agent's own session id for the active run, so the agent can reference
    /// and target itself at runtime (for example for self-diagnosis or session-scoped tooling).
    /// </summary>
    public string? SessionId { get; init; }
    /// <summary>
    /// Gets the agent's own session key (channel-scoped routing identifier) for the active run.
    /// Emitted separately from <see cref="SessionId"/> because the two identify the session at
    /// different layers (persistence id vs channel routing key).
    /// </summary>
    public string? SessionKey { get; init; }
}