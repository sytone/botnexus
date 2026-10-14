using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Providers;
using BotNexus.Memory;
using BotNexus.Memory.Embeddings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace BotNexus.Gateway.Tests.Providers;

public sealed class MemoryReembeddingWorkerTests
{
    private static readonly EmbeddingIdentity Target = new("model", "fingerprint", 2);

    [Fact]
    public async Task ProcessPass_NoActiveIdentity_DoesNotInspectAgentsOrStores()
    {
        var registry = Substitute.For<IAgentRegistry>();
        var factory = Substitute.For<IMemoryStoreFactory>();
        var embeddings = new StubEmbeddingService(null, _ => throw new InvalidOperationException());
        var worker = CreateWorker(registry, factory, embeddings);

        await worker.ProcessPassAsync(CancellationToken.None);

        registry.DidNotReceive().GetAll();
        factory.DidNotReceiveWithAnyArgs().StoreLocationExists(AgentId.From("unused"));
        factory.DidNotReceiveWithAnyArgs().Create(AgentId.From("unused"));
        embeddings.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task ProcessPass_BoundsAndOrdersAgentsAndProcessesItemsSequentially()
    {
        var agents = new[] { Agent("c"), Agent("a"), Agent("b") };
        var registry = Substitute.For<IAgentRegistry>();
        registry.GetAll().Returns(agents);
        var store = Substitute.For<IMemoryStore>();
        store.EnsureReembeddingJobAsync(Target, Arg.Any<CancellationToken>())
            .Returns(new ReembeddingJob("job", Target, ReembeddingJobState.Running, 2, 0, 0, 0, null));
        store.ClaimReembeddingBatchAsync("job", 2, Arg.Any<CancellationToken>())
            .Returns(new[] { new ReembeddingItem("one", "first", 0, 3, "token-one"), new ReembeddingItem("two", "second", 0, 4, "token-two") });
        var probed = new List<string>();
        var factory = Substitute.For<IMemoryStoreFactory>();
        factory.StoreLocationExists(AgentId.From("unused")).ReturnsForAnyArgs(call => { probed.Add(call.Arg<AgentId>().Value); return true; });
        factory.Create(AgentId.From("unused")).ReturnsForAnyArgs(store);
        var inFlight = 0;
        var maxInFlight = 0;
        var embeddings = new StubEmbeddingService(Target, async text =>
        {
            maxInFlight = Math.Max(maxInFlight, Interlocked.Increment(ref inFlight));
            await Task.Yield();
            Interlocked.Decrement(ref inFlight);
            return (Target, text == "first" ? new[] { 1f, 2f } : new[] { 3f, 4f });
        });
        var worker = CreateWorker(registry, factory, embeddings, agentBatchSize: 2, itemBatchSize: 2);

        await worker.ProcessPassAsync(CancellationToken.None);

        probed.ShouldBe(new[] { "a", "b" });
        embeddings.Contents.ShouldBe(new[] { "first", "second", "first", "second" });
        maxInFlight.ShouldBe(1);
        await store.Received(2).EnsureReembeddingJobAsync(Target, Arg.Any<CancellationToken>());
        await store.Received(2).ClaimReembeddingBatchAsync("job", 2, Arg.Any<CancellationToken>());
        await store.Received(2).CompleteReembeddingItemAsync("job", "one", 3, "token-one", Arg.Is<byte[]>(blob => BlobMatches(blob, Target, new[] { 1f, 2f })), Arg.Any<CancellationToken>());
        await store.Received(2).CompleteReembeddingItemAsync("job", "two", 4, "token-two", Arg.Is<byte[]>(blob => BlobMatches(blob, Target, new[] { 3f, 4f })), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessPass_AdvancesDeterministicAgentBatchBetweenPasses()
    {
        var registry = RegistryWith("d", "b", "a", "c");
        var store = Substitute.For<IMemoryStore>();
        store.EnsureReembeddingJobAsync(Target, Arg.Any<CancellationToken>())
            .Returns(new ReembeddingJob("job", Target, ReembeddingJobState.Running, 0, 0, 0, 0, null));
        store.ClaimReembeddingBatchAsync("job", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        var probed = new List<string>();
        var factory = Substitute.For<IMemoryStoreFactory>();
        factory.StoreLocationExists(AgentId.From("unused")).ReturnsForAnyArgs(call => { probed.Add(call.Arg<AgentId>().Value); return true; });
        factory.Create(AgentId.From("unused")).ReturnsForAnyArgs(store);
        var worker = CreateWorker(registry, factory, new StubEmbeddingService(Target, _ => throw new InvalidOperationException()), agentBatchSize: 2);

        await worker.ProcessPassAsync(CancellationToken.None);
        await worker.ProcessPassAsync(CancellationToken.None);

        probed.ShouldBe(new[] { "a", "b", "c", "d" });
    }

    [Fact]
    public async Task ProcessPass_MismatchedIdentity_PersistsActionableFailure()
    {
        var store = StoreWithSingleClaim();
        var factory = FactoryFor(store);
        var embeddings = new StubEmbeddingService(Target, _ => Task.FromResult<(EmbeddingIdentity, float[])?>(
            (new EmbeddingIdentity("other", "fingerprint", 2), new[] { 1f, 2f })));

        await CreateWorker(RegistryWith("a"), factory, embeddings).ProcessPassAsync(CancellationToken.None);

        await store.DidNotReceiveWithAnyArgs().CompleteReembeddingItemAsync(default!, default!, default, default!, default!, default);
        await store.Received(1).FailReembeddingItemAsync("job", "item", 1, "token", Arg.Is<string>(message => message.Contains("identity", StringComparison.OrdinalIgnoreCase)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessPass_HostCancellation_PropagatesWithoutPersistingFailure()
    {
        var store = StoreWithSingleClaim();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var embeddings = new StubEmbeddingService(Target, _ => throw new OperationCanceledException(cancellation.Token));
        var worker = CreateWorker(RegistryWith("a"), FactoryFor(store), embeddings);

        Func<Task> act = () => worker.ProcessPassAsync(cancellation.Token);
        await Should.ThrowAsync<OperationCanceledException>(act);
        await store.DidNotReceiveWithAnyArgs().FailReembeddingItemAsync(default!, default!, default, default!, default!, default);
    }

    [Fact]
    public async Task ProcessPass_ProviderTimeout_IsPersistedAndDoesNotStopThePass()
    {
        var store = StoreWithSingleClaim();
        var embeddings = new StubEmbeddingService(Target, _ => throw new TaskCanceledException("provider timeout"));
        var worker = CreateWorker(RegistryWith("a"), FactoryFor(store), embeddings);

        await worker.ProcessPassAsync(CancellationToken.None);

        await store.Received(1).FailReembeddingItemAsync(
            "job", "item", 1, "token",
            Arg.Is<string>(message => message.Contains("TaskCanceledException", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    private static MemoryReembeddingWorker CreateWorker(IAgentRegistry registry, IMemoryStoreFactory factory, IMemoryEmbeddingService embeddings, int agentBatchSize = 8, int itemBatchSize = 2)
        => new(registry, factory, embeddings, Options.Create(new MemoryReembeddingOptions
        {
            AgentBatchSize = agentBatchSize,
            ItemBatchSize = itemBatchSize,
            PassDelay = TimeSpan.FromSeconds(1),
            ItemYieldDelay = TimeSpan.FromMilliseconds(1)
        }), NullLogger<MemoryReembeddingWorker>.Instance, (_, _) => Task.CompletedTask);

    private static IAgentRegistry RegistryWith(params string[] ids)
    {
        var registry = Substitute.For<IAgentRegistry>();
        registry.GetAll().Returns(ids.Select(Agent).ToArray());
        return registry;
    }

    private static IMemoryStoreFactory FactoryFor(IMemoryStore store)
    {
        var factory = Substitute.For<IMemoryStoreFactory>();
        factory.StoreLocationExists(AgentId.From("unused")).ReturnsForAnyArgs(true);
        factory.Create(AgentId.From("unused")).ReturnsForAnyArgs(store);
        return factory;
    }

    private static IMemoryStore StoreWithSingleClaim()
    {
        var store = Substitute.For<IMemoryStore>();
        store.EnsureReembeddingJobAsync(Target, Arg.Any<CancellationToken>())
            .Returns(new ReembeddingJob("job", Target, ReembeddingJobState.Running, 1, 0, 0, 0, null));
        store.ClaimReembeddingBatchAsync("job", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new ReembeddingItem("item", "content", 0, 1, "token") });
        return store;
    }

    private static bool BlobMatches(byte[] blob, EmbeddingIdentity expectedIdentity, float[] expectedVector)
        => EmbeddingBlob.TryDecode(blob, out var identity, out var vector)
            && expectedIdentity.Matches(identity)
            && vector is not null
            && vector.SequenceEqual(expectedVector);

    private static AgentDescriptor Agent(string id) => new()
    {
        AgentId = AgentId.From(id), DisplayName = id, ModelId = "test", ApiProvider = "test"
    };

    private sealed class StubEmbeddingService(
        EmbeddingIdentity? identity,
        Func<string, Task<(EmbeddingIdentity Identity, float[] Vector)?>> generate) : IMemoryEmbeddingService
    {
        public EmbeddingIdentity? ActiveIdentity => identity;
        public int Calls { get; private set; }
        public List<string> Contents { get; } = [];

        public async Task<(EmbeddingIdentity Identity, float[] Vector)?> TryGenerateAsync(string text, CancellationToken ct = default)
        {
            Calls++;
            Contents.Add(text);
            return await generate(text);
        }
    }
}
