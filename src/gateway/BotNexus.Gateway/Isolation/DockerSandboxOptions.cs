namespace BotNexus.Gateway.Isolation;

/// <summary>
/// Configuration options for the Docker sandbox isolation strategy.
/// </summary>
public sealed class DockerSandboxOptions
{
    /// <summary>
    /// How long a sandbox can remain idle (no dispatches) before being stopped.
    /// Default: 10 minutes.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(10);
}
