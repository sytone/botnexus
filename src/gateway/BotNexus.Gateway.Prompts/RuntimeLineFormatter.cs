namespace BotNexus.Gateway.Prompts;

/// <summary>
/// Represents runtime line formatter.
/// </summary>
public static class RuntimeLineFormatter
{
    /// <summary>
    /// Delimiter line that marks the start of the hidden runtime-context block in the system prompt.
    /// Models see this as a bounded block; an outbound strip routine can target these markers to redact
    /// the runtime context from user-visible replies (see follow-up work for the strip half).
    /// </summary>
    public const string RuntimeContextBeginDelimiter = "INTERNAL_RUNTIME_CONTEXT_BEGIN";

    /// <summary>
    /// Delimiter line that marks the end of the hidden runtime-context block in the system prompt.
    /// Pairs with <see cref="RuntimeContextBeginDelimiter"/> to bracket the runtime-context body.
    /// </summary>
    public const string RuntimeContextEndDelimiter = "INTERNAL_RUNTIME_CONTEXT_END";

    /// <summary>
    /// Executes build runtime line.
    /// </summary>
    /// <param name="runtime">The runtime.</param>
    /// <returns>The build runtime line result.</returns>
    public static string BuildRuntimeLine(PromptRuntimeInfo? runtime)
    {
        var normalizedCapabilities = PromptText.NormalizeCapabilityIds(runtime?.Capabilities ?? []);
        var parts = new List<string>
        {
            !string.IsNullOrWhiteSpace(runtime?.AgentId) ? $"agent={runtime!.AgentId}" : string.Empty,
            !string.IsNullOrWhiteSpace(runtime?.SessionId) ? $"session={runtime!.SessionId}" : string.Empty,
            !string.IsNullOrWhiteSpace(runtime?.SessionKey) ? $"session_key={runtime!.SessionKey}" : string.Empty,
            !string.IsNullOrWhiteSpace(runtime?.Host) ? $"host={runtime!.Host}" : string.Empty,
            !string.IsNullOrWhiteSpace(runtime?.Os)
                ? $"os={runtime!.Os}{(!string.IsNullOrWhiteSpace(runtime.Arch) ? $" ({runtime.Arch})" : string.Empty)}"
                : !string.IsNullOrWhiteSpace(runtime?.Arch)
                    ? $"arch={runtime!.Arch}"
                    : string.Empty,
            !string.IsNullOrWhiteSpace(runtime?.Provider) ? $"provider={runtime!.Provider}" : string.Empty,
            !string.IsNullOrWhiteSpace(runtime?.Model) ? $"model={runtime!.Model}" : string.Empty,
            !string.IsNullOrWhiteSpace(runtime?.DefaultModel) ? $"default_model={runtime!.DefaultModel}" : string.Empty,
            !string.IsNullOrWhiteSpace(runtime?.Shell) ? $"shell={runtime!.Shell}" : string.Empty,
            !string.IsNullOrWhiteSpace(runtime?.Channel) ? $"channel={runtime!.Channel.Trim().ToLowerInvariant()}" : string.Empty,
            ShouldEmitClientKind(runtime?.ClientKind) ? $"client={runtime!.ClientKind!.Trim().ToLowerInvariant()}" : string.Empty,
            !string.IsNullOrWhiteSpace(runtime?.Channel)
                ? $"capabilities={(normalizedCapabilities.Count > 0 ? string.Join(",", normalizedCapabilities) : "none")}" : string.Empty
        };

        return $"Runtime: {string.Join(" | ", parts.Where(static part => !string.IsNullOrWhiteSpace(part)))}";
    }

    /// <summary>
    /// Determines whether the client kind should be surfaced on the runtime line. Only a
    /// present, non-default kind is emitted: the default desktop kind and the "unknown"
    /// fallback (the value an absent connect-time hint resolves to) are suppressed so that
    /// existing desktop sessions render an unchanged runtime line (#1209 AC#5).
    /// </summary>
    /// <param name="clientKind">The raw client kind, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the kind should be emitted; otherwise <see langword="false"/>.</returns>
    private static bool ShouldEmitClientKind(string? clientKind)
    {
        if (string.IsNullOrWhiteSpace(clientKind))
            return false;

        var normalized = clientKind.Trim().ToLowerInvariant();
        return normalized is not ("desktop" or "unknown");
    }
}