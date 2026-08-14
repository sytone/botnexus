namespace BotNexus.Agent.Providers.Core.Embeddings;

/// <summary>
/// One embedding model an <see cref="IEmbeddingProvider"/> can serve.
/// </summary>
/// <remarks>
/// Width is declared rather than discovered because the memory seam stamps every stored vector
/// with the dimension count it expects and discards a vector that does not match
/// (<c>MemoryEmbeddingService.TryGenerateAsync</c>). A provider that could not state its width
/// up front would force the platform to learn it from the first successful response - which is
/// exactly the moment the check is supposed to protect.
/// </remarks>
/// <param name="ModelId">Model identifier as the endpoint expects it, e.g. <c>text-embedding-3-small</c>.</param>
/// <param name="Dimensions">Number of components in every vector this model emits.</param>
public sealed record EmbeddingModelDescriptor(string ModelId, int Dimensions);

/// <summary>
/// OPTIONAL provider capability (#2855): the ability to turn text into a vector.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately separate from <see cref="Registry.IApiProvider"/> rather than a member on it.
/// Embeddings and chat completion are different endpoints with different model catalogues, and
/// most providers in this tree serve only the latter. Bolting <c>EmbedAsync</c> onto
/// <c>IApiProvider</c> would oblige every existing provider to implement - or throw from - a
/// method it has no endpoint for, which is the failure mode acceptance criterion 7 exists to
/// prevent. A provider opts in by additionally implementing this interface; one that does not
/// is resolved as ABSENT, never as an error.
/// </para>
/// <para>
/// The contract is intentionally one-text-in / one-vector-out. Batching is a transport
/// optimisation that would leak partial-failure semantics into the interface, and the memory
/// seam embeds one entry at a time.
/// </para>
/// </remarks>
public interface IEmbeddingProvider
{
    /// <summary>
    /// Provider key this capability is registered under, e.g. <c>ollama</c>, <c>openai</c>.
    /// Matches the key used in the <c>providers</c> configuration section.
    /// </summary>
    string ProviderKey { get; }

    /// <summary>Embedding models this provider can serve. May be empty when none are configured.</summary>
    IReadOnlyList<EmbeddingModelDescriptor> Models { get; }

    /// <summary>
    /// Produces a vector for <paramref name="text"/>, or <see langword="null"/> when the endpoint
    /// declined to produce one.
    /// </summary>
    /// <remarks>
    /// Implementations MAY throw for transport faults; the memory seam catches and degrades to
    /// lexical-only retrieval. Returning <see langword="null"/> is reserved for a well-formed
    /// response that simply carried no vector, so a caller can distinguish "endpoint is broken"
    /// from "endpoint had nothing to say" without parsing an exception message.
    /// </remarks>
    Task<float[]?> EmbedAsync(string modelId, string text, CancellationToken ct = default);
}
