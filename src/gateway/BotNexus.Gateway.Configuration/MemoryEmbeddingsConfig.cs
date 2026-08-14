using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Selects the embedding backend that satisfies the memory retrieval seam (#2855).
/// </summary>
/// <remarks>
/// <para>
/// Absent or <c>enabled: false</c> is the DEFAULT and preserves the behaviour that shipped with
/// #2356 exactly: nothing registers an <c>IEmbeddingGenerator</c>, the memory store is constructed
/// with <c>MemoryEmbeddingService.Disabled</c>, and retrieval is lexical-only. Turning embeddings
/// on is an explicit operator decision, because it sends memory content to the configured
/// endpoint.
/// </para>
/// <para>
/// There is no local-model option here yet. Local ONNX inference is a later implementation of the
/// same <c>IEmbeddingProvider</c> interface (#2790) and will add a discriminator; declaring one
/// now with a single valid value would be a guess wearing a type.
/// </para>
/// </remarks>
public sealed class MemoryEmbeddingsConfig
{
    /// <summary>Whether memory embeddings are enabled. Off by default.</summary>
    [Display(
        Name = "Enabled",
        Description = "Whether memory entries are embedded for hybrid (lexical + vector) retrieval. Off by default; enabling it sends memory content to the configured embeddings endpoint.",
        GroupName = "Memory embeddings",
        Order = 0)]
    [DefaultValue(false)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "memory-embeddings", Order = 0)]
    public bool Enabled { get; set; }

    /// <summary>Provider key serving the embeddings endpoint, e.g. <c>ollama</c> or <c>openai</c>.</summary>
    [Display(
        Name = "Provider",
        Description = "Provider key whose embeddings endpoint supplies vectors (for example 'ollama' or 'openai').",
        GroupName = "Memory embeddings",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "memory-embeddings", Order = 1)]
    public string? Provider { get; set; }

    /// <summary>Embedding model identifier as the endpoint expects it.</summary>
    [Display(
        Name = "Model",
        Description = "Embedding model identifier as the endpoint expects it (for example 'nomic-embed-text' or 'text-embedding-3-small').",
        GroupName = "Memory embeddings",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "memory-embeddings", Order = 2)]
    public string? Model { get; set; }

    /// <summary>
    /// Vector width the model emits. Declared rather than discovered so a model that returns the
    /// wrong width is caught and discarded on the first write instead of corrupting comparisons.
    /// </summary>
    [Display(
        Name = "Dimensions",
        Description = "Number of components in each vector the model emits. A response of a different width is discarded and the entry falls back to lexical-only.",
        GroupName = "Memory embeddings",
        Order = 3)]
    [Range(1, 65536)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "memory-embeddings", Order = 3)]
    public int Dimensions { get; set; }

    /// <summary>Base URL of the OpenAI-compatible endpoint, e.g. <c>http://localhost:11434/v1</c>.</summary>
    [Display(
        Name = "Base URL",
        Description = "Base URL of the OpenAI-compatible embeddings endpoint (the '/embeddings' path is appended).",
        GroupName = "Memory embeddings",
        Order = 4)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "memory-embeddings", Order = 4)]
    public string? BaseUrl { get; set; }

    /// <summary>Optional bearer token. Omitted for a local endpoint that requires none.</summary>
    [Display(
        Name = "API key",
        Description = "Bearer token for the embeddings endpoint. Optional for a local endpoint. Sensitive: stored and shown masked.",
        GroupName = "Memory embeddings",
        Order = 5)]
    [ConfigField(Widget = ConfigFieldWidget.Secret, Group = "memory-embeddings", Order = 5, Secret = true)]
    public string? ApiKey { get; set; }

    /// <summary>
    /// Whether this configuration is complete enough to build an embedding backend from.
    /// </summary>
    /// <remarks>
    /// A half-filled section is treated exactly like an absent one - lexical-only - rather than
    /// throwing at startup. An operator mid-way through configuring embeddings should get a
    /// working gateway with degraded retrieval, not a gateway that refuses to boot.
    /// </remarks>
    public bool IsComplete()
        => Enabled
           && !string.IsNullOrWhiteSpace(Provider)
           && !string.IsNullOrWhiteSpace(Model)
           && !string.IsNullOrWhiteSpace(BaseUrl)
           && Dimensions > 0;
}
