using BotNexus.Agent.Core.Types;

namespace BotNexus.Agent.Core.Tools;

/// <summary>
/// Classifies how a tool participates in a satellite execution scope.
/// </summary>
public enum SatelliteToolClass
{
    /// <summary>The tool executes on the primary gateway control plane.</summary>
    LocalOnly,

    /// <summary>The tool must execute through the selected satellite executor.</summary>
    RemoteCapable,

    /// <summary>The tool cannot be used while satellite execution is required.</summary>
    Unsupported
}

/// <summary>
/// Stable placement identity shared by every remote-capable tool call in one execution scope.
/// </summary>
public sealed record SatelliteExecutionScope(
    string SatelliteId,
    string WorkspaceId,
    string RunId,
    string AttemptId,
    long FencingGeneration,
    string CallerIdentity,
    string WorkingDirectory);

/// <summary>
/// Host-selected satellite routing and policy context for an agent run.
/// </summary>
public sealed record SatelliteToolExecutionOptions(
    SatelliteExecutionScope Scope,
    ISatelliteToolExecutor Executor,
    Func<string, SatelliteToolClass> ClassifyTool,
    IReadOnlyDictionary<string, string> Environment,
    string PolicyProvenance = "host-authorized");

/// <summary>
/// Versioned request sent to the selected satellite executor.
/// </summary>
public sealed record SatelliteToolRequest(
    int ProtocolVersion,
    SatelliteExecutionScope Scope,
    string ToolCallId,
    string ToolName,
    IReadOnlyDictionary<string, object?> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string PolicyProvenance,
    TimeSpan? Timeout);

/// <summary>
/// Explicit terminal outcome returned by a satellite executor.
/// </summary>
public enum SatelliteToolOutcome
{
    Completed,
    Failed,
    Cancelled,
    TimedOut,
    Disconnected,
    Unavailable,
    OutcomeUncertain
}

/// <summary>
/// Reference to a bounded artifact retained by the satellite instead of copied into the tool result.
/// </summary>
public sealed record SatelliteArtifactReference(
    string ArtifactId,
    string MediaType,
    long Length,
    string Sha256);

/// <summary>
/// Bounded delivery facts supplied by the satellite executor.
/// </summary>
public sealed record SatelliteToolResultMetadata(
    bool IsTruncated,
    bool IsIncomplete,
    IReadOnlyList<SatelliteArtifactReference> Artifacts)
{
    public static SatelliteToolResultMetadata Complete { get; } = new(false, false, []);
}

/// <summary>
/// Satellite delivery facts retained alongside tool-specific result details.
/// </summary>
public sealed record SatelliteToolResultDetails(
    SatelliteToolOutcome Outcome,
    bool IsTruncated,
    bool IsIncomplete,
    IReadOnlyList<SatelliteArtifactReference> Artifacts,
    object? ToolDetails);

/// <summary>
/// Typed remote tool result compatible with the local normalized tool-result contract.
/// </summary>
public sealed record SatelliteToolResult(
    SatelliteToolOutcome Outcome,
    AgentToolResult Result,
    bool IsError,
    SatelliteToolResultMetadata? Metadata = null)
{
    public static SatelliteToolResult Completed(AgentToolResult result) =>
        new(SatelliteToolOutcome.Completed, result, false, SatelliteToolResultMetadata.Complete);

    public static SatelliteToolResult Unavailable(string message) =>
        Error(SatelliteToolOutcome.Unavailable, message);

    public static SatelliteToolResult Error(SatelliteToolOutcome outcome, string message) =>
        new(
            outcome,
            new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, message)]),
            true,
            new SatelliteToolResultMetadata(false, true, []));
}

/// <summary>
/// Executes remote-capable tools inside one selected satellite context.
/// Implementations enforce authorization again at the satellite boundary.
/// </summary>
public interface ISatelliteToolExecutor
{
    Task<SatelliteToolResult> ExecuteAsync(
        SatelliteToolRequest request,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null);
}
