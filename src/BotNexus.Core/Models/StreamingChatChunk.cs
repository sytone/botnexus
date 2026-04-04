namespace BotNexus.Core.Models;

/// <summary>
/// Represents a chunk of data from a streaming LLM response.
/// Streaming responses may contain text deltas, tool call deltas, finish reasons, or usage statistics.
/// 
/// <para><b>Streaming Protocol:</b></para>
/// <para>
/// Most LLM providers use Server-Sent Events (SSE) for streaming. The provider parses SSE events
/// into these normalized chunks. Each chunk represents one "delta" or update in the stream.
/// </para>
/// 
/// <para><b>Chunk Types:</b></para>
/// <list type="bullet">
///   <item><b>Content Delta:</b> ContentDelta is non-null. Append to accumulated content.</item>
///   <item><b>Tool Call Start:</b> ToolCallId and ToolName are set, Arguments may be partial or empty.</item>
///   <item><b>Tool Call Delta:</b> ToolCallId is set, ArgumentsDelta contains partial JSON to append.</item>
///   <item><b>Finish:</b> FinishReason is set, marks end of generation.</item>
///   <item><b>Usage:</b> InputTokens/OutputTokens are set, typically sent at the end.</item>
/// </list>
/// 
/// <para><b>Consumer Pattern:</b></para>
/// <para>
/// Consumers should aggregate chunks into a complete response:
/// - Accumulate ContentDelta into a content string
/// - Track tool calls by ID, append ArgumentsDelta, parse JSON when complete
/// - Capture FinishReason when present
/// - Capture usage statistics when present
/// </para>
/// 
/// <para>
/// For simple text-only streaming (no tools), consumers can ignore all fields except ContentDelta.
/// This maintains backward compatibility with existing streaming consumers.
/// </para>
/// </summary>
public record StreamingChatChunk
{
    /// <summary>
    /// Text content delta to append to the accumulated response content.
    /// Null if this chunk is not a content delta.
    /// </summary>
    public string? ContentDelta { get; init; }
    
    /// <summary>
    /// Tool call ID. Set when a tool call starts or when streaming tool call arguments.
    /// Use this to track which tool call the ArgumentsDelta belongs to.
    /// </summary>
    public string? ToolCallId { get; init; }
    
    /// <summary>
    /// Tool name. Set when a tool call starts. Null for subsequent argument deltas.
    /// </summary>
    public string? ToolName { get; init; }
    
    /// <summary>
    /// Partial JSON fragment for tool call arguments. Append to accumulate full JSON.
    /// Example: chunk 1 might have `{"ac`, chunk 2 might have `tion":"`, chunk 3 might have `list"}`.
    /// Consumer must buffer and parse when complete (e.g., when FinishReason is received or next tool starts).
    /// </summary>
    public string? ArgumentsDelta { get; init; }
    
    /// <summary>
    /// Finish reason, typically sent in the final chunk. Indicates why generation stopped.
    /// </summary>
    public FinishReason? FinishReason { get; init; }
    
    /// <summary>
    /// Input tokens consumed (prompt tokens). Usually sent at the end of the stream.
    /// </summary>
    public int? InputTokens { get; init; }
    
    /// <summary>
    /// Output tokens generated (completion tokens). Usually sent at the end of the stream.
    /// </summary>
    public int? OutputTokens { get; init; }
    
    /// <summary>
    /// Creates a content delta chunk (text streaming).
    /// </summary>
    public static StreamingChatChunk FromContentDelta(string content) => new() { ContentDelta = content };
    
    /// <summary>
    /// Creates a tool call start chunk.
    /// </summary>
    public static StreamingChatChunk FromToolCallStart(string toolCallId, string toolName) => new()
    {
        ToolCallId = toolCallId,
        ToolName = toolName
    };
    
    /// <summary>
    /// Creates a tool call arguments delta chunk.
    /// </summary>
    public static StreamingChatChunk FromToolCallDelta(string toolCallId, string argumentsDelta) => new()
    {
        ToolCallId = toolCallId,
        ArgumentsDelta = argumentsDelta
    };
    
    /// <summary>
    /// Creates a finish chunk.
    /// </summary>
    public static StreamingChatChunk FromFinishReason(FinishReason reason) => new() { FinishReason = reason };
    
    /// <summary>
    /// Creates a usage chunk.
    /// </summary>
    public static StreamingChatChunk FromUsage(int inputTokens, int outputTokens) => new()
    {
        InputTokens = inputTokens,
        OutputTokens = outputTokens
    };
}
