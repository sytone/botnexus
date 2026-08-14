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
/// #2790 added the explicit <see cref="Backend"/> discriminator. The original <see cref="Enabled"/>
/// toggle remains the compatibility path: an operator who configured embeddings before the
/// discriminator existed keeps the behaviour they had, because an unspecified backend resolves
/// from <see cref="Enabled"/>. Setting <c>backend</c> explicitly always wins, which is what makes
/// the shipped default overridable by a single key.
/// </para>
/// <para>
/// <see cref="MemoryEmbeddingBackend.Local"/> is selectable and documented but not satisfied by any
/// runtime in this build: the ONNX Runtime native dependency is deliberately not vendored, so an
/// operator selecting <c>none</c> or <c>provider</c> never ships a native binary. Selecting
/// <c>local</c> today degrades to lexical-only with a warning.
/// </para>
/// </remarks>
public sealed class MemoryEmbeddingsConfig
{
    /// <summary>
    /// Backend selection: <c>none</c>, <c>local</c>, or <c>provider</c>. Absent means "unspecified"
    /// and falls back to <see cref="Enabled"/> for compatibility with pre-#2790 configurations.
    /// </summary>
    [Display(
        Name = "Backend",
        Description = "Embedding backend: 'none' for lexical-only retrieval, 'local' for on-box inference, or 'provider' to reuse a configured provider's embeddings endpoint. Defaults to 'none'.",
        GroupName = "Memory embeddings",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "memory-embeddings", Order = 0)]
    public string? Backend { get; set; }

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
    /// Resolves the selected backend (#2790). An explicit <see cref="Backend"/> value always wins;
    /// an unrecognised token is reported through <paramref name="unrecognized"/> and resolves to
    /// <see cref="MemoryEmbeddingBackend.None"/> so a typo degrades retrieval rather than
    /// preventing startup. When unspecified, the legacy <see cref="Enabled"/> toggle decides.
    /// </summary>
    public MemoryEmbeddingBackend ResolveBackend(out string? unrecognized)
    {
        unrecognized = null;

        if (!string.IsNullOrWhiteSpace(Backend))
        {
            if (MemoryEmbeddingBackendParser.TryParse(Backend, out var parsed))
                return parsed;

            unrecognized = Backend.Trim();
            return MemoryEmbeddingBackend.None;
        }

        // Pre-#2790 shape: 'enabled: true' meant the one backend that existed, the hosted provider.
        return Enabled ? MemoryEmbeddingBackend.Provider : MemoryEmbeddingBackend.None;
    }

    /// <summary>Resolves the selected backend, discarding the unrecognised-token diagnostic.</summary>
    public MemoryEmbeddingBackend ResolveBackend() => ResolveBackend(out _);

    /// <summary>
    /// Whether this configuration is complete enough to build the PROVIDER backend from.
    /// </summary>
    /// <remarks>
    /// A half-filled section is treated exactly like an absent one - lexical-only - rather than
    /// throwing at startup. An operator mid-way through configuring embeddings should get a
    /// working gateway with degraded retrieval, not a gateway that refuses to boot.
    /// </remarks>
    public bool IsComplete()
        => ResolveBackend() == MemoryEmbeddingBackend.Provider
           && !string.IsNullOrWhiteSpace(Provider)
           && !string.IsNullOrWhiteSpace(Model)
           && !string.IsNullOrWhiteSpace(BaseUrl)
           && Dimensions > 0;
}
