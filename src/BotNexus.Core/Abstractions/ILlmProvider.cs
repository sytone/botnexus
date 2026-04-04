using BotNexus.Core.Models;

namespace BotNexus.Core.Abstractions;

/// <summary>
/// Contract for LLM providers.
/// 
/// <para><b>Provider Normalization Contract:</b></para>
/// <para>
/// All implementations MUST normalize their provider-specific API responses into the canonical 
/// <see cref="LlmResponse"/> format. The agent loop and other consumers work exclusively with 
/// this normalized type and should never see raw API responses or provider-specific quirks.
/// </para>
/// 
/// <para><b>Normalization Responsibilities:</b></para>
/// <list type="bullet">
///   <item>Parse raw API responses (JSON, SDK objects, etc.) into LlmResponse</item>
///   <item>Handle multi-choice/multi-block responses (merge if needed)</item>
///   <item>Normalize tool call argument formats (JSON string vs JSON object)</item>
///   <item>Map provider-specific finish reasons to FinishReason enum</item>
///   <item>Normalize token count field names to InputTokens/OutputTokens</item>
///   <item>Handle missing/null fields gracefully (use empty strings, null, etc.)</item>
/// </list>
/// 
/// <para>
/// See <see cref="LlmResponse"/> documentation for the canonical response model specification.
/// </para>
/// </summary>
public interface ILlmProvider
{
    /// <summary>The default model name for this provider.</summary>
    string DefaultModel { get; }

    /// <summary>Generation settings.</summary>
    GenerationSettings Generation { get; set; }

    /// <summary>Gets the list of available models for this provider.</summary>
    Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a chat request and returns the complete normalized response.
    /// 
    /// <para>
    /// Implementations MUST normalize their raw API response into the canonical <see cref="LlmResponse"/> 
    /// format, handling all provider-specific quirks internally. The caller should never need to know 
    /// which provider is being used.
    /// </para>
    /// </summary>
    /// <returns>Normalized response in canonical format.</returns>
    Task<LlmResponse> ChatAsync(ChatRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a chat request and streams response chunks.
    /// 
    /// <para>
    /// Streaming mode yields <see cref="StreamingChatChunk"/> objects that may contain text deltas,
    /// tool call deltas, finish reasons, or usage statistics. Consumers should aggregate chunks
    /// to build the complete response.
    /// </para>
    /// 
    /// <para><b>Tool Call Streaming:</b></para>
    /// <para>
    /// When the model requests tool calls, the provider yields chunks with ToolCallId, ToolName,
    /// and ArgumentsDelta. Arguments stream as partial JSON fragments that must be buffered and
    /// parsed when complete. Providers that don't support tool call streaming can still use
    /// ChatAsync for non-streaming tool calls.
    /// </para>
    /// 
    /// <para><b>Backward Compatibility:</b></para>
    /// <para>
    /// Consumers that only care about text streaming can read ContentDelta and ignore other fields.
    /// This maintains compatibility with simple streaming use cases.
    /// </para>
    /// </summary>
    IAsyncEnumerable<StreamingChatChunk> ChatStreamAsync(ChatRequest request, CancellationToken cancellationToken = default);
}
