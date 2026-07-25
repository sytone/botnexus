using BotNexus.Memory.Embeddings;

namespace BotNexus.Memory.Tests.Embeddings;

/// <summary>
/// Tests the seam that guarantees a broken embedding provider can never fail a memory
/// operation.
/// </summary>
public sealed class MemoryEmbeddingServiceTests
{
    private static readonly EmbeddingIdentity Identity = new("stub-model", "fp-1", 3);

    [Fact]
    public async Task TryGenerateAsync_ReturnsStampedVector()
    {
        var service = new MemoryEmbeddingService(
            new StubEmbeddingGenerator(new Dictionary<string, float[]> { ["hello"] = [1f, 2f, 3f] }, 3),
            Identity);

        var result = await service.TryGenerateAsync("hello");

        Assert.NotNull(result);
        Assert.Equal(Identity, result!.Value.Identity);
        Assert.Equal(new[] { 1f, 2f, 3f }, result.Value.Vector);
    }

    [Fact]
    public void ActiveIdentity_IsNull_WhenNoGeneratorConfigured()
    {
        Assert.Null(MemoryEmbeddingService.Disabled.ActiveIdentity);
    }

    [Fact]
    public async Task TryGenerateAsync_ReturnsNull_WhenNoGeneratorConfigured()
    {
        Assert.Null(await MemoryEmbeddingService.Disabled.TryGenerateAsync("hello"));
    }

    [Fact]
    public async Task TryGenerateAsync_ReturnsNull_WhenGeneratorThrows()
    {
        var service = new MemoryEmbeddingService(
            new StubEmbeddingGenerator(new Dictionary<string, float[]>(), 3, new InvalidOperationException("boom")),
            Identity);

        Assert.Null(await service.TryGenerateAsync("hello"));
    }

    [Fact]
    public async Task TryGenerateAsync_ReturnsNull_WhenModelEmitsWrongDimensionCount()
    {
        // A misconfigured model that returns the wrong width must not have its output stored
        // under the declared identity - that would corrupt every later comparison.
        var service = new MemoryEmbeddingService(
            new StubEmbeddingGenerator(new Dictionary<string, float[]> { ["hello"] = [1f, 2f] }, 2),
            Identity);

        Assert.Null(await service.TryGenerateAsync("hello"));
    }

    [Fact]
    public async Task TryGenerateAsync_ReturnsNull_ForBlankInput()
    {
        var generator = new StubEmbeddingGenerator(new Dictionary<string, float[]>(), 3);
        var service = new MemoryEmbeddingService(generator, Identity);

        Assert.Null(await service.TryGenerateAsync("   "));
        Assert.Equal(0, generator.GenerateCallCount);
    }

    [Fact]
    public async Task TryGenerateAsync_PropagatesCancellation()
    {
        var service = new MemoryEmbeddingService(
            new StubEmbeddingGenerator(new Dictionary<string, float[]>(), 3, new OperationCanceledException()),
            Identity);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.TryGenerateAsync("hello"));
    }

    [Fact]
    public void Constructor_RejectsGeneratorWithoutIdentity()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new MemoryEmbeddingService(new StubEmbeddingGenerator(new Dictionary<string, float[]>(), 3), null));
    }
}
