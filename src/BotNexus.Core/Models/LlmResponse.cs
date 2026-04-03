namespace BotNexus.Core.Models;

/// <summary>
/// The reason the LLM stopped generating.
/// Provider-agnostic enum that all providers normalize their finish reasons to.
/// </summary>
public enum FinishReason 
{ 
    /// <summary>Normal completion.</summary>
    Stop, 
    
    /// <summary>Model requested tool execution.</summary>
    ToolCalls, 
    
    /// <summary>Reached token limit.</summary>
    Length, 
    
    /// <summary>Content filtered by safety systems.</summary>
    ContentFilter, 
    
    /// <summary>Other/unknown reason.</summary>
    Other 
}

/// <summary>
/// Canonical normalized response from any LLM provider.
/// 
/// <para>
/// This is the provider-agnostic response format that all ILlmProvider implementations
/// MUST normalize their raw API responses into. Each provider is responsible for handling
/// its own API quirks, response formats, and edge cases internally, then producing this
/// standardized structure.
/// </para>
/// 
/// <para><b>Provider Normalization Responsibilities:</b></para>
/// <list type="bullet">
///   <item>Parse provider-specific response formats (JSON, SDK objects, etc.)</item>
///   <item>Merge multi-choice responses if applicable (e.g., Copilot proxy splitting content/tool_calls)</item>
///   <item>Normalize tool call argument formats (JSON string vs JSON object)</item>
///   <item>Map provider-specific finish reasons to the FinishReason enum</item>
///   <item>Normalize token count field names (input_tokens, InputTokenCount, prompt_tokens, etc.)</item>
///   <item>Handle missing/null fields gracefully</item>
/// </list>
/// 
/// <para>
/// The agent loop and other consumers work exclusively with this normalized type,
/// never directly with provider-specific response structures. This isolation allows
/// providers to evolve independently without impacting agent logic.
/// </para>
/// </summary>
/// <param name="Content">Text content from the LLM. Empty string if none.</param>
/// <param name="FinishReason">Why the LLM stopped generating (normalized).</param>
/// <param name="ToolCalls">Tool calls requested by the LLM, if any. Null if none.</param>
/// <param name="InputTokens">Tokens consumed in the prompt/input. Null if not provided by the API.</param>
/// <param name="OutputTokens">Tokens generated in the response. Null if not provided by the API.</param>
public record LlmResponse(
    string Content,
    FinishReason FinishReason,
    IReadOnlyList<ToolCallRequest>? ToolCalls = null,
    int? InputTokens = null,
    int? OutputTokens = null);
