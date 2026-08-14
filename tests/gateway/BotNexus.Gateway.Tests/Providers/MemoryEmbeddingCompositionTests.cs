using System.IO.Abstractions;
using System.Net;
using System.Net.Http;
using System.Text;
using BotNexus.Agent.Providers.Core.Embeddings;
using BotNexus.Agent.Providers.OpenAICompat;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Providers;
using BotNexus.Memory;
using BotNexus.Memory.Embeddings;
using BotNexus.Memory.Models;
using Microsoft.Data.Sqlite;
using Shouldly;

using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Tests.Providers;

/// <summary>
/// Composition tests for the memory embedding backend (#2855).
/// </summary>
/// <remarks>
/// This is the only place the provider stack and <c>BotNexus.Memory</c> are both in scope, so it
/// is where the acceptance criteria that span both are proved: identity separation across hosted
/// models (4), degradation to lexical-only under a failing endpoint (5), and the preservation of
/// today's disabled behaviour (6).
/// </remarks>
public sealed class MemoryEmbeddingCompositionTests : IAsyncLifetime
{
    private string _tempDirectory = string.Empty;
    private string _dbPath = string.Empty;

    public Task InitializeAsync()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "botnexus-embed-composition", Guid.NewGuid().ToString("N"));
        _dbPath = Path.Combine(_tempDirectory, "memory.sqlite");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDirectory))
        {
            try { Directory.Delete(_tempDirectory, true); } catch (IOException) { }
        }

        return Task.CompletedTask;
    }

    /// <summary>A handler that always fails: the embeddings endpoint is down.</summary>
    private sealed class FaultInjectingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            throw new HttpRequestException("connection refused");
        }
    }

    /// <summary>A handler serving a real OpenAI-compatible embeddings response.</summary>
    private sealed class VectorHandler(float[] vector) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var components = string.Join(',', vector.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            var body = $$"""{"object":"list","data":[{"object":"embedding","index":0,"embedding":[{{components}}]}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static MemoryEmbeddingsConfig Config(
        bool enabled = true,
        string? provider = "ollama",
        string? model = "nomic-embed-text",
        string? baseUrl = "http://localhost:11434/v1",
        int dimensions = 3,
        string? backend = null)
        => new()
        {
            Backend = backend,
            Enabled = enabled,
            Provider = provider,
            Model = model,
            BaseUrl = baseUrl,
            Dimensions = dimensions,
        };

    private static EmbeddingProviderRegistry RegistryWith(HttpMessageHandler handler, string providerKey = "ollama", int dimensions = 3)
    {
        var registry = new EmbeddingProviderRegistry();
        registry.Register(new OpenAICompatEmbeddingProvider(
            new HttpClient(handler),
            providerKey,
            "http://localhost:11434/v1",
            [new EmbeddingModelDescriptor("nomic-embed-text", dimensions)]));
        return registry;
    }

    private static MemoryEntry Entry(string id, string content) => new()
    {
        Id = id,
        AgentId = "agent",
        SourceType = "manual",
        Content = content,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    // ---- #2790: the backend selection ladder ----

    [Fact]
    public void Build_ReturnsTheDisabledSingleton_WhenBackendIsExplicitlyNone()
    {
        // #2790 AC2: no backend is constructed for 'none', even with a complete endpoint section
        // and the legacy toggle on. If this reddens, explicit selection is not being honoured.
        MemoryEmbeddingComposition
            .Build(Config(enabled: true, backend: "none"), RegistryWith(new VectorHandler([1f, 2f, 3f])))
            .ShouldBeSameAs(MemoryEmbeddingService.Disabled);
    }

    [Fact]
    public async Task Build_DegradesToLexicalOnly_WhenTheLocalBackendHasNoRuntime()
    {
        // #2790 AC4 + AC7: 'local' is selectable and documented, but this build vendors no ONNX
        // runtime. An unsatisfiable backend must degrade exactly like a broken one - it must not
        // throw, and memory writes and searches must keep working.
        var service = MemoryEmbeddingComposition
            .Build(Config(enabled: true, backend: "local"), RegistryWith(new VectorHandler([1f, 2f, 3f])));

        service.ShouldBeSameAs(MemoryEmbeddingService.Disabled);
        service.ActiveIdentity.ShouldBeNull();

        await using var store = new SqliteMemoryStore(_dbPath, new FileSystem(), null, service);
        await store.InitializeAsync();
        var inserted = await store.InsertAsync(Entry("m1", "the quick brown fox jumps"));
        inserted.Embedding.ShouldBeNull();
        (await store.SearchAsync("quick brown fox")).Select(r => r.Id).ShouldContain("m1");
    }

    [Fact]
    public async Task Build_DegradesToLexicalOnly_WhenTheBackendTokenIsUnrecognised()
    {
        // #2790 AC4: a typo is a failure to resolve a backend, and every failure to resolve
        // degrades rather than failing startup.
        var service = MemoryEmbeddingComposition
            .Build(Config(enabled: true, backend: "aws-bedrock"), RegistryWith(new VectorHandler([1f, 2f, 3f])));

        service.ShouldBeSameAs(MemoryEmbeddingService.Disabled);

        await using var store = new SqliteMemoryStore(_dbPath, new FileSystem(), null, service);
        await store.InitializeAsync();
        (await store.InsertAsync(Entry("m1", "hello world"))).Embedding.ShouldBeNull();
    }

    [Fact]
    public async Task Build_ConstructsTheProviderBackend_WhenSelectedExplicitlyWithoutTheLegacyToggle()
    {
        // #2790 AC2/AC3: the discriminator alone reaches the backend, and the credentials come
        // from the already-registered provider - no second credential block is consulted.
        var handler = new VectorHandler([0.25f, -0.5f, 0.75f]);
        var service = MemoryEmbeddingComposition
            .Build(Config(enabled: false, backend: "provider"), RegistryWith(handler));

        service.ActiveIdentity.ShouldNotBeNull();
        var generated = await service.TryGenerateAsync("hello");
        generated.ShouldNotBeNull();
        generated!.Value.Vector.ShouldBe([0.25f, -0.5f, 0.75f]);
        handler.CallCount.ShouldBe(1);
    }

    [Fact]
    public void Build_PreservesThePreDiscriminatorBehaviour_WhenBackendIsUnspecified()
    {
        // Backward compatibility: a configuration written before #2790 must resolve to the same
        // backend it did then.
        MemoryEmbeddingComposition
            .Build(Config(enabled: true, backend: null), RegistryWith(new VectorHandler([1f, 2f, 3f])))
            .ActiveIdentity.ShouldNotBeNull();

        MemoryEmbeddingComposition
            .Build(Config(enabled: false, backend: null), RegistryWith(new VectorHandler([1f, 2f, 3f])))
            .ShouldBeSameAs(MemoryEmbeddingService.Disabled);
    }

    [Fact]
    public void ProviderBackendIdentity_EncodesTheBackend_NotJustTheModelName()
    {
        // #2790 AC5: the backend participates in the fingerprint, so a vector from a hypothetical
        // local build of a model can never be compared with a hosted one of the same name, width
        // and endpoint. Proved by showing the composed fingerprint is NOT the model-name-and-
        // provider-key-only derivation that #2855 used.
        var identity = MemoryEmbeddingComposition
            .Build(Config(enabled: true, backend: "provider"), RegistryWith(new VectorHandler([1f, 2f, 3f])))
            .ActiveIdentity;

        identity.ShouldNotBeNull();

        var backendAgnostic = HostedEmbeddingFingerprint.Derive(
            "ollama", "http://localhost:11434/v1", "nomic-embed-text", 3);
        identity!.ModelFingerprint.ShouldNotBe(
            backendAgnostic,
            "the backend must be part of the fingerprint material, otherwise two backends serving the same model name would share an identity");

        var otherBackend = HostedEmbeddingFingerprint.Derive(
            "Local:ollama", "http://localhost:11434/v1", "nomic-embed-text", 3);
        identity.ModelFingerprint.ShouldNotBe(otherBackend);
        identity.Matches(new EmbeddingIdentity("nomic-embed-text", otherBackend, 3)).ShouldBeFalse();
    }

    // ---- AC6: absent or disabled preserves today's Disabled behaviour EXACTLY ----

    [Fact]
    public void Build_ReturnsTheDisabledSingleton_WhenConfigIsAbsent()
    {
        MemoryEmbeddingComposition.Build(null, new EmbeddingProviderRegistry())
            .ShouldBeSameAs(MemoryEmbeddingService.Disabled);
    }

    [Fact]
    public void Build_ReturnsTheDisabledSingleton_WhenEmbeddingsAreDisabled()
    {
        MemoryEmbeddingComposition.Build(Config(enabled: false), new EmbeddingProviderRegistry())
            .ShouldBeSameAs(MemoryEmbeddingService.Disabled);
    }

    [Theory]
    [InlineData(null, "model", "http://x", 3)]
    [InlineData("ollama", null, "http://x", 3)]
    [InlineData("ollama", "model", null, 3)]
    [InlineData("ollama", "model", "http://x", 0)]
    public void Build_ReturnsTheDisabledSingleton_WhenConfigIsIncomplete(
        string? provider, string? model, string? baseUrl, int dimensions)
    {
        // A half-configured section must degrade, not throw: an operator mid-way through setup
        // should get a working gateway with lexical-only retrieval, not one that refuses to boot.
        MemoryEmbeddingComposition
            .Build(Config(provider: provider, model: model, baseUrl: baseUrl, dimensions: dimensions), new EmbeddingProviderRegistry())
            .ShouldBeSameAs(MemoryEmbeddingService.Disabled);
    }

    // ---- AC1 / AC7: a provider without the capability resolves absent, never throws ----

    [Fact]
    public void Build_ReturnsTheDisabledSingleton_WhenProviderDoesNotExposeEmbeddings()
    {
        MemoryEmbeddingComposition.Build(Config(provider: "anthropic"), RegistryWith(new VectorHandler([1f, 2f, 3f])))
            .ShouldBeSameAs(MemoryEmbeddingService.Disabled);
    }

    [Fact]
    public void Build_ReturnsTheDisabledSingleton_WhenNoRegistryIsAvailable()
    {
        MemoryEmbeddingComposition.Build(Config(), registry: null)
            .ShouldBeSameAs(MemoryEmbeddingService.Disabled);
    }

    // ---- Happy path: an active identity and a real vector, end to end over HTTP ----

    [Fact]
    public async Task Build_ProducesAStampedVector_OverARealEndpointShape()
    {
        var handler = new VectorHandler([0.25f, -0.5f, 0.75f]);
        var service = MemoryEmbeddingComposition.Build(Config(), RegistryWith(handler));

        var identity = service.ActiveIdentity;
        identity.ShouldNotBeNull();
        identity!.ModelId.ShouldBe("nomic-embed-text");
        identity.Dimensions.ShouldBe(3);
        identity.ModelFingerprint.ShouldNotBeNullOrWhiteSpace();

        var generated = await service.TryGenerateAsync("hello");
        generated.ShouldNotBeNull();
        generated!.Value.Vector.ShouldBe([0.25f, -0.5f, 0.75f]);
        generated.Value.Identity.ShouldBe(identity);
        handler.CallCount.ShouldBe(1);
    }

    // ---- AC4 + AC8: two different hosted models never compare ----

    [Fact]
    public void Identities_FromTwoDifferentHostedModels_DoNotMatch()
    {
        // AC8 non-vacuity: this clause fails if the width/fingerprint check is removed from
        // EmbeddingIdentity.Matches, because these two identities differ ONLY in the fields that
        // check inspects - the model id and dimensions flow into the fingerprint too.
        var small = MemoryEmbeddingComposition
            .Build(Config(model: "text-embedding-3-small", dimensions: 3), RegistryWith(new VectorHandler([1f, 2f, 3f])))
            .ActiveIdentity;

        var large = MemoryEmbeddingComposition
            .Build(Config(model: "text-embedding-3-large", dimensions: 5), RegistryWith(new VectorHandler([1f, 2f, 3f, 4f, 5f]), dimensions: 5))
            .ActiveIdentity;

        small.ShouldNotBeNull();
        large.ShouldNotBeNull();

        small!.ModelFingerprint.ShouldNotBe(large!.ModelFingerprint);
        small.Matches(large).ShouldBeFalse();
        large.Matches(small).ShouldBeFalse();
        small.Matches(small).ShouldBeTrue();
    }

    [Fact]
    public void Identities_FromTheSameModelOnDifferentEndpoints_DoNotMatch()
    {
        // Same model name, different deployment. Comparing across them is numerically well-formed
        // and semantically meaningless, so the identities must differ.
        var localRegistry = new EmbeddingProviderRegistry();
        localRegistry.Register(new OpenAICompatEmbeddingProvider(
            new HttpClient(new VectorHandler([1f, 2f, 3f])), "openai", "http://localhost:11434/v1",
            [new EmbeddingModelDescriptor("shared-model", 3)]));

        var hostedRegistry = new EmbeddingProviderRegistry();
        hostedRegistry.Register(new OpenAICompatEmbeddingProvider(
            new HttpClient(new VectorHandler([1f, 2f, 3f])), "openai", "https://api.openai.com/v1",
            [new EmbeddingModelDescriptor("shared-model", 3)]));

        var local = MemoryEmbeddingComposition
            .Build(Config(provider: "openai", model: "shared-model", baseUrl: "http://localhost:11434/v1"), localRegistry)
            .ActiveIdentity;
        var hosted = MemoryEmbeddingComposition
            .Build(Config(provider: "openai", model: "shared-model", baseUrl: "https://api.openai.com/v1"), hostedRegistry)
            .ActiveIdentity;

        local!.Matches(hosted).ShouldBeFalse();
    }

    // ---- AC5: a failing endpoint degrades to lexical-only without failing write or search ----

    [Fact]
    public async Task FailingEndpoint_DoesNotFailAMemoryWriteOrSearch()
    {
        var handler = new FaultInjectingHandler();
        var service = MemoryEmbeddingComposition.Build(Config(), RegistryWith(handler));

        // The service is ACTIVE - this is not the disabled path. The endpoint is simply broken.
        service.ActiveIdentity.ShouldNotBeNull();

        await using var store = new SqliteMemoryStore(_dbPath, new FileSystem(), null, service);
        await store.InitializeAsync();

        var inserted = await store.InsertAsync(Entry("m1", "the quick brown fox jumps"));
        inserted.Id.ShouldBe("m1");
        // No vector could be produced, so the row is stored lexical-only rather than rejected.
        inserted.Embedding.ShouldBeNull();

        var results = await store.SearchAsync("quick brown fox");
        results.Select(r => r.Id).ShouldContain("m1");

        // Proof the failure was real and reached the transport on both the write and the search
        // path, rather than the test having silently taken a disabled short-circuit.
        handler.CallCount.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task FailingEndpoint_LeavesSearchResultsIdenticalToTheDisabledBackend()
    {
        // The strongest form of "degrades to lexical-only": the ranked ids are the same as those
        // produced with no embedding backend at all.
        var faultingStorePath = Path.Combine(_tempDirectory, "faulting.sqlite");
        var disabledStorePath = Path.Combine(_tempDirectory, "disabled.sqlite");
        Directory.CreateDirectory(_tempDirectory);

        var handler = new FaultInjectingHandler();

        await using var faulting = new SqliteMemoryStore(
            faultingStorePath, new FileSystem(), null, MemoryEmbeddingComposition.Build(Config(), RegistryWith(handler)));
        await using var disabled = new SqliteMemoryStore(
            disabledStorePath, new FileSystem(), null, MemoryEmbeddingService.Disabled);

        foreach (var store in new[] { faulting, disabled })
        {
            await store.InitializeAsync();
            await store.InsertAsync(Entry("m1", "the quick brown fox jumps"));
            await store.InsertAsync(Entry("m2", "a lazy dog sleeps all day"));
            await store.InsertAsync(Entry("m3", "quick foxes are brown"));
        }

        var faultingIds = (await faulting.SearchAsync("quick brown")).Select(r => r.Id).ToList();
        var disabledIds = (await disabled.SearchAsync("quick brown")).Select(r => r.Id).ToList();

        faultingIds.ShouldBe(disabledIds);
        faultingIds.ShouldNotBeEmpty();
        handler.CallCount.ShouldBeGreaterThan(0);
    }

    // ---- The composed factory keeps the memory project untouched ----

    [Fact]
    public async Task EmbeddingAwareFactory_BuildsStoresCarryingTheComposedService()
    {
        var handler = new VectorHandler([0.25f, -0.5f, 0.75f]);
        var service = MemoryEmbeddingComposition.Build(Config(), RegistryWith(handler));

        await using var factory = new EmbeddingAwareMemoryStoreFactory(
            _ => _dbPath, service, new FileSystem());

        var store = factory.Create(AgentId.From("agent"));
        factory.Create(AgentId.From("agent")).ShouldBeSameAs(store, "the factory must cache one store per agent");

        await store.InitializeAsync();
        var inserted = await store.InsertAsync(Entry("m1", "hello world"));

        // A vector was produced and stored: the factory really did hand the store the service.
        inserted.Embedding.ShouldNotBeNull();
        handler.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task EmbeddingAwareFactory_WithDisabledService_StoresNoVector()
    {
        await using var factory = new EmbeddingAwareMemoryStoreFactory(
            _ => _dbPath, MemoryEmbeddingService.Disabled, new FileSystem());

        var store = factory.Create(AgentId.From("agent"));
        await store.InitializeAsync();

        (await store.InsertAsync(Entry("m1", "hello world"))).Embedding.ShouldBeNull();
    }

    [Fact]
    public async Task EmbeddingAwareFactory_ReportsStoreLocationExistenceLikeTheMemoryFactory()
    {
        // Delegated, not reimplemented - the parity assertion is what keeps the two from drifting.
        await using var composed = new EmbeddingAwareMemoryStoreFactory(
            _ => _dbPath, MemoryEmbeddingService.Disabled, new FileSystem());
        await using var original = new MemoryStoreFactory(_ => _dbPath, new FileSystem());

        composed.StoreLocationExists(AgentId.From("agent")).ShouldBe(original.StoreLocationExists(AgentId.From("agent")));

        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);

        composed.StoreLocationExists(AgentId.From("agent")).ShouldBe(original.StoreLocationExists(AgentId.From("agent")));
        composed.StoreLocationExists(AgentId.From("agent")).ShouldBeTrue();
    }
}
