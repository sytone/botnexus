namespace BotNexus.Cli.Services;

/// <summary>
/// Configuration options for starting a gateway process.
/// </summary>
/// <param name="ExecutablePath">Path to the dotnet executable or BotNexus.Gateway.Api.dll.</param>
/// <param name="Arguments">Optional command-line arguments to pass to the gateway process.</param>
/// <param name="Attached">If true, run in foreground for debugging; if false (default), run detached in a new console window.</param>
public record GatewayStartOptions(
    string ExecutablePath,
    string? Arguments = null,
    bool Attached = false
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
/// Current status of the gateway process.
/// </summary>
/// <param name="State">Current lifecycle state.</param>
/// <param name="Pid">Process ID, if running.</param>
/// <param name="Uptime">How long the gateway has been running, if known.</param>
/// <param name="Message">Diagnostic message with additional context.</param>
public record GatewayStatus(
    GatewayState State,
    int? Pid,
    TimeSpan? Uptime,
    string? Message
);
