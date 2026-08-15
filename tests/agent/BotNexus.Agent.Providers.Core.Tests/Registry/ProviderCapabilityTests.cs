using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using Moq;

namespace BotNexus.Agent.Providers.Core.Tests.Registry;

/// <summary>
/// Covers the #2853 capability vocabulary: an enum declared per registration plus a registry
/// lookup filtered on it. Vocabulary only -- there is no execution interface behind
/// <see cref="ProviderCapability.Embeddings"/>, so these assert declaration and lookup, and that
/// declaring a non-chat capability does not disturb chat resolution.
/// </summary>
public class ProviderCapabilityTests : IDisposable
{
    private readonly ApiProviderRegistry _registry = new();

    public ProviderCapabilityTests() => _registry.Clear();

    public void Dispose()
    {
        _registry.Clear();
        GC.SuppressFinalize(this);
    }

    private static Mock<IApiProvider> CreateMockProvider(string api)
    {
        var mock = new Mock<IApiProvider>();
        mock.Setup(p => p.Api).Returns(api);
        mock.Setup(p => p.Stream(It.IsAny<LlmModel>(), It.IsAny<Context>(), It.IsAny<StreamOptions?>()))
            .Returns(new LlmStream());
        return mock;
    }

    [Fact]
    public void ProviderCapability_HasChatAndEmbeddingsMembers()
    {
        Enum.GetNames<ProviderCapability>().ShouldBe(["Chat", "Embeddings"], ignoreOrder: true);
    }

    [Fact]
    public void Register_WithoutDeclaringCapabilities_DeclaresChatOnly()
    {
        var mock = CreateMockProvider("legacy-api");

        _registry.Register(mock.Object);

        var declared = _registry.GetCapabilities("legacy-api");
        declared.ShouldNotBeNull();
        declared!.ShouldBe([ProviderCapability.Chat], ignoreOrder: true);
        _registry.GetByCapability(ProviderCapability.Chat)
            .Select(p => p.Api).ShouldContain("legacy-api");
    }

    [Fact]
    public void GetCapabilities_UnregisteredApi_ReturnsNull()
    {
        _registry.GetCapabilities("nonexistent").ShouldBeNull();
    }

    /// <summary>
    /// Clause 4 of #2853. An embeddings-only registration must appear in the Embeddings lookup,
    /// must NOT appear in the Chat lookup, and must remain resolvable and streamable through the
    /// ordinary chat path -- the declaration is descriptive, not a gate.
    /// </summary>
    [Fact]
    public void EmbeddingsOnlyProvider_IsReturnedByEmbeddingsLookupOnly_AndChatResolutionDoesNotThrow()
    {
        var chatProvider = CreateMockProvider("chat-api");
        var embeddingsProvider = CreateMockProvider("embeddings-api");

        _registry.Register(chatProvider.Object);
        _registry.Register(
            embeddingsProvider.Object,
            sourceId: null,
            capabilities: new HashSet<ProviderCapability> { ProviderCapability.Embeddings });

        var embeddings = _registry.GetByCapability(ProviderCapability.Embeddings).Select(p => p.Api).ToList();
        embeddings.ShouldBe(["embeddings-api"]);

        var chat = _registry.GetByCapability(ProviderCapability.Chat).Select(p => p.Api).ToList();
        chat.ShouldBe(["chat-api"]);
        chat.ShouldNotContain("embeddings-api");

        var resolved = _registry.Get("embeddings-api");
        resolved.ShouldNotBeNull();
        var act = () => resolved!.Stream(MakeModel("embeddings-api"), new Context(null, []));
        act.ShouldNotThrow();
    }

    [Fact]
    public void Register_DeclaringBothCapabilities_AppearsInBothLookups()
    {
        var mock = CreateMockProvider("dual-api");

        _registry.Register(
            mock.Object,
            sourceId: null,
            capabilities: new HashSet<ProviderCapability> { ProviderCapability.Chat, ProviderCapability.Embeddings });

        _registry.GetByCapability(ProviderCapability.Chat).Select(p => p.Api).ShouldContain("dual-api");
        _registry.GetByCapability(ProviderCapability.Embeddings).Select(p => p.Api).ShouldContain("dual-api");
    }

    [Fact]
    public void Unregister_RemovesProviderFromCapabilityLookup()
    {
        var mock = CreateMockProvider("temp-api");

        _registry.Register(
            mock.Object,
            sourceId: "source-1",
            capabilities: new HashSet<ProviderCapability> { ProviderCapability.Embeddings });
        _registry.GetByCapability(ProviderCapability.Embeddings).ShouldNotBeEmpty();

        _registry.Unregister("source-1");

        _registry.GetByCapability(ProviderCapability.Embeddings).ShouldBeEmpty();
    }

    private static LlmModel MakeModel(string api) => new(
        Id: "test-model",
        Name: "Test Model",
        Api: api,
        Provider: "test",
        BaseUrl: "https://example.com",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 8192,
        MaxTokens: 2048
    );
}
