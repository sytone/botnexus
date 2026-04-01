using BotNexus.Core.Models;

namespace BotNexus.Core.Abstractions;

/// <summary>Contract for LLM providers.</summary>
public interface ILlmProvider
{
    /// <summary>The default model name for this provider.</summary>
    string DefaultModel { get; }

    /// <summary>Generation settings.</summary>
    GenerationSettings Generation { get; set; }

    /// <summary>Sends a chat request and returns the complete response.</summary>
    Task<LlmResponse> ChatAsync(ChatRequest request, CancellationToken cancellationToken = default);

    /// <summary>Sends a chat request and streams response tokens.</summary>
    IAsyncEnumerable<string> ChatStreamAsync(ChatRequest request, CancellationToken cancellationToken = default);
}
