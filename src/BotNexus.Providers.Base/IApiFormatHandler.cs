using BotNexus.Core.Models;

namespace BotNexus.Providers.Base;

/// <summary>
/// Handles communication with a specific API format (e.g., Anthropic Messages, OpenAI Completions, OpenAI Responses).
/// Each handler implements the same interface but speaks a different API dialect.
/// </summary>
public interface IApiFormatHandler
{
    /// <summary>The API format this handler supports (e.g., "anthropic-messages", "openai-completions", "openai-responses").</summary>
    string ApiFormat { get; }
    
    /// <summary>
    /// Sends a non-streaming chat request using this API format.
    /// </summary>
    /// <param name="model">Model definition with API details</param>
    /// <param name="request">Normalized chat request</param>
    /// <param name="apiKey">API key or access token</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Normalized LLM response</returns>
    Task<LlmResponse> ChatAsync(
        ModelDefinition model, 
        ChatRequest request, 
        string apiKey, 
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Sends a streaming chat request using this API format.
    /// </summary>
    /// <param name="model">Model definition with API details</param>
    /// <param name="request">Normalized chat request</param>
    /// <param name="apiKey">API key or access token</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Stream of chat chunks</returns>
    IAsyncEnumerable<StreamingChatChunk> ChatStreamAsync(
        ModelDefinition model, 
        ChatRequest request, 
        string apiKey, 
        CancellationToken cancellationToken);
}
