using System.Text.Json;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Isolation;

/// <summary>
/// Resolves effective Docker sandbox options for a given agent by merging
/// global <see cref="DockerSandboxOptions"/> defaults with per-agent overrides
/// from <see cref="AgentDescriptor.IsolationOptions"/>.
/// </summary>
/// <remarks>
/// Per-agent overrides are specified in the agent's JSON config under <c>isolationOptions</c>:
/// <code>
/// {
///   "isolation": "docker-sandbox",
///   "isolationOptions": {
///     "image": "custom-image:latest",
///     "networkEnabled": true,
///     "memoryLimit": "1g",
///     "idleTimeout": "00:05:00"
///   }
/// }
/// </code>
/// Any field not specified in <c>isolationOptions</c> falls back to the global default.
/// </remarks>
internal static class DockerSandboxOptionsResolver
{
    /// <summary>
    /// Resolves effective sandbox options for the given agent descriptor.
    /// </summary>
    /// <param name="defaults">Global default options.</param>
    /// <param name="descriptor">Agent descriptor with optional per-agent overrides.</param>
    /// <returns>Effective options for this agent's sandbox.</returns>
    public static ResolvedDockerSandboxOptions Resolve(
        DockerSandboxOptions defaults,
        AgentDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(descriptor);

        var options = descriptor.IsolationOptions;

        return new ResolvedDockerSandboxOptions
        {
            Image = GetStringOption(options, "image") ?? defaults.Image,
            NetworkEnabled = GetBoolOption(options, "networkEnabled") ?? defaults.NetworkEnabled,
            MemoryLimit = GetStringOption(options, "memoryLimit") ?? defaults.MemoryLimit,
            IdleTimeout = GetTimeSpanOption(options, "idleTimeout") ?? defaults.IdleTimeout
        };
    }

    private static string? GetStringOption(IReadOnlyDictionary<string, object?> options, string key)
    {
        if (!options.TryGetValue(key, out var value) || value is null)
            return null;

        if (value is string s)
            return s;

        if (value is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : element.GetRawText().Trim('"');
        }

        return value.ToString();
    }

    private static bool? GetBoolOption(IReadOnlyDictionary<string, object?> options, string key)
    {
        if (!options.TryGetValue(key, out var value) || value is null)
            return null;

        if (value is bool b)
            return b;

        if (value is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.True
                ? true
                : element.ValueKind == JsonValueKind.False
                    ? false
                    : bool.TryParse(element.GetRawText(), out var parsed) ? parsed : null;
        }

        return bool.TryParse(value.ToString(), out var result) ? result : null;
    }

    private static TimeSpan? GetTimeSpanOption(IReadOnlyDictionary<string, object?> options, string key)
    {
        if (!options.TryGetValue(key, out var value) || value is null)
            return null;

        if (value is TimeSpan ts)
            return ts;

        var raw = value is JsonElement element
            ? element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText()
            : value.ToString();

        return TimeSpan.TryParse(raw, out var parsed) ? parsed : null;
    }
}

/// <summary>
/// Fully-resolved Docker sandbox options for a specific agent.
/// All fields are guaranteed non-null (defaults have been applied).
/// </summary>
public sealed class ResolvedDockerSandboxOptions
{
    /// <summary>Docker image for the sandbox container.</summary>
    public required string Image { get; init; }

    /// <summary>Whether the sandbox has network access.</summary>
    public required bool NetworkEnabled { get; init; }

    /// <summary>Memory limit (e.g. "512m", "1g"). Null = no limit.</summary>
    public string? MemoryLimit { get; init; }

    /// <summary>Idle timeout before the sandbox is stopped.</summary>
    public required TimeSpan IdleTimeout { get; init; }
}
