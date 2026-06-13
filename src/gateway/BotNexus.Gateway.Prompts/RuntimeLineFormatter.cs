namespace BotNexus.Gateway.Prompts;

/// <summary>
/// Represents runtime line formatter.
/// </summary>
public static class RuntimeLineFormatter
{
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
            !string.IsNullOrWhiteSpace(runtime?.Channel)
                ? $"capabilities={(normalizedCapabilities.Count > 0 ? string.Join(",", normalizedCapabilities) : "none")}" : string.Empty
        };

        return $"Runtime: {string.Join(" | ", parts.Where(static part => !string.IsNullOrWhiteSpace(part)))}";
    }
}