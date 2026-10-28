using System.Text.Json;
using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Contracts.Tools;

/// <summary>Opt-in notification that durable compaction replaced model-visible context.</summary>
public interface IContextReplacementAwareGatewayTool
{
    /// <summary>Invalidates cached assumptions about prior model-visible tool results.</summary>
    void OnContextReplaced();
}

/// <summary>Marks independently answerable/cancellable, durably persisted interactive waits.</summary>
/// <remarks>
/// When the declared timeout key and alias are both absent, no caller expiry is requested and
/// executors must not apply their generic deadline. Explicit expiry restores ordinary budgeting.
/// This marker does not itself implement persistence, cancellation, or timeout enforcement.
/// </remarks>
public interface IDurableInteractiveWaitGatewayTool : IGatewayTool;

/// <summary>Opt-in admitted invocation context; identity comes from the runtime, never model arguments.</summary>
public interface IContextAwareGatewayTool : IGatewayTool
{
    /// <summary>Executes with authoritative context; a null run identity denotes a legacy caller.</summary>
    Task<GatewayToolResult> ExecuteAsync(GatewayToolExecutionContext context,
        CancellationToken cancellationToken = default, GatewayToolUpdateCallback? onUpdate = null);
}

/// <summary>Original model-requested call, kept separate from prepared/enriched arguments.</summary>
/// <param name="Id">Original tool-call correlation identifier.</param>
/// <param name="Name">Requested routing name.</param>
/// <param name="Arguments">Original JSON arguments; preserve key spelling, nested values, and nulls.</param>
/// <param name="ThoughtSignature">Opaque provider wire signature; preserve verbatim without interpreting it.</param>
public sealed record GatewayToolCall(string Id, string Name,
    IReadOnlyDictionary<string, JsonElement> Arguments, string? ThoughtSignature = null);

/// <summary>Invocation-time prompt, timeline, and available tool snapshot.</summary>
/// <param name="SystemPrompt">Active system prompt; null means absent.</param>
/// <param name="Messages">Ordered JSON message snapshots, preserving each message's original wire shape.</param>
/// <param name="Tools">Available gateway tools at snapshot time, not just their schema definitions.</param>
/// <remarks>
/// JSON snapshots replace agent/provider message types, not their data: future adapters must use
/// the originating serializer's discriminators, property naming, omission rules, content blocks,
/// signatures, usage, timestamps, errors, image values, and extension metadata without projection
/// to text. Clone elements before source documents are disposed. Runtime object identity and
/// executable delegates are not JSON metadata and must not be smuggled into these snapshots.
/// </remarks>
public sealed record GatewayToolContext(string? SystemPrompt,
    IReadOnlyList<JsonElement> Messages, IReadOnlyList<IGatewayTool> Tools);

/// <summary>Admitted execution snapshot, separating original call arguments from validated arguments.</summary>
/// <param name="AssistantMessage">Complete requesting assistant message as the originating JSON snapshot.</param>
/// <param name="ToolCallRequest">Original requested call including any thought signature.</param>
/// <param name="ValidatedArgs">Prepared/enriched JSON arguments, not a replacement for the raw request.</param>
/// <param name="AgentContext">Invocation-time model context snapshot.</param>
/// <remarks>AssistantMessage follows the same lossless wire-shape and document-lifetime rules as GatewayToolContext.Messages.</remarks>
public sealed record GatewayToolExecutionContext(JsonElement AssistantMessage,
    GatewayToolCall ToolCallRequest, IReadOnlyDictionary<string, JsonElement> ValidatedArgs,
    GatewayToolContext AgentContext)
{
    /// <summary>Authoritative admitted run identity; null for legacy callers, never inferred from model input.</summary>
    public AgentRunId? AgentRunId { get; init; }
}
