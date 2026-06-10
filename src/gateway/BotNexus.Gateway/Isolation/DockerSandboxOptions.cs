namespace BotNexus.Gateway.Isolation;

/// <summary>
/// Configuration options for the Docker sandbox isolation strategy.
/// These serve as global defaults; per-agent overrides are specified
/// in <c>AgentDescriptor.IsolationOptions</c>.
/// </summary>
public sealed class DockerSandboxOptions
{
    /// <summary>
    /// How long a sandbox can remain idle (no dispatches) before being stopped.
    /// Default: 10 minutes.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Docker image to use for sandbox containers.
    /// Default: "mcr.microsoft.com/dotnet/runtime:9.0" — lightweight .NET runtime base.
    /// </summary>
    public string Image { get; set; } = "mcr.microsoft.com/dotnet/runtime:9.0";

    /// <summary>
    /// Whether sandbox containers have network access.
    /// Default: false (sandboxed agents are network-isolated for security).
    /// </summary>
    public bool NetworkEnabled { get; set; }

    /// <summary>
    /// Maximum memory limit for the sandbox container (e.g. "512m", "1g").
    /// Null means no explicit limit (uses Docker daemon defaults).
    /// </summary>
    public string? MemoryLimit { get; set; }
}
