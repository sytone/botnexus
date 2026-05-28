using System.Text.Json.Serialization;

namespace BotNexus.Agent.Providers.IntegrationMock;

/// <summary>
/// A single step in a scripted response. One step maps to either a single streaming event
/// (text/thinking delta, end) or a higher-level action such as a tool call, completion,
/// or simulated error. Steps execute in order with an optional <see cref="DelayMs"/> applied
/// before the step is pushed onto the stream.
/// </summary>
/// <param name="Type">
/// Event kind. One of: <c>text_delta</c>, <c>text_end</c>, <c>thinking_delta</c>,
/// <c>thinking_end</c>, <c>tool_call</c>, <c>done</c>, <c>error</c>.
/// </param>
/// <param name="Delta">Text or thinking delta to push (for <c>text_delta</c>/<c>thinking_delta</c>).</param>
/// <param name="ToolName">Function name (for <c>tool_call</c>).</param>
/// <param name="ToolArguments">Argument map (for <c>tool_call</c>).</param>
/// <param name="ToolCallId">Optional stable identifier (for <c>tool_call</c>). Generated when omitted.</param>
/// <param name="StopReason">
/// Stop reason for <c>done</c> / <c>error</c>. Defaults to <c>stop</c> for <c>done</c> and
/// <c>error</c> for <c>error</c>. Tool-call scripts should use <c>toolUse</c>.
/// </param>
/// <param name="ErrorMessage">Error text for <c>error</c>.</param>
/// <param name="DelayMs">
/// Milliseconds to wait BEFORE pushing this step. Lets scripts simulate token cadence and
/// inter-event pauses for realistic streaming and concurrency testing.
/// </param>
public sealed record ScriptedResponseStep(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("delta")] string? Delta = null,
    [property: JsonPropertyName("toolName")] string? ToolName = null,
    [property: JsonPropertyName("toolArguments")] Dictionary<string, object?>? ToolArguments = null,
    [property: JsonPropertyName("toolCallId")] string? ToolCallId = null,
    [property: JsonPropertyName("stopReason")] string? StopReason = null,
    [property: JsonPropertyName("errorMessage")] string? ErrorMessage = null,
    [property: JsonPropertyName("delayMs")] int? DelayMs = null);

/// <summary>
/// The on-disk and in-memory catalog of mock responses. Keyed by the trimmed text of the
/// final user message (case-sensitive).
/// </summary>
public sealed record MockCatalog(
    [property: JsonPropertyName("scripts")] Dictionary<string, IReadOnlyList<ScriptedResponseStep>> Scripts);
