using BotNexus.Agent.Providers.Core.Embeddings;
using Microsoft.Extensions.AI;

namespace BotNexus.Agent.Providers.Core.Tests.Embeddings;

/// <summary>
/// Adapter tests for the bridge between <see cref="IEmbeddingProvider"/> and the
/// <c>Microsoft.Extensions.AI</c> seam <c>BotNexus.Memory</c> consumes (#2855, criterion 2).
/// </summary>
public sealed class EmbeddingProviderGeneratorTests
{
    private sealed class RecordingProvider(Func<string, float[]?> vectorFor, Exception? fault = null) : IEmbeddingProvider
    {
        public string ProviderKey => "fake";
        public IReadOnlyList<EmbeddingModelDescriptor> Models { get; } = [new("model-a", 3)];
        public List<(string ModelId, string Text)> Calls { get; } = [];

        public Task<float[]?> EmbedAsync(string modelId, string text, CancellationToken ct = default)
        {
            Calls.Add((modelId, text));
            if (fault is not null)
                throw fault;

            return Task.FromResult(vectorFor(text));
        }
    }

    [Fact]
    public async Task GenerateAsync_ReturnsOneEmbeddingPerInput()
    {
        var provider = new RecordingProvider(text => text == "a" ? [1f, 2f, 3f] : [4f, 5f, 6f]);
        var generator = new EmbeddingProviderGenerator(provider, "model-a");

        var results = await generator.GenerateAsync(["a", "b"]);

        Assert.Equal(2, results.Count);
        Assert.Equal([1f, 2f, 3f], results[0].Vector.ToArray());
        Assert.Equal([4f, 5f, 6f], results[1].Vector.ToArray());
    }

    [Fact]
    public async Task GenerateAsync_RequestsTheConfiguredModel()
    {
        var provider = new RecordingProvider(_ => [1f, 2f, 3f]);

        await new EmbeddingProviderGenerator(provider, "model-a").GenerateAsync(["hello"]);

        Assert.Equal(("model-a", "hello"), Assert.Single(provider.Calls));
    }

    [Fact]
    public async Task GenerateAsync_SkipsEntriesTheProviderDeclinedToEmbed()
    {
        // A zero vector would have the right WIDTH and so would sail past the memory seam's
        // dimension check and be stored as if it were real. Skipping lets the seam see an empty
        // result and fall back to lexical-only.
        var provider = new RecordingProvider(_ => null);

        var results = await new EmbeddingProviderGenerator(provider, "model-a").GenerateAsync(["hello"]);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GenerateAsync_PropagatesProviderFaults()
    {
        // Deliberately NOT swallowed here: MemoryEmbeddingService owns the degrade-to-lexical
        // policy, and duplicating it would give a memory write two different failure behaviours.
        var provider = new RecordingProvider(_ => [1f], new HttpRequestException("endpoint down"));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => new EmbeddingProviderGenerator(provider, "model-a").GenerateAsync(["hello"]));
    }

    [Fact]
    public void Generator_IsTheEmbeddingGeneratorTheMemorySeamConsumes()
    {
        var generator = new EmbeddingProviderGenerator(new RecordingProvider(_ => [1f, 2f, 3f]), "model-a");

        Assert.IsAssignableFrom<IEmbeddingGenerator<string, Embedding<float>>>(generator);
    }

    [Fact]
    public void GetService_ResolvesItselfAndTheUnderlyingProvider()
    {
        var provider = new RecordingProvider(_ => [1f, 2f, 3f]);
        var generator = new EmbeddingProviderGenerator(provider, "model-a");

        Assert.Same(generator, generator.GetService(typeof(EmbeddingProviderGenerator)));
        Assert.Same(provider, generator.GetService(typeof(IEmbeddingProvider)));
        Assert.Null(generator.GetService(typeof(EmbeddingProviderGenerator), serviceKey: "keyed"));
        Assert.Null(generator.GetService(typeof(string)));
    }

    [Fact]
    public void Constructor_RejectsMissingProviderOrModel()
    {
        Assert.Throws<ArgumentNullException>(() => new EmbeddingProviderGenerator(null!, "model-a"));
        Assert.Throws<ArgumentException>(
            () => new EmbeddingProviderGenerator(new RecordingProvider(_ => null), "  "));
    }
}
