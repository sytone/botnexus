namespace BotNexus.Cli.Services;

/// <summary>
/// Configuration options for starting a gateway process.
/// </summary>
/// <param name="ExecutablePath">Path to the dotnet executable or BotNexus.Gateway.Api.dll.</param>
/// <param name="Arguments">Optional command-line arguments to pass to the gateway process.</param>
/// <param name="Attached">If true, run in foreground for debugging; if false (default), run detached in a new console window.</param>
/// <param name="HomePath">BotNexus home directory where the PID file is written.</param>
/// <param name="HealthUrl">Effective gateway health endpoint. Callers should derive this from the requested listen URL.</param>
/// <param name="ReadinessTimeout">Maximum cold-start readiness interval; defaults to 60 seconds.</param>
public record GatewayStartOptions(
    string ExecutablePath,
    string? Arguments = null,
    bool Attached = false,
    string? HomePath = null,
    string? HealthUrl = null,
    TimeSpan? ReadinessTimeout = null
);

/// <summary>
/// Result of a gateway start operation.
/// </summary>
/// <param name="Success">True if the gateway was started successfully.</param>
/// <param name="Pid">Process ID of the started gateway, if successful.</param>
/// <param name="Message">Diagnostic message describing the result.</param>
public record GatewayStartResult(
    bool Success,
    int? Pid,
    string? Message
);

/// <summary>
/// Result of a gateway stop operation.
/// </summary>
/// <param name="Success">True if the gateway was stopped successfully.</param>
/// <param name="Message">Diagnostic message describing the result.</param>
public record GatewayStopResult(
    bool Success,
    string? Message
);

/// <summary>
/// Lifecycle state of the gateway process.
/// </summary>
public enum GatewayState
{
    /// <summary>Gateway is not running.</summary>
    NotRunning,
    /// <summary>Gateway is running and responsive.</summary>
    Running,
    /// <summary>Gateway state cannot be determined.</summary>
    Unknown
}

/// <summary>
/// Result of a health probe against a running gateway HTTP endpoint.
/// </summary>
public enum GatewayProbeResult
{
    /// <summary>Probe succeeded: gateway is reachable and authenticated.</summary>
    Healthy,
    /// <summary>Gateway is reachable but returned an auth error (401/403). Token missing or invalid.</summary>
    ReachableNoAuth,
    /// <summary>Gateway process is running but the HTTP endpoint is not reachable (wrong port, not bound yet).</summary>
    Unreachable,
}

/// <summary>
/// Current status of the gateway process.
/// </summary>
/// <param name="State">Current lifecycle state.</param>
/// <param name="Pid">Process ID, if running.</param>
/// <param name="Uptime">How long the gateway has been running, if known.</param>
/// <param name="Message">Diagnostic message with additional context.</param>
/// <param name="ProbeResult">HTTP probe result when process is alive; null when gateway is not running.</param>
public record GatewayStatus(
    GatewayState State,
    int? Pid,
    TimeSpan? Uptime,
    string? Message,
    GatewayProbeResult? ProbeResult = null
);
