using BotNexus.Agent.Providers.Core.Embeddings;

namespace BotNexus.Agent.Providers.Core.Tests.Embeddings;

/// <summary>
/// Capability-resolution tests for the optional <see cref="IEmbeddingProvider"/> seam (#2855).
/// </summary>
/// <remarks>
/// The behaviour under test is acceptance criteria 1 and 7: a provider that does not implement the
/// interface must resolve as ABSENT and must never make registration throw. That is what allows
/// composition to hand the registry the whole provider set without first classifying it.
/// </remarks>
public sealed class EmbeddingProviderRegistryTests
{
    private sealed class FakeEmbeddingProvider(string key) : IEmbeddingProvider
    {
        public string ProviderKey => key;
        public IReadOnlyList<EmbeddingModelDescriptor> Models { get; } = [new("model-a", 4)];
        public Task<float[]?> EmbedAsync(string modelId, string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>([1f, 2f, 3f, 4f]);
    }

    /// <summary>Stands in for every existing chat-only provider in the tree.</summary>
    private sealed class ChatOnlyProvider
    {
    }

    [Fact]
    public void Get_ReturnsRegisteredProvider()
    {
        var registry = new EmbeddingProviderRegistry();
        registry.Register(new FakeEmbeddingProvider("ollama"));

        Assert.NotNull(registry.Get("ollama"));
        Assert.Equal("ollama", registry.Get("ollama")!.ProviderKey);
    }

    [Fact]
    public void Get_IsCaseInsensitive()
    {
        var registry = new EmbeddingProviderRegistry();
        registry.Register(new FakeEmbeddingProvider("OpenAI"));

        Assert.NotNull(registry.Get("openai"));
    }

    // -- AC1: absent, not thrown --

    [Fact]
    public void Get_ReturnsNull_ForUnknownKey()
    {
        Assert.Null(new EmbeddingProviderRegistry().Get("nope"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Get_ReturnsNull_ForBlankKey(string? key)
    {
        var registry = new EmbeddingProviderRegistry();
        registry.Register(new FakeEmbeddingProvider("ollama"));

        Assert.Null(registry.Get(key));
    }

    // -- AC7: a provider without the capability keeps working and is simply not here --

    [Fact]
    public void TryRegister_DeclinesAProviderThatDoesNotImplementTheCapability()
    {
        var registry = new EmbeddingProviderRegistry();

        Assert.False(registry.TryRegister(new ChatOnlyProvider()));
        Assert.False(registry.TryRegister(null));
        Assert.Empty(registry.Keys);
    }

    [Fact]
    public void TryRegister_AcceptsAProviderThatDoesImplementTheCapability()
    {
        var registry = new EmbeddingProviderRegistry();

        Assert.True(registry.TryRegister(new FakeEmbeddingProvider("ollama")));
        Assert.Equal(["ollama"], registry.Keys);
    }

    [Fact]
    public void Register_IsLastWinsOnTheSameKey()
    {
        var registry = new EmbeddingProviderRegistry();
        var second = new FakeEmbeddingProvider("ollama");

        registry.Register(new FakeEmbeddingProvider("ollama"));
        registry.Register(second);

        Assert.Same(second, registry.Get("ollama"));
        Assert.Single(registry.Keys);
    }

    [Fact]
    public void Register_RejectsNullOrKeylessProvider()
    {
        var registry = new EmbeddingProviderRegistry();

        Assert.Throws<ArgumentNullException>(() => registry.Register(null!));
        Assert.Throws<ArgumentException>(() => registry.Register(new FakeEmbeddingProvider("  ")));
    }
}
